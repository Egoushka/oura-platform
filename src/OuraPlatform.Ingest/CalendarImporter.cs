using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Microsoft.Extensions.Options;
using OuraPlatform.Storage;

namespace OuraPlatform.Ingest;

public sealed class ContextOptions
{
    public const string SectionName = "Context";

    /// <summary>Feeds to poll. Empty means the importer does nothing, which is the default.</summary>
    public List<CalendarFeed> Calendars { get; set; } = [];

    /// <summary>How often to re-read the feeds. Calendars change during the day, but not so fast
    /// that a nightly correlation cares.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(3);

    /// <summary>How far back to expand recurring events. A feed rarely carries more history than
    /// this anyway; the bound exists so an infinite RRULE cannot run forever.</summary>
    public int LookbackDays { get; set; } = 400;

    public int LookaheadDays { get; set; } = 14;

    /// <summary>IANA zone the "was it an evening meeting" question is asked in. Wall-clock, so it
    /// has to be a real zone rather than a fixed offset — DST moves the answer by an hour.</summary>
    public string TimeZone { get; set; } = "Europe/Kyiv";
}

public sealed class CalendarFeed
{
    /// <summary>Short name; becomes part of the context source, e.g. <c>calendar:work</c>.</summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Google Calendar's "Secret address in iCal format", or any ICS URL.
    /// </summary>
    /// <remarks>
    /// Deliberately not the Google Calendar API. That would mean a second OAuth client, a second
    /// rotating refresh token and a second copy of the most correctness-critical code in this repo,
    /// to read data that is already published as a static file. The secret URL is one string in
    /// <c>.env.enc</c> with nothing to rotate.
    /// <para>
    /// The cost is freshness: Google regenerates these feeds on its own schedule, sometimes hours
    /// behind. That is irrelevant here — Oura's own nightly data does not land until mid-morning, so
    /// nothing correlates same-day anyway.
    /// </para>
    /// </remarks>
    [Required]
    public string Url { get; set; } = string.Empty;

    /// <summary>What kind of context this feed produces: meeting, yoga, travel, oncall.</summary>
    public string Kind { get; set; } = "meeting";

    /// <summary>Events whose summary matches any of these (case-insensitive substring) are skipped —
    /// for the all-day "birthday" and "week 32" noise that would otherwise count as meetings.</summary>
    public List<string> ExcludeContaining { get; set; } = [];

    /// <summary>All-day events have no meaningful start hour and would drag every duration
    /// calculation to 24 hours. Skipped unless a feed is genuinely about whole days (travel).</summary>
    public bool IncludeAllDay { get; set; }
}

/// <summary>
/// Fills <c>context</c> from iCalendar feeds. This is the half of the warehouse Oura cannot see, and
/// the reason the distribution dashboards have a join that currently matches nothing.
/// </summary>
public sealed class CalendarImporter
{
    public const string HttpClientName = "calendar";

    /// <summary>Backstop against a feed with an unterminated recurrence rule. A decade of a daily
    /// standup is ~3,650 rows, so this is far above anything legitimate.</summary>
    private const int MaxOccurrencesPerFeed = 20_000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ContextRepository _context;
    private readonly ContextOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<CalendarImporter> _logger;

    public CalendarImporter(
        IHttpClientFactory httpClientFactory,
        ContextRepository context,
        IOptions<ContextOptions> options,
        TimeProvider time,
        ILogger<CalendarImporter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _context = context;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public bool HasFeeds => _options.Calendars.Count > 0;

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var imported = 0;

        foreach (var feed in _options.Calendars)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                imported += await ImportAsync(feed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One bad feed must not stop the others, same as a failing Oura collection.
                _logger.LogError(exception, "Calendar '{Feed}' failed to import; continuing.", feed.Name);
            }
        }

        return imported;
    }

    private async Task<int> ImportAsync(CalendarFeed feed, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var from = now.AddDays(-_options.LookbackDays);
        var to = now.AddDays(_options.LookaheadDays);
        var source = $"calendar:{feed.Name}";

        var http = _httpClientFactory.CreateClient(HttpClientName);

        // The URL is a bearer credential in its own right — anyone holding it can read the calendar.
        // Nothing here logs it, and failures report the feed's name instead.
        using var response = await http.GetAsync(feed.Url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Calendar '{feed.Name}' returned {(int)response.StatusCode}.", null, response.StatusCode);
        }

        var ics = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // A revoked or mistyped secret URL answers 200 with an HTML login page, not a 4xx. Loading
        // that yields null rather than throwing, which would otherwise surface as "0 events" and
        // look exactly like an empty calendar.
        var calendar = Calendar.Load(ics)
            ?? throw new InvalidOperationException(
                $"Calendar '{feed.Name}' did not return parseable iCalendar data. " +
                "The secret URL is probably wrong or revoked.");

        var events = new List<ContextEvent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // GetOccurrences returns a lazy, chronologically ordered, *unbounded* sequence — an
        // unterminated RRULE runs forever. TakeWhile closes the window; Take is the backstop for the
        // case where the ordering assumption ever stops holding.
        var occurrences = calendar
            .GetOccurrences<CalendarEvent>(new CalDateTime(from.UtcDateTime, "UTC"))
            .TakeWhile(o => o.Period.StartTime.AsUtc < to.UtcDateTime)
            .Take(MaxOccurrencesPerFeed);

        foreach (var occurrence in occurrences)
        {
            if (occurrence.Source is not CalendarEvent calendarEvent)
            {
                continue;
            }

            if (ShouldSkip(feed, calendarEvent))
            {
                continue;
            }

            var start = new DateTimeOffset(occurrence.Period.StartTime.AsUtc, TimeSpan.Zero);
            // EffectiveEndTime, not EndTime: an event may carry DURATION instead of DTEND, and only
            // the effective one resolves both.
            var end = occurrence.Period.EffectiveEndTime is { } endTime
                ? new DateTimeOffset(endTime.AsUtc, TimeSpan.Zero)
                : (DateTimeOffset?)null;

            // A recurring event shares one UID across every occurrence. Without the start folded in,
            // a weekly standup would upsert onto itself and the warehouse would hold exactly one.
            var sourceId = $"{calendarEvent.Uid}:{start.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
            if (!seen.Add(sourceId))
            {
                continue;
            }

            events.Add(new ContextEvent(
                source,
                sourceId,
                start,
                end,
                feed.Kind,
                calendarEvent.Summary,
                JsonSerializer.Serialize(new
                {
                    calendar = feed.Name,
                    location = calendarEvent.Location,
                    attendees = calendarEvent.Attendees?.Count ?? 0,
                    all_day = calendarEvent.IsAllDay,
                    recurring = calendarEvent.RecurrenceRule is not null,
                })));
        }

        var written = await _context.UpsertAsync(events, cancellationToken).ConfigureAwait(false);

        // A cancelled meeting stops appearing in the feed rather than being marked deleted, so
        // anything inside the window that is no longer listed has to go.
        var pruned = await _context
            .PruneMissingAsync(source, from, to, seen, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Calendar '{Feed}': {Written} event(s) upserted, {Pruned} no longer in the feed removed.",
            feed.Name, written, pruned);

        return written;
    }

    private static bool ShouldSkip(CalendarFeed feed, CalendarEvent calendarEvent)
    {
        if (calendarEvent.IsAllDay && !feed.IncludeAllDay)
        {
            return true;
        }

        // A declined invitation still sits in the feed. It is not a thing that happened.
        if (string.Equals(calendarEvent.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var summary = calendarEvent.Summary ?? string.Empty;
        return feed.ExcludeContaining.Any(
            pattern => summary.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }
}
