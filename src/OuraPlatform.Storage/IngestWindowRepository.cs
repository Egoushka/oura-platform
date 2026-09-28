using Dapper;
using Npgsql;
using OuraPlatform.Oura;

namespace OuraPlatform.Storage;

/// <summary>
/// Backfill bookkeeping. A window is recorded only once every document inside it has been written,
/// so killing the backfill mid-window costs that window and nothing else.
/// </summary>
public sealed class IngestWindowRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public IngestWindowRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlySet<DateOnly>> CompletedWindowStartsAsync(
        string docType,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var starts = await connection.QueryAsync<DateOnly>(new CommandDefinition(
            "select window_start from ingest_window where doc_type = @docType",
            new { docType },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return starts.ToHashSet();
    }

    public async Task RecordAsync(
        string docType,
        DateWindow window,
        int documentCount,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into ingest_window (doc_type, window_start, window_end, document_count)
            values (@docType, @start, @end, @documentCount)
            on conflict (doc_type, window_start) do update set
                window_end     = excluded.window_end,
                document_count = excluded.document_count,
                completed_at   = now()
            """,
            new { docType, start = window.Start, end = window.End, documentCount },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Total documents ever recorded for a collection. A backfill that finishes with zero
    /// is the signature of a missing scope, because Oura answers those with empty arrays rather
    /// than an error.</summary>
    public async Task<int> DocumentCountAsync(string docType, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "select coalesce(sum(document_count), 0) from ingest_window where doc_type = @docType",
            new { docType },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
