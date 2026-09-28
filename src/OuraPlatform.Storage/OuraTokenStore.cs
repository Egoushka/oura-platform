using System.Data;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using OuraPlatform.Oura.Auth;

namespace OuraPlatform.Storage;

/// <summary>
/// The most correctness-critical code in the repository.
/// </summary>
/// <remarks>
/// Oura's refresh token is single-use and rotates: every refresh response carries a new one and
/// invalidates the one that was spent. Spending it twice, or spending it and failing to write the
/// replacement, means the account's authorization is gone and the browser handshake has to be
/// redone by hand.
/// <para>
/// So the refresh runs entirely inside one transaction: <c>SELECT ... FOR UPDATE</c> takes the row
/// lock, the HTTP exchange happens while holding it, and both tokens are written before the commit.
/// A caller that loses the race re-reads under the lock and finds the pair already rotated, so the
/// old token is never presented twice.
/// </para>
/// <para>
/// If the exchange succeeds but the write does not, the process is holding a token the database
/// does not know about while Oura has already killed the old one. There is no safe way to carry on,
/// so the store latches: <see cref="OuraTokenPersistenceException"/> is thrown and every later call
/// throws it again without touching the network. Both hosts treat it as fatal.
/// </para>
/// </remarks>
public sealed class OuraTokenStore : IOuraTokenStore
{
    private const string Provider = "oura";

    private readonly NpgsqlDataSource _dataSource;
    private readonly IOuraOAuthClient _oauth;
    private readonly TimeProvider _time;
    private readonly ILogger<OuraTokenStore> _logger;

    private OuraTokenPersistenceException? _latched;

    public OuraTokenStore(
        NpgsqlDataSource dataSource,
        IOuraOAuthClient oauth,
        TimeProvider time,
        ILogger<OuraTokenStore> logger)
    {
        _dataSource = dataSource;
        _oauth = oauth;
        _time = time;
        _logger = logger;
    }

    public async Task<OuraTokenSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        ThrowIfLatched();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRowAsync(connection, transaction: null, forUpdate: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OuraTokenSnapshot> SaveAsync(OuraTokenResponse response, CancellationToken cancellationToken)
    {
        ThrowIfLatched();

        var snapshot = ToSnapshot(response, existingScope: null);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(UpsertSql, ToParameters(snapshot), cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Stored a new Oura token pair; expires {ExpiresAt:o}, scope '{Scope}'.",
            snapshot.ExpiresAt,
            snapshot.Scope);

        return snapshot;
    }

    public async Task<OuraTokenSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        ThrowIfLatched();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        var current = await ReadRowAsync(connection, transaction, forUpdate: true, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "No Oura token stored. Complete the OAuth handshake at /oauth/start first.");

        // Whoever held the lock before this caller may already have rotated the pair. Spending the
        // refresh token again would be spending a token Oura has already invalidated.
        if (!current.IsStale(_time.GetUtcNow(), TimeSpan.Zero))
        {
            _logger.LogDebug("Token was rotated by another caller while waiting for the row lock; reusing it.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return current;
        }

        var exchanged = await _oauth.RefreshAsync(current.RefreshToken, cancellationToken).ConfigureAwait(false);
        var rotated = ToSnapshot(exchanged, current.Scope);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                UpsertSql, ToParameters(rotated), transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _latched = new OuraTokenPersistenceException(
                "Refreshed the Oura token pair but failed to persist it. The previous refresh token " +
                "has already been invalidated by Oura, so this process cannot recover: re-run the " +
                "OAuth handshake at /oauth/start.",
                exception);

            _logger.LogCritical(exception, "Failed to persist a rotated Oura token pair. Ingest cannot continue.");
            throw _latched;
        }

        _logger.LogInformation("Rotated the Oura token pair; expires {ExpiresAt:o}.", rotated.ExpiresAt);
        return rotated;
    }

    private void ThrowIfLatched()
    {
        if (_latched is not null)
        {
            throw new OuraTokenPersistenceException(_latched.Message, _latched.InnerException);
        }
    }

    private OuraTokenSnapshot ToSnapshot(OuraTokenResponse response, string? existingScope)
    {
        var now = _time.GetUtcNow();

        // Oura's documentation gives two different access-token lifetimes (24 hours and 30 days).
        // expires_in from the response is the only trustworthy source.
        return new OuraTokenSnapshot(
            response.AccessToken,
            response.RefreshToken,
            now.AddSeconds(response.ExpiresIn),
            response.Scope ?? existingScope ?? string.Empty,
            now);
    }

    private static async Task<OuraTokenSnapshot?> ReadRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var sql = """
            -- Aliased because Dapper matches record constructor parameters by name and does not
            -- strip underscores. Postgres folds unquoted aliases to lower case; Dapper's match is
            -- case-insensitive, so this lines up.
            select access_token  as AccessToken,
                   refresh_token as RefreshToken,
                   expires_at    as ExpiresAt,
                   scope         as Scope,
                   rotated_at    as RotatedAt
            from oauth_tokens
            where provider = @provider
            """ + (forUpdate ? "\nfor update" : string.Empty);

        var row = await connection.QuerySingleOrDefaultAsync<TokenRow>(new CommandDefinition(
            sql, new { provider = Provider }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null
            ? null
            : new OuraTokenSnapshot(
                row.AccessToken,
                row.RefreshToken,
                new DateTimeOffset(row.ExpiresAt),
                row.Scope,
                new DateTimeOffset(row.RotatedAt));
    }

    private const string UpsertSql = """
        insert into oauth_tokens (provider, access_token, refresh_token, expires_at, scope, rotated_at)
        values (@provider, @accessToken, @refreshToken, @expiresAt, @scope, @rotatedAt)
        on conflict (provider) do update set
            access_token  = excluded.access_token,
            refresh_token = excluded.refresh_token,
            expires_at    = excluded.expires_at,
            scope         = excluded.scope,
            rotated_at    = excluded.rotated_at
        """;

    private static object ToParameters(OuraTokenSnapshot snapshot) => new
    {
        provider = Provider,
        accessToken = snapshot.AccessToken,
        refreshToken = snapshot.RefreshToken,
        expiresAt = snapshot.ExpiresAt,
        scope = snapshot.Scope,
        rotatedAt = snapshot.RotatedAt,
    };

    /// <summary>Npgsql reads <c>timestamptz</c> as a UTC <see cref="DateTime"/>, not a
    /// <see cref="DateTimeOffset"/> — Postgres stores an instant and does not keep the writer's
    /// offset. The conversion happens once, in <see cref="ReadRowAsync"/>.</summary>
    private sealed record TokenRow(
        string AccessToken,
        string RefreshToken,
        DateTime ExpiresAt,
        string Scope,
        DateTime RotatedAt);
}
