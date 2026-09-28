using Microsoft.Extensions.Options;
using OuraPlatform.Oura;
using OuraPlatform.Storage;

namespace OuraPlatform.Ingest;

/// <summary>
/// Rebuilds every typed table from <c>oura_raw</c> without calling the API.
/// </summary>
/// <remarks>
/// This is the architecture invariant made executable. Two things need it:
/// a projection changed and history has to be re-derived, or a warehouse was restored from a dump
/// of <c>oura_raw</c> alone and the projections are empty. Neither is reachable through the normal
/// worker loop, which only ever projects what it has just fetched.
/// <para>
/// Runs and exits — it does not start the ingest loop, so it is safe to run against a database
/// whose worker is stopped without racing it.
/// </para>
/// </remarks>
public static class ReprojectCommand
{
    public const string Flag = "--reproject";

    public static async Task<int> RunAsync(IHost host, string[] args, CancellationToken cancellationToken)
    {
        var logger = host.Services.GetRequiredService<ILogger<IngestWorker>>();
        var migrator = host.Services.GetRequiredService<DatabaseMigrator>();
        var options = host.Services.GetRequiredService<IOptions<OuraOptions>>().Value;

        // A restored dump predates whatever migrations have been added since; project against the
        // current schema or the upserts reference columns that are not there yet.
        migrator.Run();

        // Optional collection filter: `--reproject sleep daily_sleep`. Empty means everything.
        var requested = args.Where(a => a != Flag).ToArray();
        var collections = requested.Length == 0
            ? OuraCollections.ForIngest(options.UseSandbox)
            : requested.Select(OuraCollections.ByName).ToArray();

        await using var scope = host.Services.CreateAsyncScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<IngestPipeline>();

        var total = 0;
        foreach (var collection in collections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += await pipeline.ReprojectAsync(collection, from: null, to: null, cancellationToken)
                .ConfigureAwait(false);
        }

        // enhanced_tag -> context is a projection of oura_raw like any other, so a re-projection
        // that skipped it would leave context stale against the payloads it derives from.
        if (requested.Length == 0 || requested.Contains("enhanced_tag"))
        {
            total += await scope.ServiceProvider.GetRequiredService<OuraTagContextImporter>()
                .RunAsync(cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Re-projected {Collections} collection(s) into {Rows} row(s). Nothing was fetched from Oura.",
            collections.Count, total);

        return 0;
    }
}
