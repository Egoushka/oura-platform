using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using OuraPlatform.Oura;
using OuraPlatform.Oura.Models;

namespace OuraPlatform.Storage;

/// <summary>
/// Projects raw payloads into the typed tables.
/// </summary>
/// <remarks>
/// Every projection reads only from <see cref="OuraRawDocument.Payload"/>, never from the network,
/// so the whole warehouse can be rebuilt from <c>oura_raw</c> after a schema change at Oura's end.
/// <para>
/// A document that fails to parse is logged and skipped rather than aborting the batch: the raw row
/// is already durable, and one unexpected shape must not stop the other 364 days of a backfill.
/// </para>
/// </remarks>
public sealed class DocumentProjector
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<DocumentProjector> _logger;

    public DocumentProjector(NpgsqlDataSource dataSource, ILogger<DocumentProjector> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task<int> ProjectAsync(
        OuraCollection collection,
        IReadOnlyList<OuraRawDocument> documents,
        CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
        {
            return 0;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var projected = collection.Name switch
        {
            "daily_sleep" => await UpsertDailyAsync<DailySleep>(
                connection, transaction, documents, ["sleep_score"],
                document => (document.Day, [document.Score]), cancellationToken).ConfigureAwait(false),

            "daily_readiness" => await UpsertDailyAsync<DailyReadiness>(
                connection, transaction, documents, ["readiness_score", "temp_deviation"],
                document => (document.Day, [document.Score, document.TemperatureDeviation]), cancellationToken).ConfigureAwait(false),

            "daily_activity" => await UpsertDailyAsync<DailyActivity>(
                connection, transaction, documents, ["activity_score", "steps", "active_calories"],
                document => (document.Day, [document.Score, document.Steps, document.ActiveCalories]), cancellationToken).ConfigureAwait(false),

            // Oura sends {"average": 0.0} for a night it did not measure. A 0% blood oxygen is not
            // a low reading, it is a missing one, and left alone it draws a fatal desaturation on
            // every chart. Normalised to null here rather than in the model, so oura_raw keeps what
            // Oura actually said.
            "daily_spo2" => await UpsertDailyAsync<DailySpo2>(
                connection, transaction, documents, ["spo2_avg", "breathing_disturbance_index"],
                document => (document.Day, [
                    document.Spo2Percentage?.Average is > 0 and var average ? average : null,
                    document.BreathingDisturbanceIndex,
                ]), cancellationToken).ConfigureAwait(false),

            "daily_stress" => await UpsertDailyAsync<DailyStress>(
                connection, transaction, documents, ["stress_high_sec", "recovery_high_sec"],
                document => (document.Day, [document.StressHigh, document.RecoveryHigh]), cancellationToken).ConfigureAwait(false),

            "daily_resilience" => await UpsertDailyAsync<DailyResilience>(
                connection, transaction, documents, ["resilience_level"],
                document => (document.Day, [document.Level]), cancellationToken).ConfigureAwait(false),

            "daily_cardiovascular_age" => await UpsertDailyAsync<DailyCardiovascularAge>(
                connection, transaction, documents, ["vascular_age", "pulse_wave_velocity"],
                document => (document.Day, [document.VascularAge, document.PulseWaveVelocity]), cancellationToken).ConfigureAwait(false),

            "vo2_max" => await UpsertDailyAsync<VO2Max>(
                connection, transaction, documents, ["vo2_max"],
                document => (document.Day, [document.Vo2Max]), cancellationToken).ConfigureAwait(false),

            "sleep" => await ProjectSleepAsync(connection, transaction, documents, cancellationToken).ConfigureAwait(false),
            "workout" => await ProjectWorkoutsAsync(connection, transaction, documents, cancellationToken).ConfigureAwait(false),
            "enhanced_tag" => await ProjectTagsAsync(connection, transaction, documents, cancellationToken).ConfigureAwait(false),
            "heartrate" => await ProjectHeartRateAsync(connection, transaction, documents, cancellationToken).ConfigureAwait(false),
            "ring_battery_level" => await ProjectRingBatteryAsync(connection, transaction, documents, cancellationToken).ConfigureAwait(false),

            // session, sleep_time, rest_mode_period, ring_configuration, personal_info and the
            // deprecated tag live in oura_raw only. They have no dashboard yet; when one needs
            // them, the projection is written here and replayed from raw.
            _ => 0,
        };

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return projected;
    }

    // ---------------------------------------------------------------------
    // daily: several collections each own a few columns of the same row, so every
    // writer upserts only its own and leaves the rest untouched.
    // ---------------------------------------------------------------------

    private async Task<int> UpsertDailyAsync<TDocument>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<OuraRawDocument> documents,
        string[] columns,
        Func<TDocument, (DateOnly Day, object?[] Values)> select,
        CancellationToken cancellationToken)
    {
        // Column names come from this file only, never from a payload.
        var parameterNames = columns.Select((_, index) => $"@v{index}").ToArray();
        var sql = $"""
            insert into daily (day, {string.Join(", ", columns)})
            values (@day, {string.Join(", ", parameterNames)})
            on conflict (day) do update set
                {string.Join(",\n    ", columns.Select(column => $"{column} = excluded.{column}"))},
                updated_at = now()
            """;

        var projected = 0;
        foreach (var document in documents)
        {
            if (!TryParse<TDocument>(document, out var parsed))
            {
                continue;
            }

            var (day, values) = select(parsed);
            var parameters = new DynamicParameters();
            parameters.Add("day", day);
            for (var index = 0; index < values.Length; index++)
            {
                parameters.Add($"v{index}", values[index]);
            }

            projected += await connection.ExecuteAsync(new CommandDefinition(
                sql, parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        return projected;
    }

    // ---------------------------------------------------------------------
    // sleep: the one document that fans out into three tables.
    // ---------------------------------------------------------------------

    private async Task<int> ProjectSleepAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<OuraRawDocument> documents,
        CancellationToken cancellationToken)
    {
        var sessions = new List<(string Id, DateOnly Night, DateTimeOffset Start, DateTimeOffset End, string Payload)>();
        var series = new List<(DateTimeOffset Ts, DateOnly Night, string Metric, double? Value)>();
        var hypnogram = new List<(DateTimeOffset Ts, DateOnly Night, short Phase)>();
        var nightly = new List<SleepPeriod>();

        foreach (var document in documents)
        {
            if (!TryParse<SleepPeriod>(document, out var sleep))
            {
                continue;
            }

            sessions.Add((sleep.Id, sleep.Day, sleep.BedtimeStart, sleep.BedtimeEnd, document.Payload));
            series.AddRange(Expand(sleep.Hrv, sleep.Day, "hrv"));
            series.AddRange(Expand(sleep.HeartRate, sleep.Day, "hr"));

            // Prefer the ring pipeline's hypnogram; fall back to the phone app's.
            var phases = sleep.SleepPhase5Min ?? sleep.AppSleepPhase5Min;
            if (phases is not null)
            {
                for (var index = 0; index < phases.Length; index++)
                {
                    if (phases[index] is >= '1' and <= '4')
                    {
                        hypnogram.Add((
                            sleep.BedtimeStart.AddMinutes(5 * index),
                            sleep.Day,
                            (short)(phases[index] - '0')));
                    }
                }
            }

            // Naps and rest periods must not overwrite the night's summary in `daily`.
            if (sleep.Type is "long_sleep" or "sleep")
            {
                nightly.Add(sleep);
            }
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into sleep_sessions (id, night, bedtime_start, bedtime_end, payload)
            values (@id, @night, @start, @end, @payload::jsonb)
            on conflict (id) do update set
                night         = excluded.night,
                bedtime_start = excluded.bedtime_start,
                bedtime_end   = excluded.bedtime_end,
                payload       = excluded.payload
            """,
            sessions.Select(s => new { id = s.Id, night = s.Night, start = s.Start.ToUniversalTime(), end = s.End.ToUniversalTime(), payload = s.Payload }),
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await BulkUpsert.ExecuteAsync(
            connection, transaction, "sleep_series",
            ["ts", "night", "metric", "value"],
            ["ts", "metric"],
            "night = excluded.night, value = excluded.value",
            series,
            (importer, row) =>
            {
                importer.Write(row.Ts.ToUniversalTime(), NpgsqlDbType.TimestampTz);
                importer.Write(row.Night, NpgsqlDbType.Date);
                importer.Write(row.Metric, NpgsqlDbType.Text);
                if (row.Value is { } value)
                {
                    importer.Write((decimal)value, NpgsqlDbType.Numeric);
                }
                else
                {
                    // A gap in the ring's coverage. Never a zero.
                    importer.WriteNull();
                }
            },
            cancellationToken).ConfigureAwait(false);

        await BulkUpsert.ExecuteAsync(
            connection, transaction, "hypnogram",
            ["ts", "night", "phase"],
            ["ts"],
            "night = excluded.night, phase = excluded.phase",
            hypnogram,
            (importer, row) =>
            {
                importer.Write(row.Ts.ToUniversalTime(), NpgsqlDbType.TimestampTz);
                importer.Write(row.Night, NpgsqlDbType.Date);
                importer.Write(row.Phase, NpgsqlDbType.Smallint);
            },
            cancellationToken).ConfigureAwait(false);

        foreach (var sleep in nightly)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into daily (day, hrv_avg, rhr_lowest)
                values (@day, @hrv, @rhr)
                on conflict (day) do update set
                    hrv_avg    = excluded.hrv_avg,
                    rhr_lowest = excluded.rhr_lowest,
                    updated_at = now()
                """,
                new { day = sleep.Day, hrv = (decimal?)sleep.AverageHrv, rhr = (decimal?)sleep.LowestHeartRate },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        return sessions.Count;
    }

    /// <summary>Turns a <c>PublicSample</c> into one row per interval. The interval is read from the
    /// payload, never assumed: the sandbox says 60 and production says 300.</summary>
    private static IEnumerable<(DateTimeOffset Ts, DateOnly Night, string Metric, double? Value)> Expand(
        PublicSample? sample,
        DateOnly night,
        string metric)
    {
        if (sample is null || sample.Interval <= 0)
        {
            yield break;
        }

        for (var index = 0; index < sample.Items.Count; index++)
        {
            yield return (sample.Timestamp.AddSeconds(sample.Interval * index), night, metric, sample.Items[index]);
        }
    }

    // ---------------------------------------------------------------------
    // events and series
    // ---------------------------------------------------------------------

    private async Task<int> ProjectWorkoutsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<OuraRawDocument> documents,
        CancellationToken cancellationToken)
    {
        var rows = new List<object>(documents.Count);
        foreach (var document in documents)
        {
            if (!TryParse<Workout>(document, out var workout))
            {
                continue;
            }

            rows.Add(new
            {
                id = workout.Id,
                start = workout.StartDatetime.ToUniversalTime(),
                end = workout.EndDatetime.ToUniversalTime(),
                activity = workout.Activity,
                intensity = workout.Intensity,
                payload = document.Payload,
            });
        }

        return await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into workouts (id, start_ts, end_ts, activity, intensity, payload)
            values (@id, @start, @end, @activity, @intensity, @payload::jsonb)
            on conflict (id) do update set
                start_ts  = excluded.start_ts,
                end_ts    = excluded.end_ts,
                activity  = excluded.activity,
                intensity = excluded.intensity,
                payload   = excluded.payload
            """,
            rows, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task<int> ProjectTagsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<OuraRawDocument> documents,
        CancellationToken cancellationToken)
    {
        var rows = new List<object>(documents.Count);
        foreach (var document in documents)
        {
            if (!TryParse<EnhancedTag>(document, out var tag))
            {
                continue;
            }

            rows.Add(new
            {
                id = tag.Id,
                ts = tag.StartTime.ToUniversalTime(),
                tagType = tag.TagTypeCode ?? tag.CustomName,
                comment = tag.Comment,
                payload = document.Payload,
            });
        }

        return await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into tags (id, ts, tag_type, comment, payload)
            values (@id, @ts, @tagType, @comment, @payload::jsonb)
            on conflict (id) do update set
                ts       = excluded.ts,
                tag_type = excluded.tag_type,
                comment  = excluded.comment,
                payload  = excluded.payload
            """,
            rows, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task<int> ProjectHeartRateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<OuraRawDocument> documents,
        CancellationToken cancellationToken)
    {
        var rows = new List<HeartRateRow>(documents.Count);
        foreach (var document in documents)
        {
            if (TryParse<HeartRateRow>(document, out var row))
            {
                rows.Add(row);
            }
        }

        return await BulkUpsert.ExecuteAsync(
            connection, transaction, "hr_samples",
            ["ts", "bpm", "source"],
            ["ts", "source"],
            "bpm = excluded.bpm",
            rows,
            (importer, row) =>
            {
                importer.Write(row.Timestamp.ToUniversalTime(), NpgsqlDbType.TimestampTz);
                importer.Write((short)row.Bpm, NpgsqlDbType.Smallint);
                importer.Write(row.Source, NpgsqlDbType.Text);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ProjectRingBatteryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<OuraRawDocument> documents,
        CancellationToken cancellationToken)
    {
        var rows = new List<RingBatteryLevelRow>(documents.Count);
        foreach (var document in documents)
        {
            if (TryParse<RingBatteryLevelRow>(document, out var row))
            {
                rows.Add(row);
            }
        }

        return await BulkUpsert.ExecuteAsync(
            connection, transaction, "ring_battery",
            ["ts", "level"],
            ["ts"],
            "level = excluded.level",
            rows,
            (importer, row) =>
            {
                importer.Write(row.Timestamp.ToUniversalTime(), NpgsqlDbType.TimestampTz);
                importer.Write((short)row.Level, NpgsqlDbType.Smallint);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private bool TryParse<TDocument>(OuraRawDocument document, out TDocument parsed)
    {
        try
        {
            var value = JsonSerializer.Deserialize<TDocument>(document.Payload, OuraJson.Options);
            if (value is not null)
            {
                parsed = value;
                return true;
            }
        }
        catch (JsonException exception)
        {
            // The raw row is already durable, so this is recoverable: fix the model, re-project.
            _logger.LogWarning(
                exception,
                "Could not project {DocType}/{DocId}; the raw payload is stored and can be replayed.",
                document.DocType,
                document.DocId);

            parsed = default!;
            return false;
        }

        _logger.LogWarning("Payload for {DocType}/{DocId} deserialized to null.", document.DocType, document.DocId);
        parsed = default!;
        return false;
    }
}
