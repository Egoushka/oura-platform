using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace OuraPlatform.Storage.Tests;

[Collection(TimescaleCollection.Name)]
public sealed class MigrationTests(TimescaleFixture timescale)
{
    [Theory]
    [InlineData("oura_raw")]
    [InlineData("oauth_tokens")]
    [InlineData("ingest_window")]
    [InlineData("daily")]
    [InlineData("sleep_sessions")]
    [InlineData("workouts")]
    [InlineData("tags")]
    [InlineData("context")]
    [InlineData("hr_samples")]
    [InlineData("sleep_series")]
    [InlineData("hypnogram")]
    [InlineData("ring_battery")]
    public async Task Table_exists(string table)
    {
        await using var connection = await timescale.DataSource.OpenConnectionAsync();

        Assert.True(await connection.ExecuteScalarAsync<bool>(
            "select exists (select 1 from information_schema.tables where table_name = @table)",
            new { table }));
    }

    [Theory]
    [InlineData("hr_samples")]
    [InlineData("sleep_series")]
    [InlineData("hypnogram")]
    [InlineData("ring_battery")]
    public async Task Series_table_is_a_hypertable(string table)
    {
        await using var connection = await timescale.DataSource.OpenConnectionAsync();

        Assert.True(await connection.ExecuteScalarAsync<bool>(
            "select exists (select 1 from timescaledb_information.hypertables where hypertable_name = @table)",
            new { table }));
    }

    [Theory]
    [InlineData("sleep_nightly")]
    [InlineData("hr_daily")]
    [InlineData("hypnogram_nightly")]
    public async Task Rollup_view_is_queryable(string view)
    {
        await using var connection = await timescale.DataSource.OpenConnectionAsync();

        // Selecting from it proves the SQL parses against the real schema, which a catalog lookup
        // would not.
        await connection.ExecuteAsync($"select * from {view} limit 1");
    }

    /// <summary>
    /// Migrations run without a transaction, because TimescaleDB refuses several statements inside
    /// one. That makes idempotency load-bearing rather than a nicety: a script that fails halfway
    /// is re-run from the top on the next start.
    /// </summary>
    [Fact]
    public void Migrations_are_idempotent()
    {
        var migrator = new DatabaseMigrator(timescale.ConnectionString, NullLogger<DatabaseMigrator>.Instance);

        migrator.Run();
        migrator.Run();
    }

    [Fact]
    public async Task Sleep_series_allows_a_null_value_for_a_gap()
    {
        await using var connection = await timescale.DataSource.OpenConnectionAsync();

        // Non-wear and unmeasured intervals must survive as gaps. A NOT NULL here would force the
        // writer to invent a zero.
        await connection.ExecuteAsync(
            """
            insert into sleep_series (ts, night, metric, value)
            values (now(), current_date, 'hrv', null)
            on conflict (ts, metric) do nothing
            """);
    }
}
