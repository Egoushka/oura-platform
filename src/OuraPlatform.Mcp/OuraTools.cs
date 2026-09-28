using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using OuraPlatform.Storage;

namespace OuraPlatform.Mcp;

/// <summary>
/// The tool surface over the warehouse.
/// </summary>
/// <remarks>
/// Every tool answers with an aggregate and a unit. Nothing here returns a 5-minute array: the
/// caller is a language model, and a night is ~200 samples it would only average back down again.
/// <para>
/// Metric names are not listed in the descriptions because the catalogue lives in
/// <see cref="DailyMetric"/> and an attribute needs a compile-time constant. <c>coverage</c> returns
/// it instead, and <see cref="DailyMetric.Resolve"/> names every valid metric when it rejects one,
/// so a wrong guess costs one round trip rather than a wrong answer.
/// </para>
/// </remarks>
[McpServerToolType]
public static class OuraTools
{
    internal const string Instructions =
        """
        Oura Ring warehouse. Answers questions about sleep, recovery, heart rate and daily activity
        by querying a Postgres/TimescaleDB copy of the ring's data, joined against calendar and tag
        context.

        Start with `coverage`: it reports the date span actually held and the metric catalogue every
        other tool takes a name from. Start with `context_kinds` before `correlate`, so an empty
        answer can be read as "nothing recorded" rather than "no relationship".

        Missing data is absent, never zero. A day the ring was not worn produces no row, and a
        metric with zero days in a window means it was never recorded, not that it was nil.
        """;

    // ----------------------------------------------------------------------
    // Discovery
    // ----------------------------------------------------------------------

    [McpServerTool(Name = "coverage", Title = "What the warehouse holds", ReadOnly = true, Idempotent = true)]
    [Description(
        "The date span and row counts actually in the warehouse, plus the catalogue of metric names "
        + "every other tool accepts. Call this first: it is what separates 'no effect' from 'no data'.")]
    public static async Task<CoverageReport> Coverage(
        AnalyticsRepository analytics,
        CancellationToken cancellationToken)
    {
        var coverage = await analytics.CoverageAsync(cancellationToken);

        var metrics = DailyMetric.Names
            .Select(name => Describe(name, DailyMetric.Resolve(name)))
            .ToArray();

        return new CoverageReport(coverage, metrics);
    }

    [McpServerTool(Name = "context_kinds", Title = "Context kinds available to correlate against", ReadOnly = true, Idempotent = true)]
    [Description(
        "The kinds of non-Oura context recorded — calendar meetings, Oura tags — with how many "
        + "events of each and over what span. These are the values `correlate` takes.")]
    public static async Task<IReadOnlyList<ContextKind>> ContextKinds(
        AnalyticsRepository analytics,
        CancellationToken cancellationToken) =>
        await analytics.ContextKindsAsync(cancellationToken);

    // ----------------------------------------------------------------------
    // Analysis
    // ----------------------------------------------------------------------

    [McpServerTool(Name = "compare_periods", Title = "Compare one metric across two date ranges", ReadOnly = true, Idempotent = true)]
    [Description(
        "Distribution of one metric over a date range, against a baseline range. With no baseline "
        + "given it uses the equally long window immediately before, which answers 'how was this "
        + "month compared with last'. `direction` already accounts for metrics where lower is better.")]
    public static async Task<PeriodComparison> ComparePeriods(
        AnalyticsRepository analytics,
        [Description("Metric name from the `coverage` catalogue, e.g. hrv, resting_hr, sleep_score.")]
        string metric,
        [Description("First day of the period, yyyy-MM-dd.")] string from,
        [Description("Last day of the period, inclusive, yyyy-MM-dd.")] string to,
        [Description("Optional first day of the baseline period, yyyy-MM-dd.")]
        string? baselineFrom = null,
        [Description("Optional last day of the baseline period, inclusive, yyyy-MM-dd.")]
        string? baselineTo = null,
        CancellationToken cancellationToken = default)
    {
        var definition = Resolve(metric);
        var start = ParseDay(from, nameof(from));
        var end = ParseDay(to, nameof(to));

        if (end < start)
        {
            throw new McpException($"`to` ({end:yyyy-MM-dd}) is before `from` ({start:yyyy-MM-dd}).");
        }

        // Both ends or neither: half a baseline is a silent unit change, not a convenience.
        if ((baselineFrom is null) != (baselineTo is null))
        {
            throw new McpException("Give both `baselineFrom` and `baselineTo`, or neither.");
        }

        var priorEnd = baselineTo is null ? start.AddDays(-1) : ParseDay(baselineTo, nameof(baselineTo));
        var priorStart = baselineFrom is null
            ? priorEnd.AddDays(-(end.DayNumber - start.DayNumber))
            : ParseDay(baselineFrom, nameof(baselineFrom));

        if (priorEnd < priorStart)
        {
            throw new McpException(
                $"`baselineTo` ({priorEnd:yyyy-MM-dd}) is before `baselineFrom` ({priorStart:yyyy-MM-dd}).");
        }

        var period = await analytics.SummarisePeriodAsync(definition, start, end, cancellationToken);
        var baseline = await analytics.SummarisePeriodAsync(definition, priorStart, priorEnd, cancellationToken);

        return new PeriodComparison(
            Metric: Describe(metric, definition),
            Period: period,
            Baseline: baseline,
            MeanChange: Change(period.Mean, baseline.Mean),
            Direction: Direction(definition, period.Mean, baseline.Mean),
            Note: Thin(period.Days, baseline.Days));
    }

    [McpServerTool(Name = "correlate", Title = "Split a metric by whether something happened", ReadOnly = true, Idempotent = true)]
    [Description(
        "One metric split into the days a kind of context happened and the days it did not. "
        + "`lagDays` shifts the context forward: 1, the default, asks about the night *after* the "
        + "event, which is usually the question meant. Read `note` before drawing a conclusion — "
        + "these buckets are routinely too small to mean anything.")]
    public static async Task<Correlation> Correlate(
        AnalyticsRepository analytics,
        [Description("Metric name from the `coverage` catalogue, e.g. hrv, sleep_score.")]
        string metric,
        [Description("Context kind from `context_kinds`, e.g. meeting, party.")]
        string contextKind,
        [Description("Days to shift the context by. 1 = the day after the event (default). 0 = the same day.")]
        int? lagDays = null,
        CancellationToken cancellationToken = default)
    {
        var definition = Resolve(metric);
        var lag = lagDays ?? 1;

        var buckets = await analytics.CorrelateAsync(definition, contextKind, lag, cancellationToken);

        var with = buckets.SingleOrDefault(b => b.Bucket == "with");
        var without = buckets.SingleOrDefault(b => b.Bucket == "without");

        return new Correlation(
            Metric: Describe(metric, definition),
            ContextKind: contextKind,
            LagDays: lag,
            Buckets: buckets,
            MeanChange: Change(with?.Mean, without?.Mean),
            Direction: Direction(definition, with?.Mean, without?.Mean),
            Note: with is null
                ? $"No days matched '{contextKind}' at a lag of {lag}. Check `context_kinds` — this is absence of data, not absence of an effect."
                : Thin(with.Days, without?.Days ?? 0));
    }

    [McpServerTool(Name = "outlier_nights", Title = "The days furthest from normal", ReadOnly = true, Idempotent = true)]
    [Description(
        "The days whose value for a metric sits furthest from the series mean, by absolute z-score. "
        + "Two-sided on purpose: an unusually good run is the same signal as a bad one and just as "
        + "worth explaining. Pair each day with `night_detail` or `context_kinds` to find out why.")]
    public static async Task<Outliers> OutlierNights(
        AnalyticsRepository analytics,
        [Description("Metric name from the `coverage` catalogue, e.g. hrv, resting_hr.")]
        string metric,
        [Description("How many days to return. Defaults to 5, capped at 30.")]
        int? count = null,
        CancellationToken cancellationToken = default)
    {
        var definition = Resolve(metric);

        // Clamped rather than validated: a model asking for 500 wants "the unusual ones", and the
        // whole design constraint here is that the answer stays small.
        var wanted = Math.Clamp(count ?? 5, 1, 30);

        return new Outliers(
            Describe(metric, definition),
            await analytics.OutlierDaysAsync(definition, wanted, cancellationToken));
    }

    [McpServerTool(Name = "weekly_digest", Title = "A week against the one before it", ReadOnly = true, Idempotent = true)]
    [Description(
        "Every metric at once for one week, beside the week before, with the direction of each "
        + "change. Metrics with no data that week are left out rather than reported as zero. "
        + "Defaults to the last complete Monday-to-Sunday week, not the one currently running.")]
    public static async Task<WeeklyDigestReport> WeeklyDigest(
        AnalyticsRepository analytics,
        [Description("Any day in the week, yyyy-MM-dd; the week is taken as the seven days from it. Defaults to the last complete week.")]
        string? weekStart = null,
        CancellationToken cancellationToken = default)
    {
        // The week currently running is two days long on a Wednesday, and comparing that with seven
        // full days is a unit change the caller will read as a trend. Default to the last complete one.
        var start = weekStart is null ? MostRecentMonday().AddDays(-7) : ParseDay(weekStart, nameof(weekStart));
        var end = start.AddDays(6);

        var rows = await analytics.WeeklyDigestAsync(start, cancellationToken);

        var metrics = rows
            .Select(row =>
            {
                var definition = DailyMetric.Resolve(row.Metric);
                return new WeeklyChange(
                    Describe(row.Metric, definition),
                    row.ThisWeek,
                    row.PriorWeek,
                    row.Days,
                    Change(row.ThisWeek, row.PriorWeek),
                    Direction(definition, row.ThisWeek, row.PriorWeek));
            })
            .ToArray();

        return new WeeklyDigestReport(
            start,
            end,
            metrics,
            end >= DateOnly.FromDateTime(DateTime.Now)
                ? "This week has not finished. Its averages cover fewer days than the week it is compared with."
                : null);
    }

    [McpServerTool(Name = "night_detail", Title = "One night, compressed", ReadOnly = true, Idempotent = true)]
    [Description(
        "One night's headline numbers, minutes in each sleep phase, and HRV and heart rate averaged "
        + "into hour-long bands from sleep onset. The bands are the shape of the night — where "
        + "recovery started and where it collapsed — without the underlying 5-minute arrays.")]
    public static async Task<NightDetail?> NightDetail(
        AnalyticsRepository analytics,
        [Description("The night, yyyy-MM-dd. Oura keys a night on the day it ended, so the night of the 4th into the 5th is 2026-09-05.")]
        string night,
        CancellationToken cancellationToken) =>
        await analytics.NightDetailAsync(ParseDay(night, nameof(night)), cancellationToken);

    // ----------------------------------------------------------------------

    /// <summary>
    /// <see cref="DailyMetric.Resolve"/>, with its rejection turned into something the caller is
    /// allowed to read.
    /// </summary>
    /// <remarks>
    /// The SDK propagates the message of an <see cref="McpException"/> and replaces every other
    /// exception with "An error occurred invoking 'x'". That generic form is useless here: the
    /// whole reason <see cref="DailyMetric"/> names every valid metric when it rejects one is so a
    /// wrong guess costs a round trip instead of an answer.
    /// </remarks>
    private static MetricDefinition Resolve(string metric)
    {
        try
        {
            return DailyMetric.Resolve(metric);
        }
        catch (ArgumentException exception)
        {
            throw new McpException(exception.Message);
        }
    }

    /// <remarks>
    /// Tolerant on the way in because the caller is a language model, which will eventually send a
    /// whole timestamp where a date was asked for. Everything downstream is a <c>date</c>.
    /// </remarks>
    private static DateOnly ParseDay(string value, string parameterName)
    {
        if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return day;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
        {
            return DateOnly.FromDateTime(timestamp.Date);
        }

        throw new McpException($"`{parameterName}`: '{value}' is not a date. Use yyyy-MM-dd.");
    }

    /// <summary>Local, because <c>TZ</c> is set on the container and the context views are local too.</summary>
    private static DateOnly MostRecentMonday()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
    }

    private static MetricInfo Describe(string name, MetricDefinition definition) =>
        new(name, definition.Label, definition.Unit, definition.HigherIsBetter);

    private static decimal? Change(decimal? current, decimal? baseline) =>
        current is null || baseline is null ? null : Math.Round(current.Value - baseline.Value, 2);

    /// <summary>
    /// Whether a change is an improvement, which depends on the metric: a lower resting heart rate
    /// is good and a lower HRV is not. Saying so here stops the caller having to already know.
    /// </summary>
    private static string Direction(MetricDefinition metric, decimal? current, decimal? baseline)
    {
        if (current is null || baseline is null)
        {
            return "unknown";
        }

        var change = current.Value - baseline.Value;
        return change == 0
            ? "unchanged"
            : change > 0 == metric.HigherIsBetter ? "better" : "worse";
    }

    /// <summary>
    /// Fourteen days is not a statistical threshold, it is the point below which a difference of
    /// means over daily physiology is noise. Said out loud because the caller will otherwise report
    /// a two-day bucket as a finding.
    /// </summary>
    private static string? Thin(int days, int baselineDays)
    {
        var smaller = Math.Min(days, baselineDays);
        return smaller < 14
            ? $"Only {smaller} {(smaller == 1 ? "day" : "days")} on the smaller side. Treat any difference as indicative at best."
            : null;
    }
}

public sealed record MetricInfo(string Name, string Label, string Unit, bool HigherIsBetter);

public sealed record CoverageReport(WarehouseCoverage Warehouse, IReadOnlyList<MetricInfo> Metrics);

public sealed record PeriodComparison(
    MetricInfo Metric,
    PeriodSummary Period,
    PeriodSummary Baseline,
    decimal? MeanChange,
    string Direction,
    string? Note);

public sealed record Correlation(
    MetricInfo Metric,
    string ContextKind,
    int LagDays,
    IReadOnlyList<CorrelationBucket> Buckets,
    decimal? MeanChange,
    string Direction,
    string? Note);

public sealed record Outliers(MetricInfo Metric, IReadOnlyList<OutlierDay> Days);

public sealed record WeeklyChange(
    MetricInfo Metric, decimal? ThisWeek, decimal? PriorWeek, int Days, decimal? Change, string Direction);

public sealed record WeeklyDigestReport(
    DateOnly WeekStart, DateOnly WeekEnd, IReadOnlyList<WeeklyChange> Metrics, string? Note);
