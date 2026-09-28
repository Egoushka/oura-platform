namespace OuraPlatform.Oura.Models;

public sealed record Workout(
    string Id,
    DateOnly Day,
    DateTimeOffset StartDatetime,
    DateTimeOffset EndDatetime,
    string Activity,
    // easy | moderate | hard
    string Intensity,
    // manual | autodetected | confirmed | workout_heart_rate
    string Source,
    string? Label,
    double? Calories,
    double? Distance);

public sealed record Session(
    string Id,
    DateOnly Day,
    DateTimeOffset StartDatetime,
    DateTimeOffset EndDatetime,
    // breathing | meditation | nap | relaxation | rest | body_status
    string Type,
    // bad | worse | same | good | great
    string? Mood,
    PublicSample? HeartRate,
    PublicSample? HeartRateVariability,
    PublicSample? MotionCount);

/// <summary>Current tag endpoint. Replaces the deprecated <see cref="LegacyTag"/>.</summary>
public sealed record EnhancedTag(
    string Id,
    string? TagTypeCode,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    DateOnly StartDay,
    DateOnly? EndDay,
    string? Comment,
    string? CustomName);

/// <summary><c>/v2/usercollection/tag</c> — deprecated by Oura in favour of
/// <see cref="EnhancedTag"/>. Modelled for completeness; not ingested.</summary>
public sealed record LegacyTag(
    string Id,
    DateOnly Day,
    DateTimeOffset Timestamp,
    string? Text,
    IReadOnlyList<string> Tags);

public sealed record RestModeEpisode(IReadOnlyList<string> Tags, DateTimeOffset Timestamp);

public sealed record RestModePeriod(
    string Id,
    DateOnly StartDay,
    DateOnly? EndDay,
    DateTimeOffset? StartTime,
    DateTimeOffset? EndTime,
    IReadOnlyList<RestModeEpisode> Episodes);
