using OuraPlatform.Ingest;
using OuraPlatform.Oura;
using OuraPlatform.Storage;
using Serilog;
using Serilog.Formatting.Compact;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((services, configuration) => configuration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));

builder.Services.Configure<IngestOptions>(builder.Configuration.GetSection(IngestOptions.SectionName));
builder.Services.Configure<ContextOptions>(builder.Configuration.GetSection(ContextOptions.SectionName));

// Its own client: the feed URL is a credential, and it must never travel through the Oura
// pipeline's bearer handler.
builder.Services.AddHttpClient(CalendarImporter.HttpClientName, http =>
{
    http.Timeout = TimeSpan.FromSeconds(60);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("oura-platform/0.1 (+context importer)");
});

builder.Services.AddOuraApi(builder.Configuration);
builder.Services.AddOuraStorage(builder.Configuration);

builder.Services.AddScoped<IngestPipeline>();
builder.Services.AddScoped<BackfillJob>();
builder.Services.AddScoped<ReconcileJob>();
builder.Services.AddScoped<CalendarImporter>();
builder.Services.AddScoped<OuraTagContextImporter>();

// `--reproject [collection ...]` rebuilds the typed tables from oura_raw and exits, without
// touching the API. Registering the worker in that mode would start the ingest loop alongside it.
var reproject = args.Contains(ReprojectCommand.Flag);
if (!reproject)
{
    builder.Services.AddHostedService<IngestWorker>();
}

var host = builder.Build();

if (reproject)
{
    return await ReprojectCommand.RunAsync(host, args, CancellationToken.None);
}

host.Run();
return 0;
