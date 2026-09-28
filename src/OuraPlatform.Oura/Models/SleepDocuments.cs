using System.Text.Json.Serialization;

namespace OuraPlatform.Oura.Models;

/// <summary>Readiness snapshot embedded in a <see cref="SleepPeriod"/>.</summary>
public sealed record EmbeddedReadiness(
    ReadinessContributors Contributors,
    int? Score,
    double? TemperatureDeviation,
    double? TemperatureTrendDeviation);

/// <summary>
/// One sleep period (<c>/v2/usercollection/sleep</c>). The richest object in the API: it carries
/// per-interval HRV and heart rate across the whole night plus the hypnogram.
/// </summary>
public sealed record SleepPeriod(
    string Id,
    DateOnly Day,
    DateTimeOffset BedtimeStart,
    DateTimeOffset BedtimeEnd,
    // deleted | sleep | long_sleep | late_nap | rest
    string? Type,
    int Period,
    int TimeInBed,
    int? TotalSleepDuration,
    int? AwakeTime,
    int? DeepSleepDuration,
    int? LightSleepDuration,
    int? RemSleepDuration,
    int? Latency,
    int? Efficiency,
    int? RestlessPeriods,
    double? AverageBreath,
    double? AverageHeartRate,
    // Computed by Oura on 30-second samples; will not equal min() of HeartRate.Items.
    int? LowestHeartRate,
    int? AverageHrv,
    // interval=300 in production, 60 in the sandbox. Nulls mark gaps.
    PublicSample? HeartRate,
    PublicSample? Hrv,
    // Hypnogram: one digit per 5 minutes. 1=deep 2=light 3=REM 4=awake.
    [property: JsonPropertyName("sleep_phase_5_min")] string? SleepPhase5Min,
    // Same encoding, produced by the phone app rather than the ring pipeline.
    [property: JsonPropertyName("app_sleep_phase_5_min")] string? AppSleepPhase5Min,
    [property: JsonPropertyName("sleep_phase_30_sec")] string? SleepPhase30Sec,
    [property: JsonPropertyName("movement_30_sec")] string? Movement30Sec,
    // v1 | v2
    string? SleepAlgorithmVersion,
    // foreground_sleep_analysis | bedtime_edit | background_sleep_analysis
    // | background_created_foreground_updated
    string? SleepAnalysisReason,
    string? RingId,
    bool LowBatteryAlert,
    int? ReadinessScoreDelta,
    int? SleepScoreDelta,
    EmbeddedReadiness? Readiness);

/// <summary>Offsets are seconds relative to midnight of <see cref="SleepTime.Day"/>; negative
/// values fall on the previous evening.</summary>
public sealed record SleepTimeWindow(int DayTz, int StartOffset, int EndOffset);

public sealed record SleepTime(
    string Id,
    DateOnly Day,
    SleepTimeWindow? OptimalBedtime,
    // improve_efficiency | earlier_bedtime | later_bedtime | earlier_wake_up_time
    // | later_wake_up_time | follow_optimal_bedtime
    string? Recommendation,
    // not_enough_nights | not_enough_recent_nights | bad_sleep_quality
    // | only_recommended_found | optimal_found
    string? Status);
