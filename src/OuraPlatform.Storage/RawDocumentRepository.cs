using Dapper;
using Npgsql;
using NpgsqlTypes;
using OuraPlatform.Oura;

namespace OuraPlatform.Storage;

/// <summary>
/// Writes to <c>oura_raw</c>, the declared source of truth. Nothing here interprets a payload —
/// that is the projector's job, and a payload no model can parse must still land here.
/// </summary>
public sealed class RawDocumentRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public RawDocumentRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<int> UpsertAsync(
        IReadOnlyList<OuraRawDocument> documents,
        string specVersion,
        CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
        {
            return 0;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var written = await BulkUpsert.ExecuteAsync(
            connection,
            transaction,
            "oura_raw",
            ["doc_type", "doc_id", "day", "payload", "spec_ver", "fetched_at"],
            ["doc_type", "doc_id"],
            """
            day        = excluded.day,
            payload    = excluded.payload,
            spec_ver   = excluded.spec_ver,
            fetched_at = excluded.fetched_at
            """,
            documents,
            (importer, document) =>
            {
                importer.Write(document.DocType, NpgsqlDbType.Text);
                importer.Write(document.DocId, NpgsqlDbType.Text);
                if (document.Day is { } day)
                {
                    importer.Write(day, NpgsqlDbType.Date);
                }
                else
                {
                    importer.WriteNull();
                }

                importer.Write(document.Payload, NpgsqlDbType.Jsonb);
                importer.Write(specVersion, NpgsqlDbType.Text);
                importer.Write(DateTime.UtcNow, NpgsqlDbType.TimestampTz);
            },
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }

    /// <summary>Replays stored payloads for a collection so projections can be rebuilt without
    /// re-calling the API. This is the invariant `oura_raw` exists to serve.</summary>
    public async Task<IReadOnlyList<OuraRawDocument>> ReadAsync(
        string docType,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<RawRow>(new CommandDefinition(
            """
            select doc_type as DocType,
                   doc_id   as DocId,
                   day      as Day,
                   payload  as Payload
            from oura_raw
            where doc_type = @docType
              -- Explicit casts: a null bound carries no type, and Postgres will not infer one.
              and (@from::date is null or day is null or day >= @from::date)
              and (@to::date   is null or day is null or day <= @to::date)
            order by day nulls first, doc_id
            """,
            new { docType, from, to },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(row => new OuraRawDocument(row.DocType, row.DocId, row.Day, row.Payload)).ToArray();
    }

    private sealed record RawRow(string DocType, string DocId, DateOnly? Day, string Payload);
}
