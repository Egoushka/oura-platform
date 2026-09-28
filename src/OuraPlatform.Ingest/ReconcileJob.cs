using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OuraPlatform.Oura;
using OuraPlatform.Oura.Auth;

namespace OuraPlatform.Ingest;

/// <summary>
/// Re-fetches a trailing window of already-ingested days.
/// </summary>
/// <remarks>
/// Two reasons this is not "fetch since last run": nightly data only lands mid-morning the
/// following day, and Oura amends recent days after the fact (a bedtime edit rewrites a sleep
/// period that was already ingested). Re-fetching the last week and upserting is cheaper than
/// reasoning about which days might have changed.
/// </remarks>
public sealed class ReconcileJob
{
    private readonly IngestPipeline _pipeline;
    private readonly OuraOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ReconcileJob> _logger;

    public ReconcileJob(
        IngestPipeline pipeline,
        IOptions<OuraOptions> options,
        TimeProvider time,
        ILogger<ReconcileJob> logger)
    {
        _pipeline = pipeline;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        var from = today.AddDays(-_options.ReconcileTrailingDays);

        // Tomorrow, not today: Oura interprets date parameters in the user's local timezone, which
        // may already be on the next calendar day when this worker's clock says it is not.
        var to = today.AddDays(1);

        var documents = 0;
        var failed = 0;

        foreach (var collection in OuraCollections.ForIngest(_options.UseSandbox))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                foreach (var window in OuraWindows.Split(collection, from, to))
                {
                    documents += await _pipeline
                        .IngestWindowAsync(collection, window, recordProgress: false, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OuraTokenPersistenceException))
            {
                // Nothing is recorded as complete here, so the next run simply tries again.
                failed++;
                _logger.LogError(exception, "{Collection}: reconcile failed; continuing with the rest.", collection.Name);
            }
        }

        _logger.LogInformation(
            "Reconciled {From}..{To}: {Count} document(s) upserted, {Failed} collection(s) failed.",
            from, to, documents, failed);
    }
}
