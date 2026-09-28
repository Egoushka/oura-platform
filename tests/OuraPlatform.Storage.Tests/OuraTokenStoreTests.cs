using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using OuraPlatform.Oura.Auth;

namespace OuraPlatform.Storage.Tests;

/// <summary>
/// The refresh token is single-use and rotates. Everything here exists because getting it wrong
/// costs the account's authorization, not a retry.
/// </summary>
[Collection(TimescaleCollection.Name)]
public sealed class OuraTokenStoreTests(TimescaleFixture timescale) : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly CountingOAuthClient _oauth = new();

    public Task InitializeAsync() => timescale.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private OuraTokenStore Store() =>
        new(timescale.DataSource, _oauth, _time, NullLogger<OuraTokenStore>.Instance);

    private static OuraTokenResponse Response(string suffix, int expiresIn = 86400) =>
        new($"access-{suffix}", $"refresh-{suffix}", expiresIn, "daily heartrate", "Bearer");

    [Fact]
    public async Task Reads_null_before_the_handshake()
    {
        Assert.Null(await Store().ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Expiry_comes_from_expires_in_not_from_a_constant()
    {
        // Oura's docs claim both 24 hours and 30 days in different places, so nothing may be
        // hardcoded — the response is the only authority.
        var stored = await Store().SaveAsync(Response("1", expiresIn: 1800), CancellationToken.None);

        Assert.Equal(_time.GetUtcNow().AddMinutes(30), stored.ExpiresAt);
    }

    [Fact]
    public async Task Refresh_persists_both_halves_of_the_new_pair()
    {
        var store = Store();
        await store.SaveAsync(Response("1", expiresIn: 60), CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));

        var rotated = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal("access-2", rotated.AccessToken);
        Assert.Equal("refresh-2", rotated.RefreshToken);

        // Re-read from the database rather than trusting the returned object: the whole point is
        // that the new refresh token is durable before the caller sees it.
        var reread = await Store().ReadAsync(CancellationToken.None);
        Assert.Equal("refresh-2", reread!.RefreshToken);
        Assert.Equal("refresh-1", _oauth.LastSpentRefreshToken);
    }

    /// <summary>
    /// The one that matters. Two callers racing to refresh must spend the single-use token exactly
    /// once; the loser takes the row lock, re-reads, and finds the pair already rotated.
    /// </summary>
    [Fact]
    public async Task Concurrent_refreshes_spend_the_refresh_token_exactly_once()
    {
        await Store().SaveAsync(Response("1", expiresIn: 60), CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));

        // Separate store instances, because two instances share nothing but the database — which is
        // exactly what the row lock has to cover.
        var first = Store();
        var second = Store();
        _oauth.HoldFor = TimeSpan.FromMilliseconds(300);

        var results = await Task.WhenAll(
            first.RefreshAsync(CancellationToken.None),
            second.RefreshAsync(CancellationToken.None));

        Assert.Equal(1, _oauth.RefreshCalls);
        Assert.Equal("refresh-2", results[0].RefreshToken);
        Assert.Equal("refresh-2", results[1].RefreshToken);
    }

    [Fact]
    public async Task A_failed_write_latches_the_store_instead_of_continuing()
    {
        var store = Store();
        await store.SaveAsync(Response("1", expiresIn: 60), CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));

        await BreakWritesAsync();

        // The exchange succeeds and Oura has already invalidated refresh-1, so there is nothing to
        // fall back to.
        await Assert.ThrowsAsync<OuraTokenPersistenceException>(() => store.RefreshAsync(CancellationToken.None));

        await RestoreWritesAsync();

        // Latched: even now that writes work again, this instance refuses to present a token pair
        // it cannot vouch for.
        await Assert.ThrowsAsync<OuraTokenPersistenceException>(() => store.ReadAsync(CancellationToken.None));
        Assert.Equal(1, _oauth.RefreshCalls);
    }

    [Fact]
    public async Task A_failed_write_leaves_the_stored_pair_untouched()
    {
        var store = Store();
        await store.SaveAsync(Response("1", expiresIn: 60), CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));

        await BreakWritesAsync();
        await Assert.ThrowsAsync<OuraTokenPersistenceException>(() => store.RefreshAsync(CancellationToken.None));
        await RestoreWritesAsync();

        // The transaction rolled back, so the row still holds the pair that was there before. It is
        // dead at Oura's end, but a half-written row would be worse: it would look usable.
        var stored = await Store().ReadAsync(CancellationToken.None);
        Assert.Equal("refresh-1", stored!.RefreshToken);
    }

    [Fact]
    public async Task Refresh_is_skipped_when_another_caller_already_rotated()
    {
        var store = Store();
        await store.SaveAsync(Response("1", expiresIn: 86400), CancellationToken.None);

        // Not stale, so there is nothing to spend.
        var result = await store.RefreshAsync(CancellationToken.None);

        Assert.Equal(0, _oauth.RefreshCalls);
        Assert.Equal("refresh-1", result.RefreshToken);
    }

    [Fact]
    public async Task Tokens_never_appear_in_a_string_representation()
    {
        var stored = await Store().SaveAsync(Response("secret"), CancellationToken.None);

        // Records print every member by default, which is one careless log statement away from
        // putting a refresh token in a log file.
        Assert.DoesNotContain("access-secret", stored.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-secret", stored.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("access-secret", Response("secret").ToString(), StringComparison.Ordinal);
    }

    private async Task BreakWritesAsync()
    {
        await using var connection = await timescale.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            create or replace function reject_token_write() returns trigger as $$
            begin raise exception 'simulated write failure'; end;
            $$ language plpgsql;

            create trigger reject_token_write before update on oauth_tokens
            for each row execute function reject_token_write();
            """);
    }

    private async Task RestoreWritesAsync()
    {
        await using var connection = await timescale.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("drop trigger if exists reject_token_write on oauth_tokens");
    }

    private sealed class CountingOAuthClient : IOuraOAuthClient
    {
        private int _issued = 1;

        public int RefreshCalls { get; private set; }

        public string? LastSpentRefreshToken { get; private set; }

        public TimeSpan HoldFor { get; set; } = TimeSpan.Zero;

        public Uri BuildAuthorizationUri(string state) => new($"https://example.invalid/authorize?state={state}");

        public Task<OuraTokenResponse> ExchangeCodeAsync(string code, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<OuraTokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            RefreshCalls++;
            LastSpentRefreshToken = refreshToken;

            if (HoldFor > TimeSpan.Zero)
            {
                await Task.Delay(HoldFor, cancellationToken);
            }

            var suffix = ++_issued;
            return new OuraTokenResponse($"access-{suffix}", $"refresh-{suffix}", 86400, "daily heartrate", "Bearer");
        }
    }
}
