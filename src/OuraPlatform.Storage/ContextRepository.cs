using Dapper;
using Npgsql;

namespace OuraPlatform.Storage;

/// <summary>
/// One thing that happened, from somewhere Oura cannot see.
/// </summary>
/// <param name="Source">Which importer produced it — <c>calendar:work</c>, <c>oura_tag</c>.</param>
/// <param name="SourceId">
/// Stable upstream identifier, unique within <paramref name="Source"/>. A recurring calendar event
/// shares one UID across every occurrence, so an importer must fold the occurrence's start into
/// this or the whole series collapses onto a single row.
/// </param>
/// <param name="EndsAt">Null for things with no duration — a coffee, a deploy.</param>
/// <param name="Meta">Raw jsonb, or null. Keeps whatever the source said that has no column.</param>
public sealed record ContextEvent(
    string Source,
    string SourceId,
    DateTimeOffset Ts,
    DateTimeOffset? EndsAt,
    string Kind,
    string? Label,
    string? Meta);

/// <summary>
/// Writes to <c>context</c> — the table this project exists for. Everything else here is data Oura
/// already has; this is the half it does not.
/// </summary>
public sealed class ContextRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public ContextRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// Upserts on <c>(source, source_id)</c>. Re-importing the same feed converges rather than
    /// appending: a duplicated meeting silently doubles every count it is joined into, and nothing
    /// downstream would show that it had happened.
    /// </summary>
    public async Task<int> UpsertAsync(IReadOnlyList<ContextEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return 0;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into context (source, source_id, ts, ends_at, kind, label, meta)
            values (@Source, @SourceId, @Ts, @EndsAt, @Kind, @Label, @Meta::jsonb)
            on conflict (source, source_id) where source_id is not null do update set
                ts      = excluded.ts,
                ends_at = excluded.ends_at,
                kind    = excluded.kind,
                label   = excluded.label,
                meta    = excluded.meta
            """,
            events.Select(e => new
            {
                e.Source,
                e.SourceId,
                Ts = e.Ts.ToUniversalTime(),
                EndsAt = e.EndsAt?.ToUniversalTime(),
                e.Kind,
                e.Label,
                e.Meta,
            }),
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return affected;
    }

    /// <summary>
    /// Drops rows a source no longer reports inside a window. Calendars are mutable in a way Oura
    /// documents are not — a meeting that was cancelled simply stops appearing in the feed, and
    /// without this it would stay in the warehouse forever, still being counted.
    /// </summary>
    public async Task<int> PruneMissingAsync(
        string source,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyCollection<string> keepSourceIds,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(new CommandDefinition(
            """
            delete from context
            where source = @source
              and ts >= @from
              and ts <  @to
              and source_id is not null
              and not (source_id = any(@keep))
            """,
            new
            {
                source,
                from = from.ToUniversalTime(),
                to = to.ToUniversalTime(),
                keep = keepSourceIds.ToArray(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
