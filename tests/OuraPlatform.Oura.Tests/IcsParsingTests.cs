using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace OuraPlatform.Oura.Tests;

/// <summary>
/// The iCalendar assumptions <c>CalendarImporter</c> is built on, pinned against the library rather
/// than trusted. Every one of these was wrong in a first draft, and each fails silently: a recurring
/// meeting collapsing to one row looks exactly like a calendar with one meeting.
/// </summary>
public sealed class IcsParsingTests
{
    private const string Feed = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//EN
        BEGIN:VEVENT
        UID:standup@example.com
        DTSTART:20260803T060000Z
        DTEND:20260803T061500Z
        RRULE:FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR
        SUMMARY:Standup
        END:VEVENT
        BEGIN:VEVENT
        UID:retro@example.com
        DTSTART:20260804T150000Z
        DURATION:PT2H30M
        SUMMARY:Retro
        END:VEVENT
        BEGIN:VEVENT
        UID:birthday@example.com
        DTSTART;VALUE=DATE:20260805
        DTEND;VALUE=DATE:20260806
        SUMMARY:Someone's birthday
        END:VEVENT
        END:VCALENDAR
        """;

    private static List<Ical.Net.DataTypes.Occurrence> Occurrences(string from, string to) =>
        Calendar.Load(Feed)!
            .GetOccurrences<CalendarEvent>(new CalDateTime(DateTime.Parse(from).ToUniversalTime(), "UTC"))
            .TakeWhile(o => o.Period.StartTime.AsUtc < DateTime.Parse(to).ToUniversalTime())
            .ToList();

    /// <summary>
    /// A recurring event carries one UID for the whole series. The importer folds the occurrence
    /// start into the key precisely because of this — without it, a daily standup upserts onto
    /// itself and the warehouse ends up holding exactly one.
    /// </summary>
    [Fact]
    public void A_recurring_event_expands_to_many_occurrences_sharing_one_uid()
    {
        var standups = Occurrences("2026-08-03T00:00:00Z", "2026-08-17T00:00:00Z")
            .Where(o => ((CalendarEvent)o.Source).Uid == "standup@example.com")
            .ToList();

        Assert.Equal(10, standups.Count);
        Assert.Single(standups.Select(o => ((CalendarEvent)o.Source).Uid).Distinct());

        var keys = standups
            .Select(o => $"{((CalendarEvent)o.Source).Uid}:{o.Period.StartTime.AsUtc:yyyy-MM-ddTHH:mm:ssZ}")
            .ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    /// <summary>An event may carry DURATION instead of DTEND. Reading EndTime alone loses the
    /// duration and the event looks instantaneous.</summary>
    [Fact]
    public void An_event_with_duration_and_no_dtend_still_has_an_end()
    {
        var retro = Occurrences("2026-08-04T00:00:00Z", "2026-08-05T00:00:00Z")
            .Single(o => ((CalendarEvent)o.Source).Uid == "retro@example.com");

        Assert.NotNull(retro.Period.EffectiveEndTime);
        Assert.Equal(
            new DateTime(2026, 8, 4, 17, 30, 0, DateTimeKind.Utc),
            retro.Period.EffectiveEndTime!.AsUtc);
    }

    /// <summary>All-day events would otherwise contribute 24 hours each and swamp every duration
    /// sum, which is why the importer skips them by default.</summary>
    [Fact]
    public void All_day_events_are_identifiable()
    {
        var birthday = Occurrences("2026-08-05T00:00:00Z", "2026-08-06T00:00:00Z")
            .Select(o => (CalendarEvent)o.Source)
            .Single(e => e.Uid == "birthday@example.com");

        Assert.True(birthday.IsAllDay);
    }

    /// <summary>The window has to be closed by the caller: the sequence is lazy and unbounded, so a
    /// feed with an unterminated RRULE would otherwise never finish.</summary>
    [Fact]
    public void The_occurrence_sequence_is_bounded_only_by_the_caller()
    {
        var oneWeek = Occurrences("2026-08-03T00:00:00Z", "2026-08-10T00:00:00Z");
        var twoWeeks = Occurrences("2026-08-03T00:00:00Z", "2026-08-17T00:00:00Z");

        Assert.True(twoWeeks.Count > oneWeek.Count);
        Assert.All(oneWeek, o => Assert.True(o.Period.StartTime.AsUtc < new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Occurrences_arrive_in_chronological_order()
    {
        // TakeWhile is only a correct window if this holds.
        var starts = Occurrences("2026-08-03T00:00:00Z", "2026-08-17T00:00:00Z")
            .Select(o => o.Period.StartTime.AsUtc)
            .ToList();

        Assert.Equal(starts.OrderBy(s => s), starts);
    }
}
