using System.Text.Json;
using OuraPlatform.Oura;
using OuraPlatform.Oura.Models;

namespace OuraPlatform.Oura.Tests;

/// <summary>
/// The parts of the mapping that fail quietly rather than loudly: digit-boundary property names,
/// the embedded sample arrays and the sandbox-vs-production differences.
/// </summary>
public sealed class ModelBindingTests
{
    private static OuraPage<T> Page<T>(string collectionName) =>
        JsonSerializer.Deserialize<OuraPage<T>>(
            Fixtures.Sandbox(OuraCollections.ByName(collectionName)), OuraJson.Options)!;

    /// <summary>
    /// <c>JsonNamingPolicy.SnakeCaseLower</c> renders <c>SleepPhase5Min</c> as
    /// <c>sleep_phase5_min</c> — no underscore before the digit. Every such property carries an
    /// explicit <c>[JsonPropertyName]</c>; without it the field binds to null and the hypnogram
    /// silently disappears.
    /// </summary>
    [Fact]
    public void Digit_boundary_properties_bind_despite_the_naming_policy()
    {
        Assert.Equal("sleep_phase5_min", JsonNamingPolicy.SnakeCaseLower.ConvertName("SleepPhase5Min"));

        var sleep = Page<SleepPeriod>("sleep").Data[0];
        Assert.NotNull(sleep.AppSleepPhase5Min);
        Assert.NotNull(sleep.Movement30Sec);

        var activity = Page<DailyActivity>("daily_activity").Data[0];
        Assert.NotNull(activity.Class5Min);
    }

    [Fact]
    public void Sleep_carries_per_interval_hrv_and_heart_rate()
    {
        var sleep = Page<SleepPeriod>("sleep").Data[0];

        Assert.NotNull(sleep.Hrv);
        Assert.NotNull(sleep.HeartRate);
        Assert.NotEmpty(sleep.Hrv.Items);
        Assert.NotEmpty(sleep.HeartRate.Items);

        // Sandbox lies here: production returns interval 300 with items spanning the whole night.
        // Nothing downstream may assume 60 — the parser reads the value.
        Assert.Equal(60d, sleep.Hrv.Interval);
        Assert.Equal(10, sleep.Hrv.Items.Count);
    }

    [Fact]
    public void Sample_items_keep_nulls_for_gaps()
    {
        // Item type is double? precisely so an unmeasured interval survives as a gap rather than a
        // zero. Nothing in the sandbox exercises it, so assert against a synthetic payload.
        const string json = """{"interval": 300, "items": [54.0, null, 61.5], "timestamp": "2026-07-30T23:10:00+03:00"}""";

        var sample = JsonSerializer.Deserialize<PublicSample>(json, OuraJson.Options)!;

        Assert.Equal([54.0, null, 61.5], sample.Items);
        Assert.Equal(TimeSpan.FromHours(3), sample.Timestamp.Offset);
    }

    [Fact]
    public void Localized_timestamps_keep_the_users_utc_offset()
    {
        var episode = Page<RestModePeriod>("rest_mode_period").Data[0].Episodes[0];

        Assert.Equal(TimeSpan.FromHours(2), episode.Timestamp.Offset);
    }

    [Fact]
    public void Heartrate_rows_survive_the_missing_timestamp_unix()
    {
        // The spec marks timestamp_unix required; the sandbox omits it and sends a null
        // producer_timestamp instead. Neither may break deserialization.
        var rows = Page<HeartRateRow>("heartrate").Data;

        Assert.All(rows, row => Assert.Null(row.TimestampUnix));
        Assert.Equal(["awake", "workout"], rows.Select(r => r.Source));
        Assert.All(rows, row => Assert.True(row.Bpm > 0));
    }

    [Fact]
    public void Ring_battery_rows_bind_charging_state()
    {
        var rows = Page<RingBatteryLevelRow>("ring_battery_level").Data;

        Assert.Equal([false, true], rows.Select(r => r.Charging));
        Assert.Equal([85, 95], rows.Select(r => r.Level));
    }

    [Fact]
    public void Nested_documents_bind()
    {
        var sleep = Page<SleepPeriod>("sleep").Data[0];
        Assert.Equal(80, sleep.Readiness?.Score);
        Assert.Equal(80, sleep.Readiness?.Contributors.HrvBalance);

        var spo2 = Page<DailySpo2>("daily_spo2").Data[0];
        Assert.Equal(97d, spo2.Spo2Percentage?.Average);

        var bedtime = Page<SleepTime>("sleep_time").Data[0].OptimalBedtime;
        Assert.Equal(-1080, bedtime?.StartOffset);
    }

    [Fact]
    public void Open_ended_enums_bind_as_strings()
    {
        // Oura adds enum members between spec revisions. A closed enum would throw on the first
        // unseen value and abort the whole page.
        Assert.Equal("gen3", Page<RingConfiguration>("ring_configuration").Data[0].HardwareType);
        Assert.Equal("limited", Page<DailyResilience>("daily_resilience").Data[0].Level);
        Assert.Equal("swimming", Page<Workout>("workout").Data[0].Activity);
        Assert.Equal("generic_happy", Page<EnhancedTag>("enhanced_tag").Data[0].TagTypeCode);
    }

    [Fact]
    public void Missing_scores_bind_as_null_not_zero()
    {
        const string json = """{"id":"x","day":"2026-07-30","timestamp":"2026-07-30T00:00:00+03:00","score":null,"contributors":{}}""";

        var document = JsonSerializer.Deserialize<DailySleep>(json, OuraJson.Options)!;

        Assert.Null(document.Score);
        Assert.Null(document.Contributors.DeepSleep);
    }

    [Fact]
    public void Next_token_is_read_from_the_envelope()
    {
        Assert.Null(Page<DailySleep>("daily_sleep").NextToken);

        const string json = """{"data": [], "next_token": "abc123"}""";
        Assert.Equal("abc123", JsonSerializer.Deserialize<OuraPage<DailySleep>>(json, OuraJson.Options)!.NextToken);
    }

    /// <summary>Spec 1.34 removed the per-document <c>meta</c> wrapper. Anything written against
    /// the old shape unwraps <c>data.meta</c> and finds nothing.</summary>
    [Fact]
    public void Documents_have_no_meta_envelope()
    {
        using var recorded = JsonDocument.Parse(Fixtures.Sandbox(OuraCollections.ByName("daily_sleep")));
        var document = recorded.RootElement.GetProperty("data")[0];

        Assert.False(document.TryGetProperty("meta", out _));
    }
}
