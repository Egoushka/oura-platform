using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using OuraPlatform.Oura.Auth;

namespace OuraPlatform.Storage;

public static class StorageServiceCollectionExtensions
{
    public const string ConnectionStringName = "Postgres";

    public static IServiceCollection AddOuraStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"ConnectionStrings:{ConnectionStringName} is not configured. See .env.example.");

        services.AddNpgsqlDataSource(connectionString);

        services.AddSingleton(provider => new DatabaseMigrator(
            connectionString,
            provider.GetRequiredService<ILogger<DatabaseMigrator>>()));

        services.AddSingleton<RawDocumentRepository>();
        services.AddSingleton<IngestWindowRepository>();
        services.AddSingleton<DocumentProjector>();
        services.AddSingleton<ContextRepository>();
        services.AddSingleton<AnalyticsRepository>();

        // Singleton because OuraAuthenticationHandler is effectively singleton-scoped per named
        // client and must not capture a scoped dependency.
        services.AddSingleton<IOuraTokenStore, OuraTokenStore>();

        return services;
    }
}
