namespace OuraPlatform.Oura.Models;

/// <summary>
/// One heart-rate reading. Despite the docs' "5-minute increments" claim, daytime coverage is
/// sparse and irregular — the ring measures opportunistically. Gaps are not data loss.
/// </summary>
/// <param name="TimestampUnix">
/// Declared required by the spec but absent from every sandbox response, which sends a
/// <c>producer_timestamp</c> instead. Deliberately optional; <paramref name="Timestamp"/> is the
/// field that is actually always present, and the verbatim payload lands in <c>oura_raw</c> either
/// way.
/// </param>
public sealed record HeartRateRow(
    DateTimeOffset Timestamp,
    int Bpm,
    // awake | workout | rest | sleep | live | session
    string Source,
    long? TimestampUnix);

public sealed record RingBatteryLevelRow(
    DateTimeOffset Timestamp,
    int Level,
    bool? Charging,
    bool? InCharger,
    long? TimestampUnix);
