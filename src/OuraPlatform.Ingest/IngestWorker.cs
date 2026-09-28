using Microsoft.Extensions.Options;
using OuraPlatform.Oura;
using OuraPlatform.Oura.Auth;
using OuraPlatform.Storage;

namespace OuraPlatform.Ingest;

public sealed class IngestOptions
{
    public const string SectionName = "Ingest";

    /// <summary>How often the reconcile job runs. Nightly data lands mid-morning the following day,
    /// so anything under a few hours is wasted requests.</summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Wait between the first failure and the first retry of a whole cycle.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Apply pending migrations at startup. Safe with a single instance, which is the
    /// documented deployment shape.</summary>
    public bool MigrateOnStartup { get; set; } = true;
}

/// <summary>
/// The whole ingest loop: migrate, backfill once, then reconcile a trailing window on a timer.
/// </summary>
/// <remarks>
/// Single instance by design (see CLAUDE.md). There is no distributed locking here and adding a
/// second replica would have both of them spending the same single-use refresh token.
/// </remarks>
public sealed class IngestWorker(
    IServiceProvider services,
    DatabaseMigrator migrator,
    IOuraTokenStore tokens,
    IHostApplicationLifetime lifetime,
    IOptions<IngestOptions> options,
    IOptions<OuraOptions> oura,
    ILogger<IngestWorker> logger) : BackgroundService
{
    private readonly IngestOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.MigrateOnStartup)
        {
            migrator.Run();
        }

        await WaitForAuthorizationAsync(stoppingToken).ConfigureAwait(false);

        var backfilled = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!backfilled)
                {
                    await using var scope = services.CreateAsyncScope();

                    // Only latch when every collection finished. A partial backfill has to be
                    // retried, and the recorded windows make the retry cheap.
                    backfilled = await scope.ServiceProvider.GetRequiredService<BackfillJob>()
                        .RunAsync(stoppingToken).ConfigureAwait(false);
                }

                await using (var scope = services.CreateAsyncScope())
                {
                    await scope.ServiceProvider.GetRequiredService<ReconcileJob>()
                        .RunAsync(stoppingToken).ConfigureAwait(false);

                    // Context is what makes the Oura data worth joining, and neither importer talks
                    // to Oura — so a failure here must not touch the ingest cycle's own retry logic.
                    await ImportContextAsync(scope.ServiceProvider, stoppingToken).ConfigureAwait(false);
                }

                await Task.Delay(_options.ReconcileInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OuraTokenPersistenceException exception)
            {
                // A rotated token pair reached Oura but not the database. Nothing this process does
                // from here can recover, and retrying would only present a token Oura has already
                // invalidated.
                logger.LogCritical(exception, "Oura authorization is unrecoverable. Stopping.");
                Environment.ExitCode = 70;
                lifetime.StopApplication();
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Ingest cycle failed; retrying in {Delay}.", _options.RetryDelay);
                await Task.Delay(_options.RetryDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Calendar feeds and Oura's own tags, folded into `context`. Both are best-effort: a warehouse
    /// with health data and no context is still useful, one that stopped ingesting because a
    /// calendar URL expired is not.
    /// </summary>
    private async Task ImportContextAsync(IServiceProvider scope, CancellationToken stoppingToken)
    {
        try
        {
            await scope.GetRequiredService<OuraTagContextImporter>().RunAsync(stoppingToken).ConfigureAwait(false);

            var calendars = scope.GetRequiredService<CalendarImporter>();
            if (calendars.HasFeeds)
            {
                await calendars.RunAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Context import failed; health ingest is unaffected.");
        }
    }

    /// <summary>
    /// Blocks until someone has completed the browser handshake at <c>/oauth/start</c>. Polling
    /// rather than failing keeps the container in <c>restart: unless-stopped</c> from crash-looping
    /// while waiting for a human.
    /// </summary>
    private async Task WaitForAuthorizationAsync(CancellationToken stoppingToken)
    {
        if (oura.Value.UseSandbox)
        {
            // The sandbox accepts any Authorization string, so there is no handshake to wait for.
            logger.LogWarning("Running against the Oura sandbox. The data is fabricated — see docs/oura-api-notes.md.");
            return;
        }

        var warned = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (await tokens.ReadAsync(stoppingToken).ConfigureAwait(false) is not null)
            {
                return;
            }

            if (!warned)
            {
                logger.LogWarning("No Oura token stored. Waiting — complete the handshake at /oauth/start.");
                warned = true;
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        }
    }
}
