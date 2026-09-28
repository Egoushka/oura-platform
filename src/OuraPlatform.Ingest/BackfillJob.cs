using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OuraPlatform.Oura;
using OuraPlatform.Oura.Auth;
using OuraPlatform.Storage;

namespace OuraPlatform.Ingest;

/// <summary>
/// Walks every collection from <see cref="OuraOptions.BackfillFrom"/> to today, one window at a
/// time. Idempotent and resumable: completed windows are recorded in <c>ingest_window</c> and
/// skipped on the next run, and every write is an upsert, so killing this mid-run and restarting
/// converges.
/// </summary>
public sealed class BackfillJob
{
    private readonly IngestPipeline _pipeline;
    private readonly IngestWindowRepository _windows;
    private readonly OuraOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<BackfillJob> _logger;

    public BackfillJob(
        IngestPipeline pipeline,
        IngestWindowRepository windows,
        IOptions<OuraOptions> options,
        TimeProvider time,
        ILogger<BackfillJob> logger)
    {
        _pipeline = pipeline;
        _windows = windows;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>Returns false if any collection failed, in which case the caller should run the
    /// backfill again rather than treating history as complete.</summary>
    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        var complete = true;

        foreach (var collection in OuraCollections.ForIngest(_options.UseSandbox))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await BackfillCollectionAsync(collection, today, cancellationToken).ConfigureAwait(false);
            }
            catch (OuraApiException exception) when (exception.MissingScope is not null)
            {
                // The most likely failure for a freshly consented app, and the only place a wrong
                // scope name ever surfaces — the authorize endpoint accepts unknown ones silently.
                // No stack trace: the fix is one config line and a second trip to /oauth/start.
                complete = false;
                _logger.LogError(
                    "{Collection}: skipped — the token lacks the '{Scope}' scope. " +
                    "Add it to Oura__Scopes (currently '{Scopes}') and re-consent at /oauth/start.",
                    collection.Name, exception.MissingScope, _options.Scopes);
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OuraTokenPersistenceException))
            {
                // One collection must not take the other eighteen with it. A 403 on a single
                // endpoint (a lapsed Oura subscription, say) would otherwise stop the whole
                // warehouse from filling. Completed windows are already recorded, so the next cycle
                // resumes where this one stopped.
                complete = false;
                _logger.LogError(exception, "{Collection}: backfill failed; continuing with the rest.", collection.Name);
            }
        }

        return complete;
    }

    private async Task BackfillCollectionAsync(
        OuraCollection collection,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var completed = await _windows.CompletedWindowStartsAsync(collection.Name, cancellationToken).ConfigureAwait(false);

        // Newest first: a backfill interrupted on day one still leaves the most useful data behind,
        // and Grafana has something to draw before the 2020 windows finish.
        var pending = OuraWindows.Split(collection, _options.BackfillFrom, today)
            .Where(window => !completed.Contains(window.Start))
            .OrderByDescending(window => window.Start)
            .ToArray();

        if (pending.Length == 0)
        {
            _logger.LogDebug("{Collection}: backfill already complete.", collection.Name);
            return;
        }

        _logger.LogInformation("{Collection}: backfilling {Count} window(s).", collection.Name, pending.Length);

        var documents = 0;
        foreach (var window in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            documents += await _pipeline.IngestWindowAsync(collection, window, recordProgress: true, cancellationToken).ConfigureAwait(false);
        }

        var total = await _windows.DocumentCountAsync(collection.Name, cancellationToken).ConfigureAwait(false);
        if (total == 0)
        {
            // Missing scopes return empty arrays rather than errors, so this is the only signal that
            // the app was authorized without the scope this collection needs.
            _logger.LogWarning(
                "{Collection}: backfilled {Windows} window(s) from {From} and found no documents at all. " +
                "Either the ring never produced this data, or the '{Scopes}' scope grant is missing one. " +
                "Re-run /oauth/start to re-consent.",
                collection.Name, pending.Length, _options.BackfillFrom, _options.Scopes);
        }
        else
        {
            _logger.LogInformation(
                "{Collection}: backfill complete, {New} new document(s), {Total} recorded in total.",
                collection.Name, documents, total);
        }
    }
}
