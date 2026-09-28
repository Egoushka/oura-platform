using Dapper;

namespace OuraPlatform.Storage.Tests;

/// <summary>
/// The analytics queries, against a real TimescaleDB.
/// </summary>
/// <remarks>
/// These exist for two failure modes that a compiler cannot see and a mock would not reproduce.
/// <para>
/// The first is materialisation. Dapper matches a record's constructor parameters positionally
/// against the reader's columns, and refuses to narrow <c>bigint</c> to <c>int</c>, so a bare
/// <c>count(*)</c> or a column in the wrong order throws at runtime about a missing constructor —
/// having compiled, deployed and answered a health check perfectly happily. Every query here is run
/// for real and read into its record for that reason alone.
/// </para>
/// <para>
/// The second is arithmetic that is wrong rather than broken: a lag that does not shift, a count of
/// days reported as a count of events. Those return a plausible number.
/// </para>
/// </remarks>
[Collection(TimescaleCollection.Name)]
public sealed class AnalyticsTests(TimescaleFixture timescale) : IAsyncLifetime
{
    public Task InitializeAsync() => timescale.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private AnalyticsRepository Repository() => new(timescale.DataSource);

    private static readonly MetricDefinition Hrv = DailyMetric.Resolve("hrv");
    private static readonly MetricDefinition RestingHr = DailyMetric.Resolve("resting_hr");

    /// <summary>Fourteen consecutive days of HRV, rising by one a day from 50.</summary>
    private async Task SeedDailyAsync(DateOnly from = default, int days = 14)
    {
        var start = from == default ? new DateOnly(2026, 8, 1) : from;

        await using var connection = await timescale.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            insert into daily (day, hrv_avg, rhr_lowest, sleep_score)
            select @start::date + n, 50 + n, 60 - n, 70 + n
            from generate_series(0, @days - 1) as n
            """,
            new { start, days });
    }

    private async Task SeedContextAsync(string kind, params string[] timestamps)
    {
        await using var connection = await timescale.DataSource.OpenConnectionAsync();
        foreach (var (timestamp, index) in timestamps.Select((t, i) => (t, i)))
        {
            await connection.ExecuteAsync(
                """
                insert into context (ts, kind, label, source, source_id)
                values (@ts::timestamptz, @kind, 'seeded', 'test', @id)
                """,
                new { ts = timestamp, kind, id = $"{kind}-{index}" });
        }
    }

    // ------------------------------------------------------------------
    // Materialisation
    // ------------------------------------------------------------------

    [Fact]
    public async Task Every_query_reads_back_into_its_record()
    {
        await SeedDailyAsync();
        await SeedContextAsync("party", "2026-08-05T12:00:00Z");
        await SeedNightAsync(new DateOnly(2026, 8, 3));

        var repository = Repository();

        // No assertions on the numbers: the point is that none of these throws on the way out.
        await repository.SummarisePeriodAsync(Hrv, new(2026, 8, 1), new(2026, 8, 14), CancellationToken.None);
        await repository.CorrelateAsync(Hrv, "party", 1, CancellationToken.None);
        await repository.OutlierDaysAsync(Hrv, 5, CancellationToken.None);
        await repository.WeeklyDigestAsync(new(2026, 8, 3), CancellationToken.None);
        await repository.NightDetailAsync(new(2026, 8, 3), CancellationToken.None);
        await repository.ContextKindsAsync(CancellationToken.None);
        await repository.CoverageAsync(CancellationToken.None);
    }

    // ------------------------------------------------------------------
    // Arithmetic
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_period_summarises_only_the_days_inside_it()
    {
        await SeedDailyAsync();

        // Days 1..14 carry 50..63. The 3rd to the 5th is 52, 53, 54.
        var summary = await Repository().SummarisePeriodAsync(
            Hrv, new(2026, 8, 3), new(2026, 8, 5), CancellationToken.None);

        Assert.Equal(3, summary.Days);
        Assert.Equal(53m, summary.Mean);
        Assert.Equal(52m, summary.Min);
        Assert.Equal(54m, summary.Max);
    }

    [Fact]
    public async Task An_empty_period_reports_no_days_rather_than_zero()
    {
        await SeedDailyAsync();

        var summary = await Repository().SummarisePeriodAsync(
            Hrv, new(2020, 1, 1), new(2020, 1, 31), CancellationToken.None);

        // Missing is not zero: a mean of 0 here would read as a catastrophic month.
        Assert.Equal(0, summary.Days);
        Assert.Null(summary.Mean);
    }

    /// <summary>
    /// The lag is the whole question — a late night damages the night that follows it. A shift that
    /// silently does nothing still returns two buckets and a difference, so it has to be asserted
    /// against a day whose value is known.
    /// </summary>
    [Fact]
    public async Task Correlate_shifts_the_context_by_the_lag()
    {
        await SeedDailyAsync();
        await SeedContextAsync("party", "2026-08-05T12:00:00Z");

        var repository = Repository();

        // The 5th carries 54, the 6th 55.
        var sameDay = await repository.CorrelateAsync(Hrv, "party", 0, CancellationToken.None);
        Assert.Equal(54m, sameDay.Single(b => b.Bucket == "with").Mean);

        var nextDay = await repository.CorrelateAsync(Hrv, "party", 1, CancellationToken.None);
        Assert.Equal(55m, nextDay.Single(b => b.Bucket == "with").Mean);
    }

    [Fact]
    public async Task A_context_kind_that_never_happened_produces_no_with_bucket()
    {
        await SeedDailyAsync();
        await SeedContextAsync("party", "2026-08-05T12:00:00Z");

        var buckets = await Repository().CorrelateAsync(Hrv, "yoga", 1, CancellationToken.None);

        // Absence of data, not absence of an effect. The caller has to be able to tell.
        Assert.DoesNotContain(buckets, b => b.Bucket == "with");
        Assert.Equal(14, buckets.Single().Days);
    }

    /// <summary>
    /// <c>context_daily</c> is already grouped by (day, kind), so counting its rows counts days.
    /// Three events on one day is the case that tells the two apart.
    /// </summary>
    [Fact]
    public async Task Context_kinds_counts_events_not_days()
    {
        await SeedContextAsync(
            "meeting",
            "2026-08-05T09:00:00Z", "2026-08-05T12:00:00Z", "2026-08-05T15:00:00Z",
            "2026-08-07T09:00:00Z");

        var kinds = await Repository().ContextKindsAsync(CancellationToken.None);

        var meetings = Assert.Single(kinds);
        Assert.Equal(4, meetings.Events);
        Assert.Equal(new DateOnly(2026, 8, 5), meetings.FirstDay);
        Assert.Equal(new DateOnly(2026, 8, 7), meetings.LastDay);
    }

    [Fact]
    public async Task Outliers_are_two_sided()
    {
        await SeedDailyAsync();

        var outliers = await Repository().OutlierDaysAsync(Hrv, 2, CancellationToken.None);

        // A rising series is symmetric about its mean, so both ends come back before anything else.
        Assert.Equal(2, outliers.Count);
        Assert.Contains(outliers, o => o.Day == new DateOnly(2026, 8, 1));
        Assert.Contains(outliers, o => o.Day == new DateOnly(2026, 8, 14));
        Assert.All(outliers, o => Assert.True(Math.Abs(o.ZScore!.Value) > 1));
    }

    [Fact]
    public async Task A_weekly_digest_compares_the_week_with_the_one_before_and_drops_empty_metrics()
    {
        await SeedDailyAsync();

        // 8-8 to 8-14 carry 57..63; the week before, 8-1 to 8-7, carries 50..56.
        var digest = await Repository().WeeklyDigestAsync(new(2026, 8, 8), CancellationToken.None);

        var hrv = digest.Single(m => m.Metric == "hrv");
        Assert.Equal(60m, hrv.ThisWeek);
        Assert.Equal(53m, hrv.PriorWeek);
        Assert.Equal(7, hrv.Days);

        // rhr_lowest runs the other way — 60 - n, so 53..47 over the same week — and the seed
        // leaves most columns null.
        Assert.Equal(50m, digest.Single(m => m.Metric == "resting_hr").ThisWeek);
        Assert.DoesNotContain(digest, m => m.Metric == "steps");
    }

    // ------------------------------------------------------------------
    // Night detail
    // ------------------------------------------------------------------

    /// <summary>Four hours of 5-minute HRV samples from 23:00 the evening before, and a hypnogram.</summary>
    private async Task SeedNightAsync(DateOnly night)
    {
        var start = night.AddDays(-1).ToDateTime(new TimeOnly(23, 0));

        await using var connection = await timescale.DataSource.OpenConnectionAsync();

        await connection.ExecuteAsync(
            """
            insert into sleep_sessions (id, night, bedtime_start, bedtime_end, payload)
            values ('session-1', @night, @start, @start + interval '4 hours', '{}'::jsonb)
            """,
            new { night, start });

        // Hour n averages 60 + n, so a band that is not grouped by hour cannot produce these.
        await connection.ExecuteAsync(
            """
            insert into sleep_series (ts, night, metric, value)
            select @start::timestamptz + (n || ' minutes')::interval, @night, 'hrv', 60 + floor(n / 60.0)
            from generate_series(0, 239, 5) as n
            """,
            new { night, start });

        await connection.ExecuteAsync(
            """
            insert into hypnogram (ts, night, phase)
            select @start::timestamptz + (n || ' minutes')::interval, @night, 2
            from generate_series(0, 55, 5) as n
            """,
            new { night, start });
    }

    [Fact]
    public async Task A_night_comes_back_as_hour_bands_not_samples()
    {
        var night = new DateOnly(2026, 8, 3);
        await SeedDailyAsync();
        await SeedNightAsync(night);

        var detail = await Repository().NightDetailAsync(night, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(night, detail.Headline.Night);

        // 48 samples in, 4 bands out. That ratio is the whole design constraint.
        Assert.Equal(4, detail.HourBands.Count);
        Assert.Equal([60m, 61m, 62m, 63m], detail.HourBands.OrderBy(b => b.HoursIn).Select(b => b.Mean));

        var light = Assert.Single(detail.Phases);
        Assert.Equal("light", light.Phase);
        Assert.Equal(60, light.Minutes);
    }

    [Fact]
    public async Task A_night_with_no_data_is_null_rather_than_empty()
    {
        await SeedDailyAsync();

        // 2026-08-20 is outside the seeded range entirely.
        Assert.Null(await Repository().NightDetailAsync(new(2026, 8, 20), CancellationToken.None));
    }

    // ------------------------------------------------------------------

    [Fact]
    public async Task Coverage_reports_what_is_actually_held()
    {
        await SeedDailyAsync();
        await SeedNightAsync(new DateOnly(2026, 8, 3));
        await SeedContextAsync("party", "2026-08-05T12:00:00Z");

        var coverage = await Repository().CoverageAsync(CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 8, 1), coverage.FirstDay);
        Assert.Equal(new DateOnly(2026, 8, 14), coverage.LastDay);
        Assert.Equal(14, coverage.Days);
        Assert.Equal(1, coverage.Nights);
        Assert.Equal(0, coverage.Workouts);
        Assert.Equal(1, coverage.ContextEvents);
    }

    /// <summary>
    /// The column name reaches SQL by interpolation, so this is the boundary that keeps it safe.
    /// </summary>
    [Fact]
    public void An_unknown_metric_is_rejected_by_name()
    {
        Assert.Throws<ArgumentException>(() => DailyMetric.Resolve("hrv_avg; drop table daily"));
        Assert.Throws<ArgumentException>(() => DailyMetric.Resolve(null));

        // Case is not part of the contract.
        Assert.Equal(RestingHr.Column, DailyMetric.Resolve("RESTING_HR").Column);
    }
}
