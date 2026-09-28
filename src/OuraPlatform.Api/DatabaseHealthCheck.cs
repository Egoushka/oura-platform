using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OuraPlatform.Oura.Auth;

namespace OuraPlatform.Api;

/// <summary>
/// Backs <c>/healthz</c>, which is what the Compose healthcheck polls. Degraded rather than
/// unhealthy when no token is stored: before the first handshake that is the expected state, and
/// restarting the container would not fix it.
/// </summary>
public sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource, IOuraTokenStore tokens) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("select 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Postgres is not reachable.", exception);
        }

        var token = await tokens.ReadAsync(cancellationToken);
        return token is null
            ? HealthCheckResult.Degraded("No Oura token stored. Visit /oauth/start.")
            : HealthCheckResult.Healthy($"Oura token valid until {token.ExpiresAt:u}.");
    }
}
