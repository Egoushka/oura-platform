using Npgsql;

namespace OuraPlatform.Storage;

/// <summary>
/// COPY into a temp table, then <c>INSERT ... ON CONFLICT DO UPDATE</c> out of it.
/// </summary>
/// <remarks>
/// <c>COPY</c> cannot upsert, and ingest has to be idempotent — re-running an overlapping window
/// must converge rather than duplicate. Staging through a temp table keeps the bulk path for the
/// write that matters (a backfill expands one night of sleep into ~200 <c>sleep_series</c> rows)
/// without giving up the conflict handling.
/// </remarks>
internal static class BulkUpsert
{
    /// <param name="conflictColumns">
    /// The unique key. Used both for <c>ON CONFLICT</c> and to deduplicate the batch — see the
    /// <c>DISTINCT ON</c> below.
    /// </param>
    public static async Task<int> ExecuteAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> conflictColumns,
        string updateAssignments,
        IReadOnlyList<T> rows,
        Action<NpgsqlBinaryImporter, T> write,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        var staging = $"staging_{table}";
        var columnList = string.Join(", ", columns);
        var keyList = string.Join(", ", conflictColumns);

        await using (var create = new NpgsqlCommand(
            $"create temp table {staging} (like {table} including defaults) on commit drop", connection, transaction))
        {
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var importer = await connection
            .BeginBinaryImportAsync($"copy {staging} ({columnList}) from stdin (format binary)", cancellationToken)
            .ConfigureAwait(false))
        {
            foreach (var row in rows)
            {
                await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                write(importer, row);
            }

            await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        // DISTINCT ON is not tidiness — Postgres raises 21000 "ON CONFLICT DO UPDATE command cannot
        // affect row a second time" if the source contains two rows with the same key. That happens
        // in ordinary data: overlapping sleep periods (a nap recorded inside a rest period) produce
        // the same sleep_series timestamp twice in one batch, and heart-rate rows sharing a
        // timestamp collapse onto the same synthesised document id.
        // `ctid desc` keeps the last row copied, so the batch's own ordering decides the winner.
        await using var upsert = new NpgsqlCommand(
            $"""
             insert into {table} ({columnList})
             select {columnList}
             from (
                 select distinct on ({keyList}) {columnList}, ctid
                 from {staging}
                 order by {keyList}, ctid desc
             ) deduplicated
             on conflict ({keyList}) do update set {updateAssignments}
             """,
            connection,
            transaction);

        var affected = await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // The temp table is dropped at commit, but a caller may stage several batches into the same
        // transaction, so clear it explicitly rather than colliding on the next create.
        await using var drop = new NpgsqlCommand($"drop table {staging}", connection, transaction);
        await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return affected;
    }
}
