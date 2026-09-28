using System.Text.Json.Serialization;

namespace OuraPlatform.Oura.Models;

// Oura's string enums (ring colour, workout intensity, sleep type, tag codes, ...) are modelled as
// plain strings. They grow between spec revisions and a closed enum would throw on the first new
// value, aborting a whole page. The projection layer writes them to text columns anyway.

public sealed record ActivityContributors(
    int? MeetDailyTargets,
    int? MoveEveryHour,
    int? RecoveryTime,
    int? StayActive,
    int? TrainingFrequency,
    int? TrainingVolume);

public sealed record DailyActivity(
    string Id,
    DateOnly Day,
    DateTimeOffset Timestamp,
    int? Score,
    ActivityContributors Contributors,
    int ActiveCalories,
    int TotalCalories,
    int TargetCalories,
    double AverageMetMinutes,
    int HighActivityMetMinutes,
    int MediumActivityMetMinutes,
    int LowActivityMetMinutes,
    int SedentaryMetMinutes,
    int HighActivityTime,
    int MediumActivityTime,
    int LowActivityTime,
    int SedentaryTime,
    int RestingTime,
    int NonWearTime,
    int InactivityAlerts,
    int Steps,
    int EquivalentWalkingDistance,
    int MetersToTarget,
    int TargetMeters,
    PublicSample Met,
    // One character per 5 minutes of the day; activity class codes.
    [property: JsonPropertyName("class_5_min")] string? Class5Min);

public sealed record ReadinessContributors(
    int? ActivityBalance,
    int? BodyTemperature,
    int? HrvBalance,
    int? PreviousDayActivity,
    int? PreviousNight,
    int? RecoveryIndex,
    int? RestingHeartRate,
    int? SleepBalance,
    int? SleepRegularity);

public sealed record DailyReadiness(
    string Id,
    DateOnly Day,
    DateTimeOffset Timestamp,
    int? Score,
    ReadinessContributors Contributors,
    double? TemperatureDeviation,
    double? TemperatureTrendDeviation);

public sealed record SleepContributors(
    int? DeepSleep,
    int? Efficiency,
    int? Latency,
    int? RemSleep,
    int? Restfulness,
    int? Timing,
    int? TotalSleep);

public sealed record DailySleep(
    string Id,
    DateOnly Day,
    DateTimeOffset Timestamp,
    int? Score,
    SleepContributors Contributors);

public sealed record Spo2AggregatedValues(double Average);

public sealed record DailySpo2(
    string Id,
    DateOnly Day,
    int? BreathingDisturbanceIndex,
    Spo2AggregatedValues? Spo2Percentage);

public sealed record DailyStress(
    string Id,
    DateOnly Day,
    // Seconds spent in high stress. Not a score.
    int? StressHigh,
    int? RecoveryHigh,
    // restored | normal | stressful
    string? DaySummary);

public sealed record ResilienceContributors(
    double SleepRecovery,
    double DaytimeRecovery,
    double Stress);

public sealed record DailyResilience(
    string Id,
    DateOnly Day,
    ResilienceContributors Contributors,
    // limited | adequate | solid | strong | exceptional
    string Level);

public sealed record DailyCardiovascularAge(
    string Id,
    DateOnly Day,
    double? PulseWaveVelocity,
    int? VascularAge);

/// <summary>Served from the capital-O path <c>/v2/usercollection/vO2_max</c>.</summary>
public sealed record VO2Max(
    string Id,
    DateOnly Day,
    DateTimeOffset Timestamp,
    int Vo2Max);
