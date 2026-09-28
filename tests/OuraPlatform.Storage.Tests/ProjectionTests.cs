using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using OuraPlatform.Oura;

namespace OuraPlatform.Storage.Tests;

/// <summary>
/// Projections read only from stored payloads, so these run entirely offline once the container is
/// up — no API client involved.
/// </summary>
[Collection(TimescaleCollection.Name)]
public sealed class ProjectionTests(TimescaleFixture timescale) : IAsyncLifetime
{
    public Task InitializeAsync() => timescale.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private DocumentProjector Projector() =>
        new(timescale.DataSource, NullLogger<DocumentProjector>.Instance);

    private RawDocumentRepository Raw() => new(timescale.DataSource);

    /// <summary>Production shape: interval 300, a null in the middle for a gap.</summary>
    private const string SleepPayload = """
        {
          "id": "sleep-1",
          "day": "2026-07-31",
          "bedtime_start": "2026-07-30T23:00:00+03:00",
          "bedtime_end": "2026-07-31T07:00:00+03:00",
          "type": "long_sleep",
          "period": 0,
          "time_in_bed": 28800,
          "total_sleep_duration": 26000,
          "average_hrv": 62,
          "lowest_heart_rate": 47,
          "low_battery_alert": false,
          "hrv": {"interval": 300, "items": [55.0, null, 71.0], "timestamp": "2026-07-30T23:00:00+03:00"},
          "heart_rate": {"interval": 300, "items": [52.0, 51.0], "timestamp": "2026-07-30T23:00:00+03:00"},
          "sleep_phase_5_min": "1234"
        }
        """;

    private static OuraRawDocument SleepDocument() =>
        new("sleep", "sleep-1", new DateOnly(2026, 7, 31), SleepPayload);

    [Fact]
    public async Task Sleep_fans_out_into_session_series_and_hypnogram()
    {
        await Projector().ProjectAsync(OuraCollections.ByName("sleep"), [SleepDocument()], CancellationToken.None);

        await using var connection = await timescale.DataSource.OpenConnectionAsync();

        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("select count(*) from sleep_sessions"));
        Assert.Equal(5, await connection.ExecuteScalarAsync<int>("select count(*) from sleep_series"));
        Assert.Equal(4, await connection.ExecuteScalarAsync<int>("select count(*) from hypnogram"));

        // 1=deep 2=light 3=REM 4=awake, one step per 5 minutes from bedtime_start.
        Assert.Equal([1, 2, 3, 4], await connection.QueryAsync<short>("select phase from hypnogram order by ts"));
    }

    [Fact]
    public async Task A_gap_in_the_sample_stays_null()
    {
        await Projector().ProjectAsync(OuraCollections.ByName("sleep"), [SleepDocument()], CancellationToken.None);

        await using var connection = await timescale.DataSource.OpenConnectionAsync();
        var values = (await connection.QueryAsync<decimal?>(
            "select value from sleep_series where metric = 'hrv' order by ts")).ToArray();

        // Missing is not zero. A zero here would drag every nightly average down and look plausible.
        Assert.Equal([55m, null, 71m], values);
    }

    [Fact]
    public async Task Sample_timestamps_advance_by_the_payloads_own_interval()
    {
        await Projector().ProjectAsync(OuraCollections.ByName("sleep"), [SleepDocument()], CancellationToken.None);

        await using var connection = await timescale.DataSource.OpenConnectionAsync();
        var timestamps = (await connection.QueryAsync<DateTime>(
            "select ts from sleep_series where metric = 'hrv' order by ts")).ToArray();

        // The sandbox says 60 and production says 300; the projector must read it, not assume it.
        Assert.Equal(TimeSpan.FromMinutes(5), timestamps[1] - timestamps[0]);
    }

    [Fact]
    public async Task Projecting_the_same_document_twice_converges()
    {
        var projector = Projector();
        var collection = OuraCollections.ByName("sleep");

        await projector.ProjectAsync(collection, [SleepDocument()], CancellationToken.None);
        await projector.ProjectAsync(collection, [SleepDocument()], CancellationToken.None);

        await using var connection = await timescale.DataSource.OpenConnectionAsync();

        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("select count(*) from sleep_sessions"));
        Assert.Equal(5, await connection.ExecuteScalarAsync<int>("select count(*) from sleep_series"));
        Assert.Equal(4, await connection.ExecuteScalarAsync<int>("select count(*) from hypnogram"));
    }

    /// <summary>
    /// Two sleep periods in the same batch whose samples land on the same timestamps. Postgres
    /// raises 21000 — "ON CONFLICT DO UPDATE command cannot affect row a second time" — if the batch
    /// is not deduplicated before the upsert, and it took a real backfill against the sandbox to
    /// surface it.
    /// </summary>
    [Fact]
    public async Task Overlapping_sleep_periods_in_one_batch_do_not_break_the_upsert()
    {
        var overlapping = new OuraRawDocument("sleep", "sleep-2", new(2026, 7, 31),
            SleepPayload.Replace("\"sleep-1\"", "\"sleep-2\"", StringComparison.Ordinal));

        await Projector().ProjectAsync(
            OuraCollections.ByName("sleep"), [SleepDocument(), overlapping], CancellationToken.None);

        await using var connection = await timescale.DataSource.OpenConnectionAsync();

        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("select count(*) from sleep_sessions"));
        // Both periods cover the same instants, so the series and hypnogram collapse onto one set.
        Assert.Equal(5, await connection.ExecuteScalarAsync<int>("select count(*) from sleep_series"));
        Assert.Equal(4, await connection.ExecuteScalarAsync<int>("select count(*) from hypnogram"));
    }

    [Fact]
    public async Task Duplicate_documents_in_one_raw_batch_do_not_break_the_upsert()
    {
        var written = await Raw().UpsertAsync(
            [SleepDocument(), SleepDocument() with { Payload = """{"id":"sleep-1","efficiency":91}""" }],
            "1.37",
            CancellationToken.None);

        await using var connection = await timescale.DataSource.OpenConnectionAsync();

        Assert.Equal(1, written);
        // Last one copied wins, so the batch's own ordering decides.
        Assert.Equal(91, await connection.ExecuteScalarAsync<int>("select (payload->>'efficiency')::int from oura_raw"));
    }

    [Fact]
    public async Task Several_daily_collections_share_one_row_without_clobbering_each_other()
    {
        var projector = Projector();

        await projector.ProjectAsync(
            OuraCollections.ByName("daily_sleep"),
            [new("daily_sleep", "s1", new(2026, 7, 31), """{"id":"s1","day":"2026-07-31","score":83,"contributors":{},"timestamp":"2026-07-31T00:00:00+03:00"}""")],
            CancellationToken.None);

        await projector.ProjectAsync(
            OuraCollections.ByName("daily_readiness"),
            [new("daily_readiness", "r1", new(2026, 7, 31), """{"id":"r1","day":"2026-07-31","score":74,"contributors":{},"temperature_deviation":-0.2,"timestamp":"2026-07-31T00:00:00+03:00"}""")],
            CancellationToken.None);

        await projector.ProjectAsync(OuraCollections.ByName("sleep"), [SleepDocument()], CancellationToken.None);

        await using var connection = await timescale.DataSource.OpenConnectionAsync();
        var row = await connection.QuerySingleAsync<DailyRow>(
            """
            select sleep_score     as SleepScore,
                   readiness_score as ReadinessScore,
                   temp_deviation  as TempDeviation,
                   hrv_avg         as HrvAvg,
                   rhr_lowest      as RhrLowest
            from daily where day = '2026-07-31'
            """);

        Assert.Equal(83, row.SleepScore);
        Assert.Equal(74, row.ReadinessScore);
        Assert.Equal(-0.2m, row.TempDeviation);
        Assert.Equal(62m, row.HrvAvg);
        Assert.Equal(47m, row.RhrLowest);
    }

    /// <summary>
    /// Oura sends <c>{"average": 0.0}</c> for a night it did not measure — seen on a live account.
    /// A 0% blood oxygen is not a low reading, it is a missing one, and a chart drawn
    /// from it shows a fatal desaturation.
    /// </summary>
    [Fact]
    public async Task A_zero_spo2_average_is_stored_as_missing_not_as_zero()
    {
        await Projector().ProjectAsync(
            OuraCollections.ByName("daily_spo2"),
            [
                new("daily_spo2", "s0", new(2025, 6, 1), """{"id":"s0","day":"2025-06-01","spo2_percentage":{"average":0.0},"breathing_disturbance_index":0}"""),
                new("daily_spo2", "s1", new(2025, 6, 2), """{"id":"s1","day":"2025-06-02","spo2_percentage":{"average":95.25},"breathing_disturbance_index":0}"""),
            ],
            CancellationToken.None);

        await using var connection = await timescale.DataSource.OpenConnectionAsync();

        Assert.Null(await connection.ExecuteScalarAsync<decimal?>(
            "select spo2_avg from daily where day = '2025-06-01'"));

        Assert.Equal(95.25m, await connection.ExecuteScalarAsync<decimal?>(
            "select spo2_avg from daily where day = '2025-06-02'"));

        // A real zero elsewhere in the document is left alone — no disturbances is a fact.
        Assert.Equal(0m, await connection.ExecuteScalarAsync<decimal?>(
            "select breathing_disturbance_index from daily where day = '2025-06-01'"));
    }

    [Fact]
    public async Task Naps_do_not_overwrite_the_nights_summary()
    {
        var nap = new OuraRawDocument("sleep", "sleep-nap", new(2026, 7, 31), SleepPayload
            .Replace("\"sleep-1\"", "\"sleep-nap\"", StringComparison.Ordinal)
            .Replace("\"long_sleep\"", "\"late_nap\"", StringComparison.Ordinal)
            .Replace("\"average_hrv\": 62", "\"average_hrv\": 9", StringComparison.Ordinal));

        var projector = Projector();
        await projector.ProjectAsync(OuraCollections.ByName("sleep"), [SleepDocument()], CancellationToken.None);
        await projector.ProjectAsync(OuraCollections.ByName("sleep"), [nap], CancellationToken.None);

        await using var connection = await timescale.DataSource.OpenConnectionAsync();

        Assert.Equal(62m, await connection.ExecuteScalarAsync<decimal>(
            "select hrv_avg from daily where day = '2026-07-31'"));

        // The nap is still a first-class session and still contributes its own series rows.
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("select count(*) from sleep_sessions"));
    }

    [Fact]
    public async Task An_unparseable_document_is_skipped_without_losing_the_batch()
    {
        var broken = new OuraRawDocument("sleep", "sleep-broken", new(2026, 7, 30), """{"id":"sleep-broken","day":"not-a-date"}""");

        var projected = await Projector().ProjectAsync(
            OuraCollections.ByName("sleep"), [broken, SleepDocument()], CancellationToken.None);

        await using var connection = await timescale.DataSource.OpenConnectionAsync();

        // The raw row is durable regardless, so this is recoverable: fix the model, re-project.
        Assert.Equal(1, projected);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("select count(*) from sleep_sessions"));
    }

    [Fact]
    public async Task Heart_rate_rows_upsert_on_timestamp_and_source()
    {
        var collection = OuraCollections.ByName("heartrate");
        OuraRawDocument Row(int bpm) => new(
            "heartrate",
            "heartrate:2026-07-31T10:00:00.000Z",
            null,
            $$"""{"timestamp":"2026-07-31T10:00:00.000Z","bpm":{{bpm}},"source":"awake"}""");

        var projector = Projector();
        await projector.ProjectAsync(collection, [Row(60)], CancellationToken.None);
        await projector.ProjectAsync(collection, [Row(64)], CancellationToken.None);

        await using var connection = await timescale.DataSource.OpenConnectionAsync();

        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("select count(*) from hr_samples"));
        Assert.Equal((short)64, await connection.ExecuteScalarAsync<short>("select bpm from hr_samples"));
    }

    /// <summary>
    /// The invariant the whole schema is built around: every typed table must be rebuildable from
    /// <c>oura_raw</c> without calling the API.
    /// </summary>
    [Fact]
    public async Task Projections_can_be_rebuilt_from_raw_alone()
    {
        var collection = OuraCollections.ByName("sleep");
        await Raw().UpsertAsync([SleepDocument()], "1.37", CancellationToken.None);
        await Projector().ProjectAsync(collection, [SleepDocument()], CancellationToken.None);

        await using (var connection = await timescale.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("truncate sleep_sessions, sleep_series, hypnogram, daily");
        }

        var stored = await Raw().ReadAsync("sleep", null, null, CancellationToken.None);
        await Projector().ProjectAsync(collection, stored, CancellationToken.None);

        await using (var connection = await timescale.DataSource.OpenConnectionAsync())
        {
            Assert.Equal(5, await connection.ExecuteScalarAsync<int>("select count(*) from sleep_series"));
            Assert.Equal(62m, await connection.ExecuteScalarAsync<decimal>("select hrv_avg from daily"));
        }
    }

    [Fact]
    public async Task Raw_upserts_are_idempotent_and_keep_the_latest_payload()
    {
        var raw = Raw();
        await raw.UpsertAsync([SleepDocument()], "1.37", CancellationToken.None);
        await raw.UpsertAsync(
            [SleepDocument() with { Payload = """{"id":"sleep-1","day":"2026-07-31","efficiency":91}""" }],
            "1.38",
            CancellationToken.None);

        await using var connection = await timescale.DataSource.OpenConnectionAsync();

        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("select count(*) from oura_raw"));
        Assert.Equal("1.38", await connection.ExecuteScalarAsync<string>("select spec_ver from oura_raw"));
        Assert.Equal(91, await connection.ExecuteScalarAsync<int>("select (payload->>'efficiency')::int from oura_raw"));
    }

    [Fact]
    public async Task Collections_without_a_projection_are_stored_but_not_projected()
    {
        var projected = await Projector().ProjectAsync(
            OuraCollections.ByName("session"),
            [new("session", "x1", new(2026, 7, 31), """{"id":"x1","day":"2026-07-31","type":"meditation","start_datetime":"2026-07-31T08:00:00+03:00","end_datetime":"2026-07-31T08:10:00+03:00"}""")],
            CancellationToken.None);

        Assert.Equal(0, projected);
    }

    private sealed record DailyRow(
        int? SleepScore,
        int? ReadinessScore,
        decimal? TempDeviation,
        decimal? HrvAvg,
        decimal? RhrLowest);
}
