namespace OuraPlatform.Storage;

/// <summary>
/// The metrics an analytics caller may ask for, and the column each maps to.
/// </summary>
/// <remarks>
/// A whitelist, not string interpolation. Every caller of this is ultimately a language model
/// choosing a name from a prompt, so the column has to come from a fixed set — nothing that reaches
/// the query is ever derived from what was asked for.
/// <para>
/// Oura's 0-100 scores are here because they are worth reporting, but the underlying signals (HRV,
/// resting heart rate, temperature) are the ones worth reasoning about: the scores are computed on
/// the phone by proprietary models and change between app versions.
/// </para>
/// </remarks>
public static class DailyMetric
{
    private static readonly Dictionary<string, MetricDefinition> Definitions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["readiness"] = new("readiness_score", "Readiness score", "score", HigherIsBetter: true),
            ["sleep_score"] = new("sleep_score", "Sleep score", "score", HigherIsBetter: true),
            ["activity"] = new("activity_score", "Activity score", "score", HigherIsBetter: true),
            ["hrv"] = new("hrv_avg", "Average overnight HRV", "ms", HigherIsBetter: true),
            ["resting_hr"] = new("rhr_lowest", "Lowest resting heart rate", "bpm", HigherIsBetter: false),
            ["temperature"] = new("temp_deviation", "Temperature deviation from baseline", "C", HigherIsBetter: false),
            ["spo2"] = new("spo2_avg", "Average overnight SpO2", "%", HigherIsBetter: true),
            ["breathing_disturbance"] = new("breathing_disturbance_index", "Breathing disturbance index", "index", HigherIsBetter: false),
            ["stress_high"] = new("stress_high_sec", "Seconds in high stress", "s", HigherIsBetter: false),
            ["recovery_high"] = new("recovery_high_sec", "Seconds in high recovery", "s", HigherIsBetter: true),
            ["steps"] = new("steps", "Steps", "steps", HigherIsBetter: true),
            ["active_calories"] = new("active_calories", "Active calories", "kcal", HigherIsBetter: true),
            ["vo2_max"] = new("vo2_max", "VO2 max", "ml/kg/min", HigherIsBetter: true),
            ["vascular_age"] = new("vascular_age", "Vascular age", "years", HigherIsBetter: false),
        };

    public static IReadOnlyCollection<string> Names => Definitions.Keys;

    /// <summary>One line listing every metric and its unit, for a tool description.</summary>
    public static string Describe() =>
        string.Join(", ", Definitions.Select(kvp => $"{kvp.Key} ({kvp.Value.Unit})"));

    public static MetricDefinition Resolve(string? metric) =>
        Definitions.TryGetValue(metric?.Trim() ?? string.Empty, out var definition)
            ? definition
            : throw new ArgumentException(
                $"Unknown metric '{metric}'. Available: {string.Join(", ", Definitions.Keys)}.", nameof(metric));
}

/// <param name="Column">
/// Interpolated into SQL, which is safe only because it can never be anything other than a value
/// from the dictionary above.
/// </param>
/// <param name="HigherIsBetter">
/// So a caller can be told whether a change is an improvement without having to already know that a
/// lower resting heart rate is good and a lower HRV is not.
/// </param>
public sealed record MetricDefinition(string Column, string Label, string Unit, bool HigherIsBetter);
