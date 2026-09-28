using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using OuraPlatform.Storage;
using Testcontainers.PostgreSql;

namespace OuraPlatform.Storage.Tests;

/// <summary>
/// A real TimescaleDB, migrated with the real migration scripts. Nothing here is mocked: the
/// hypertables, the <c>FOR UPDATE</c> semantics and the jsonb casts are exactly the things a fake
/// would get wrong.
/// </summary>
/// <remarks>Needs a running Docker daemon. Unlike the Oura fixture tests, these are not part of the
/// zero-dependency suite.</remarks>
public sealed class TimescaleFixture : IAsyncLifetime
{
    // Same image tag as docker-compose.yml: the hypertable DDL is only meaningful against a real
    // TimescaleDB, and a plain postgres image would pass these tests for the wrong reason.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("timescale/timescaledb:2.17.2-pg17")
        .WithDatabase("oura")
        .WithUsername("oura")
        .WithPassword("oura")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        new DatabaseMigrator(ConnectionString, NullLogger<DatabaseMigrator>.Instance).Run();
        DataSource = NpgsqlDataSource.Create(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>Empties every table between tests. The migration journal is left alone.</summary>
    public async Task ResetAsync()
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            truncate oura_raw, oauth_tokens, ingest_window, daily, sleep_sessions, workouts, tags,
                     context, hr_samples, sleep_series, hypnogram, ring_battery
            """,
            connection);

        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class TimescaleCollection : ICollectionFixture<TimescaleFixture>
{
    public const string Name = "timescale";
}
