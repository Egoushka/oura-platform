using Dapper;
using Npgsql;

namespace OuraPlatform.Storage;

/// <summary>
/// Read-only analytics over the warehouse.
/// </summary>
/// <remarks>
/// Every method returns an aggregate. That is the whole design constraint: the caller is a language
/// model, and handing it a night's 5-minute arrays would spend thousands of tokens restating numbers
/// it cannot hold in its head anyway. A night is ~200 samples; this returns eight of them.
/// <para>
/// `daily` is keyed on Oura's own `day`, so nothing here re-derives which night a number belongs to.
/// </para>
/// </remarks>
public sealed class AnalyticsRepository(NpgsqlDataSource dataSource)
{
    /// <summary>Distribution of one metric over a closed date range.</summary>
    public async Task<PeriodSummary> SummarisePeriodAsync(
        MetricDefinition metric, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleAsync<PeriodSummary>(new CommandDefinition(
            $"""
             select
                 @from                                                        as "From",
                 @to                                                          as "To",
                 count({metric.Column})::int                                  as "Days",
                 round(avg({metric.Column})::numeric, 2)                      as "Mean",
                 round(percentile_cont(0.5) within group (order by {metric.Column})::numeric, 2) as "Median",
                 round(stddev_samp({metric.Column})::numeric, 2)              as "StdDev",
                 round(min({metric.Column})::numeric, 2)                      as "Min",
                 round(max({metric.Column})::numeric, 2)                      as "Max"
             from daily
             where day between @from and @to
             """,
            new { from, to }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// One metric split by whether a kind of context happened, optionally offset.
    /// </summary>
    /// <param name="lagDays">
    /// 0 asks about the same day; 1 asks about the day after — which is usually the question worth
    /// asking, since a late meeting damages the night that follows it, not the one before.
    /// </param>
    public async Task<IReadOnlyList<CorrelationBucket>> CorrelateAsync(
        MetricDefinition metric, string contextKind, int lagDays, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<CorrelationBucket>(new CommandDefinition(
            $"""
             with tagged as (
                 -- context_daily already resolves the local calendar day; shifting it here keeps the
                 -- lag in day units rather than hours, which is what the question means.
                 select distinct day + (@lagDays || ' days')::interval as day
                 from context_daily
                 where kind = @contextKind
             )
             select
                 case when t.day is null then 'without' else 'with' end      as "Bucket",
                 count(*)::int                                                as "Days",
                 round(avg(d.{metric.Column})::numeric, 2)                    as "Mean",
                 round(percentile_cont(0.5) within group (order by d.{metric.Column})::numeric, 2) as "Median",
                 round(stddev_samp(d.{metric.Column})::numeric, 2)            as "StdDev"
             from daily d
             left join tagged t on t.day = d.day
             where d.{metric.Column} is not null
             group by 1
             order by 1
             """,
            new { contextKind, lagDays }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToArray();
    }

    /// <summary>
    /// The days furthest from the series mean, by absolute z-score.
    /// </summary>
    /// <remarks>
    /// Deliberately two-sided. "My worst nights" is the obvious question, but a run of unusually good
    /// ones is the same kind of signal and just as worth explaining.
    /// </remarks>
    public async Task<IReadOnlyList<OutlierDay>> OutlierDaysAsync(
        MetricDefinition metric, int count, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<OutlierDay>(new CommandDefinition(
            $"""
             with stats as (
                 select avg({metric.Column}) as mean, stddev_samp({metric.Column}) as sd
                 from daily where {metric.Column} is not null
             )
             select
                 d.day                                                          as "Day",
                 round(d.{metric.Column}::numeric, 2)                           as "Value",
                 round(((d.{metric.Column} - s.mean) / nullif(s.sd, 0))::numeric, 2) as "ZScore",
                 round(s.mean::numeric, 2)                                      as "SeriesMean"
             from daily d cross join stats s
             where d.{metric.Column} is not null
             order by abs((d.{metric.Column} - s.mean) / nullif(s.sd, 0)) desc nulls last
             limit @count
             """,
            new { count }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToArray();
    }

    /// <summary>A week against the one before it, for every metric at once.</summary>
    public async Task<IReadOnlyList<WeeklyMetric>> WeeklyDigestAsync(
        DateOnly weekStart, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Built from the whitelist rather than written out by hand, so a metric added there shows
        // up here without a second edit. One SELECT per metric, unioned — Postgres has no way to
        // pivot a variable set of columns into rows, and the alternative is a jsonb round trip.
        var branches = DailyMetric.Names.Select(name =>
        {
            var definition = DailyMetric.Resolve(name);
            return $"""
                select '{name}' as "Metric",
                       round(avg({definition.Column}) filter (where day >= @weekStart and day < @weekStart + 7)::numeric, 2)  as "ThisWeek",
                       round(avg({definition.Column}) filter (where day >= @weekStart - 7 and day < @weekStart)::numeric, 2) as "PriorWeek",
                       count({definition.Column}) filter (where day >= @weekStart and day < @weekStart + 7)::int             as "Days"
                from daily
                """;
        });

        var rows = await connection.QueryAsync<WeeklyMetric>(new CommandDefinition(
            string.Join("\nunion all\n", branches),
            new { weekStart }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Where(r => r.Days > 0).ToArray();
    }

    /// <summary>
    /// One night, compressed: headline numbers, phase minutes, and HRV in hour-long bands.
    /// </summary>
    /// <remarks>
    /// The bands are the point. A night holds ~100 HRV samples; returning eight numbers keeps the
    /// shape — where recovery started, where it collapsed — without the caller paging through an
    /// array it will only average anyway.
    /// </remarks>
    public async Task<NightDetail?> NightDetailAsync(DateOnly night, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var headline = await connection.QuerySingleOrDefaultAsync<NightHeadline>(new CommandDefinition(
            """
            select
                d.day                                    as "Night",
                d.readiness_score                        as "Readiness",
                d.sleep_score                            as "SleepScore",
                d.hrv_avg                                as "HrvAvg",
                d.rhr_lowest                             as "RestingHr",
                round(d.temp_deviation, 2)               as "TempDeviation",
                s.bedtime_start                          as "BedtimeStart",
                s.bedtime_end                            as "BedtimeEnd"
            from daily d
            left join lateral (
                select bedtime_start, bedtime_end from sleep_sessions
                where night = d.day order by bedtime_start limit 1
            ) s on true
            where d.day = @night
            """,
            new { night }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (headline is null)
        {
            return null;
        }

        var phases = await connection.QueryAsync<PhaseMinutes>(new CommandDefinition(
            """
            select case phase when 1 then 'deep' when 2 then 'light' when 3 then 'rem' when 4 then 'awake' end as "Phase",
                   -- hypnogram_nightly.minutes is count(*) * 5, so bigint. Dapper will not narrow
                   -- that to the record's int and throws about a missing constructor instead.
                   minutes::int as "Minutes"
            from hypnogram_nightly where night = @night order by phase
            """,
            new { night }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        // The offset has to be computed in a CTE first: Postgres rejects a window function inside
        // another window's PARTITION BY, and grouping is clearer than nesting them anyway.
        var bands = await connection.QueryAsync<HourBand>(new CommandDefinition(
            """
            with banded as (
                select
                    metric,
                    floor(extract(epoch from (ts - first_value(ts) over (partition by metric order by ts))) / 3600)::int as hours_in,
                    value
                from sleep_series
                where night = @night and value is not null
            )
            -- Column order, not just the aliases: Dapper matches a record's constructor parameters
            -- positionally against the reader's columns, and reports a missing constructor when
            -- they are merely out of order.
            select hours_in as "HoursIn", metric as "Metric", round(avg(value)::numeric, 1) as "Mean"
            from banded
            group by metric, hours_in
            order by metric, hours_in
            """,
            new { night }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new NightDetail(
            headline,
            phases.ToArray(),
            bands.ToArray());
    }

    /// <summary>What context kinds exist, so a caller can ask about them without guessing.</summary>
    public async Task<IReadOnlyList<ContextKind>> ContextKindsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<ContextKind>(new CommandDefinition(
            """
            -- context_daily is already grouped by (day, kind), so count(*) here would be days.
            select kind as "Kind", sum(events)::int as "Events", min(day) as "FirstDay", max(day) as "LastDay"
            from context_daily group by kind order by sum(events) desc
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToArray();
    }

    /// <summary>Coverage, so a caller can tell "no correlation" from "no data".</summary>
    public async Task<WarehouseCoverage> CoverageAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleAsync<WarehouseCoverage>(new CommandDefinition(
            """
            select
                (select min(day) from daily)                as "FirstDay",
                (select max(day) from daily)                as "LastDay",
                (select count(*)::int from daily)           as "Days",
                (select count(*)::int from sleep_sessions)  as "Nights",
                (select count(*)::int from workouts)        as "Workouts",
                (select count(*)::int from context)         as "ContextEvents"
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}

public sealed record PeriodSummary(
    DateOnly From, DateOnly To, int Days, decimal? Mean, decimal? Median, decimal? StdDev, decimal? Min, decimal? Max);

public sealed record CorrelationBucket(string Bucket, int Days, decimal? Mean, decimal? Median, decimal? StdDev);

public sealed record OutlierDay(DateOnly Day, decimal Value, decimal? ZScore, decimal SeriesMean);

public sealed record WeeklyMetric(string Metric, decimal? ThisWeek, decimal? PriorWeek, int Days);

public sealed record NightHeadline(
    DateOnly Night, int? Readiness, int? SleepScore, decimal? HrvAvg, decimal? RestingHr,
    decimal? TempDeviation, DateTime? BedtimeStart, DateTime? BedtimeEnd);

public sealed record PhaseMinutes(string Phase, int Minutes);

public sealed record HourBand(int HoursIn, string Metric, decimal Mean);

public sealed record NightDetail(
    NightHeadline Headline, IReadOnlyList<PhaseMinutes> Phases, IReadOnlyList<HourBand> HourBands);

public sealed record ContextKind(string Kind, int Events, DateOnly FirstDay, DateOnly LastDay);

public sealed record WarehouseCoverage(
    DateOnly? FirstDay, DateOnly? LastDay, int Days, int Nights, int Workouts, int ContextEvents);
