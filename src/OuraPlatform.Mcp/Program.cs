using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OuraPlatform.Mcp;
using OuraPlatform.Storage;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));

// Storage only. This process never talks to Oura, so it needs no client id, no secret and no token
// — which is the point of putting the server over the warehouse rather than over the API.
builder.Services.AddOuraStorage(builder.Configuration);

builder.Services
    .AddMcpServer(options =>
    {
        // Otherwise this announces itself as "OuraPlatform.Mcp", which is what it is called on disk
        // and not what anyone picking it out of a client's server list is looking for.
        options.ServerInfo = new() { Name = "oura-platform", Version = "0.3.0" };
        options.ServerInstructions = OuraTools.Instructions;
    })
    .WithHttpTransport()
    .WithToolsFromAssembly();

builder.Services.AddHealthChecks().AddCheck<WarehouseHealthCheck>("warehouse");

var app = builder.Build();

app.UseSerilogRequestLogging();
app.MapHealthChecks("/healthz");

// No authentication of its own, like the api container: it is reachable only on the Compose network
// and through whatever fronts it. Do not publish this port.
app.MapMcp("/mcp");

app.Run();

/// <summary>Postgres reachability. Unlike the api, a missing Oura token is none of this process's business.</summary>
internal sealed class WarehouseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("select count(*) from daily", connection);
            var days = (long?)await command.ExecuteScalarAsync(cancellationToken) ?? 0;

            return days > 0
                ? HealthCheckResult.Healthy($"{days} days in the warehouse.")
                : HealthCheckResult.Degraded("The warehouse is empty; ingest has not backfilled yet.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Postgres is not reachable.", exception);
        }
    }
}
