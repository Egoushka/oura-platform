using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OuraPlatform.Oura;
using OuraPlatform.Storage;

namespace OuraPlatform.Ingest;

/// <summary>
/// Fetches one window of one collection, writes it to <c>oura_raw</c>, then projects it.
/// </summary>
/// <remarks>
/// Raw first, always. If the projection throws, the payload is already durable and the projection
/// can be replayed from the database without touching the API again — the invariant the whole
/// schema is built around.
/// </remarks>
public sealed class IngestPipeline
{
    private readonly IOuraApiClient _api;
    private readonly RawDocumentRepository _raw;
    private readonly DocumentProjector _projector;
    private readonly IngestWindowRepository _windows;
    private readonly OuraOptions _options;
    private readonly ILogger<IngestPipeline> _logger;

    public IngestPipeline(
        IOuraApiClient api,
        RawDocumentRepository raw,
        DocumentProjector projector,
        IngestWindowRepository windows,
        IOptions<OuraOptions> options,
        ILogger<IngestPipeline> logger)
    {
        _api = api;
        _raw = raw;
        _projector = projector;
        _windows = windows;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Ingests one window. Returns the document count.</summary>
    /// <param name="recordProgress">
    /// Backfill bookkeeping. Only the backfill sets this: its windows are the ones
    /// <c>ingest_window</c> describes, and a reconcile window that happened to share a start date
    /// would otherwise shrink a completed month to a week and make the backfill skip the rest of it.
    /// </param>
    public async Task<int> IngestWindowAsync(
        OuraCollection collection,
        DateWindow window,
        bool recordProgress,
        CancellationToken cancellationToken)
    {
        var documents = new List<OuraRawDocument>();
        await foreach (var document in _api.FetchAsync(collection, window, cancellationToken))
        {
            documents.Add(document);
        }

        if (documents.Count > 0)
        {
            await _raw.UpsertAsync(documents, _options.SpecVersion, cancellationToken).ConfigureAwait(false);
            await _projector.ProjectAsync(collection, documents, cancellationToken).ConfigureAwait(false);
        }

        if (recordProgress)
        {
            // Recorded only after every document is durable, so a run killed mid-window redoes that
            // window and nothing else.
            await _windows.RecordAsync(collection.Name, window, documents.Count, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("{Collection} {Window}: {Count} document(s).", collection.Name, window, documents.Count);
        return documents.Count;
    }

    /// <summary>
    /// Re-reads stored payloads and rebuilds the typed tables, without calling the API. This is what
    /// makes <c>oura_raw</c> the source of truth rather than a debugging aid.
    /// </summary>
    public async Task<int> ReprojectAsync(
        OuraCollection collection,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken)
    {
        var stored = await _raw.ReadAsync(collection.Name, from, to, cancellationToken).ConfigureAwait(false);
        var projected = await _projector.ProjectAsync(collection, stored, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Re-projected {Count} stored {Collection} document(s) into {Rows} row(s).",
            stored.Count, collection.Name, projected);

        return projected;
    }
}
