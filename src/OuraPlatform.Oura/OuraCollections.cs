using OuraPlatform.Oura.Models;

namespace OuraPlatform.Oura;

/// <summary>How a collection is filtered and paged.</summary>
public enum OuraQueryStyle
{
    /// <summary><c>start_date</c> / <c>end_date</c>, one document per day.</summary>
    Daily,

    /// <summary><c>start_datetime</c> / <c>end_datetime</c>, discrete rows.</summary>
    TimeSeries,

    /// <summary>Paged but not filterable by date (<c>ring_configuration</c>).</summary>
    Undated,

    /// <summary>Bare document, no <c>data</c> envelope (<c>personal_info</c>).</summary>
    Singleton,
}

/// <param name="Name">Webhook <c>data_type</c> spelling — the key used in <c>oura_raw.doc_type</c>.</param>
/// <param name="Path">REST path segment. Differs from <paramref name="Name"/> for vO2_max.</param>
/// <param name="HasSandbox">False for <c>personal_info</c>, which 404s in the sandbox.</param>
public sealed record OuraCollection(
    string Name,
    string Path,
    OuraQueryStyle Style,
    Type DocumentType,
    bool HasSandbox = true);

public static class OuraCollections
{
    /// <summary>Everything with a REST endpoint. The webhook enum additionally lists
    /// <c>meal</c>, which has no endpoint to poll.</summary>
    public static readonly IReadOnlyList<OuraCollection> All =
    [
        new("daily_activity", "daily_activity", OuraQueryStyle.Daily, typeof(DailyActivity)),
        new("daily_readiness", "daily_readiness", OuraQueryStyle.Daily, typeof(DailyReadiness)),
        new("daily_sleep", "daily_sleep", OuraQueryStyle.Daily, typeof(DailySleep)),
        new("daily_spo2", "daily_spo2", OuraQueryStyle.Daily, typeof(DailySpo2)),
        new("daily_stress", "daily_stress", OuraQueryStyle.Daily, typeof(DailyStress)),
        new("daily_resilience", "daily_resilience", OuraQueryStyle.Daily, typeof(DailyResilience)),
        new("daily_cardiovascular_age", "daily_cardiovascular_age", OuraQueryStyle.Daily, typeof(DailyCardiovascularAge)),
        // Webhook enum spells this lowercase; the REST path has a capital O.
        new("vo2_max", "vO2_max", OuraQueryStyle.Daily, typeof(VO2Max)),
        new("sleep", "sleep", OuraQueryStyle.Daily, typeof(SleepPeriod)),
        new("sleep_time", "sleep_time", OuraQueryStyle.Daily, typeof(SleepTime)),
        new("session", "session", OuraQueryStyle.Daily, typeof(Session)),
        new("workout", "workout", OuraQueryStyle.Daily, typeof(Workout)),
        new("enhanced_tag", "enhanced_tag", OuraQueryStyle.Daily, typeof(EnhancedTag)),
        new("rest_mode_period", "rest_mode_period", OuraQueryStyle.Daily, typeof(RestModePeriod)),
        // Deprecated by Oura in favour of enhanced_tag. Modelled and fixture-tested, not ingested.
        new("tag", "tag", OuraQueryStyle.Daily, typeof(LegacyTag)),
        new("heartrate", "heartrate", OuraQueryStyle.TimeSeries, typeof(HeartRateRow)),
        new("ring_battery_level", "ring_battery_level", OuraQueryStyle.TimeSeries, typeof(RingBatteryLevelRow)),
        new("ring_configuration", "ring_configuration", OuraQueryStyle.Undated, typeof(RingConfiguration)),
        new("personal_info", "personal_info", OuraQueryStyle.Singleton, typeof(PersonalInfo), HasSandbox: false),
    ];

    /// <summary>What the ingest actually polls: everything except the deprecated <c>tag</c>.</summary>
    public static readonly IReadOnlyList<OuraCollection> Ingested =
        All.Where(c => c.Name != "tag").ToArray();

    /// <summary>Against the sandbox, drops the collections it does not serve — <c>personal_info</c>
    /// answers 404 there.</summary>
    public static IReadOnlyList<OuraCollection> ForIngest(bool useSandbox) =>
        useSandbox ? Ingested.Where(c => c.HasSandbox).ToArray() : Ingested;

    public static OuraCollection ByName(string name) =>
        All.FirstOrDefault(c => c.Name == name)
        ?? throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown Oura collection.");
}
