---
title: "Database schema"
description: "Every table and view the migrations create, with keys and columns, which Oura collection fills each, and how days, nights and time zones line up."
order: 5
section: "Reference"
---

Four migrations in [db/migrations/](../../db/migrations/) create the schema, applied in name order by DbUp and journalled in `schemaversions` ([DatabaseMigrator.cs](../../src/OuraPlatform.Storage/DatabaseMigrator.cs)):

- [0001_init.sql](../../db/migrations/0001_init.sql): the `timescaledb` extension, the raw, token and bookkeeping tables, `daily`, the event tables and `context`.
- [0002_hypertables.sql](../../db/migrations/0002_hypertables.sql): the four time-series hypertables.
- [0003_rollups.sql](../../db/migrations/0003_rollups.sql): the views `sleep_nightly`, `hr_daily` and `hypnogram_nightly`.
- [0004_context_identity.sql](../../db/migrations/0004_context_identity.sql): the `source`, `source_id` and `ends_at` columns of `context`, and the view `context_daily`.

A migration is never edited once applied; a change is a new numbered file ([CLAUDE.md](../../CLAUDE.md#conventions)).

## Raw and bookkeeping tables

**`oura_raw`** holds every document Oura returned, verbatim. It is the source of truth: every other Oura table is projected from it. Primary key `(doc_type, doc_id)`.

| column | type | meaning |
|---|---|---|
| `doc_type` | text | the collection, in its webhook spelling (`vo2_max`) |
| `doc_id` | text | Oura's `id`, or `<doc_type>:<UTC timestamp>` for series rows |
| `day` | date | the document's `day` or `start_day`; null for series rows |
| `payload` | jsonb | the document as Oura sent it |
| `spec_ver` | text | `Oura:SpecVersion` when it was written, `1.37` |
| `fetched_at` | timestamptz | when it was last written |

**`oauth_tokens`** holds the token pair in one row, `provider = 'oura'`: `access_token`, `refresh_token`, `expires_at` (write time plus `expires_in`), `scope` as Oura reported it, and `rotated_at`.

> [!CAUTION]
> `oauth_tokens` holds a live, single-use refresh token. Keep it out of dumps and copies: a second process that spends it strands the first. `scripts/export-warehouse.sh` leaves it out ([export-warehouse.sh](../../scripts/export-warehouse.sh)).

**`ingest_window`** records backfill progress. Primary key `(doc_type, window_start)`, with `window_end` (inclusive), `document_count` and `completed_at`. A window is recorded only after all its documents are written, and the backfill skips every `window_start` already here. The reconcile job does not write to it ([IngestPipeline.cs](../../src/OuraPlatform.Ingest/IngestPipeline.cs)).

## Daily and event tables

**`daily`** has one row per `day`, its primary key. Several collections share a row, each upserting only its own columns ([DocumentProjector.cs](../../src/OuraPlatform.Storage/DocumentProjector.cs)).

| column | type | from |
|---|---|---|
| `readiness_score` | int | `daily_readiness.score` |
| `temp_deviation` | numeric | `daily_readiness.temperature_deviation` |
| `sleep_score` | int | `daily_sleep.score` |
| `activity_score` | int | `daily_activity.score` |
| `steps` | int | `daily_activity.steps` |
| `active_calories` | int | `daily_activity.active_calories` |
| `spo2_avg` | numeric | `daily_spo2.spo2_percentage.average`, `0` stored as null |
| `breathing_disturbance_index` | numeric | `daily_spo2.breathing_disturbance_index` |
| `stress_high_sec` | int | `daily_stress.stress_high` |
| `recovery_high_sec` | int | `daily_stress.recovery_high` |
| `resilience_level` | text | `daily_resilience.level` |
| `vascular_age` | numeric | `daily_cardiovascular_age.vascular_age` |
| `pulse_wave_velocity` | numeric | `daily_cardiovascular_age.pulse_wave_velocity` |
| `vo2_max` | numeric | `vO2_max.vo2_max` |
| `hrv_avg` | numeric | `sleep.average_hrv`, main sleep only |
| `rhr_lowest` | numeric | `sleep.lowest_heart_rate`, main sleep only |
| `updated_at` | timestamptz | the last upsert |

"Main sleep only" means a `sleep` document whose `type` is `long_sleep` or `sleep`, so a nap never overwrites the night. Oura computes `lowest_heart_rate` on 30-second samples, so `rhr_lowest` does not equal the minimum of that night's `sleep_series` heart rate ([Oura API notes](../oura-api-notes.md#the-sleep-document)).

**`sleep_sessions`** has one row per `sleep` document, naps and rest periods included: `id` (primary key), `night` (the document's `day`), `bedtime_start`, `bedtime_end` and the whole `payload`.

**`workouts`** has one row per `workout` document: `id` (primary key), `start_ts`, `end_ts`, `activity`, `intensity` and `payload`.

**`tags`** has one row per `enhanced_tag` document: `id` (primary key), `ts` (its `start_time`), `tag_type` (its `tag_type_code`, else its `custom_name`), `comment` and `payload`.

## Time-series hypertables

TimescaleDB hypertables partitioned on `ts`. Each unique key includes `ts`, and re-fetched rows collapse onto it ([0002_hypertables.sql](../../db/migrations/0002_hypertables.sql)).

| table | columns | unique key | from |
|---|---|---|---|
| `hr_samples` | `ts`, `bpm`, `source` | `(ts, source)` | `heartrate` |
| `sleep_series` | `ts`, `night`, `metric`, `value` | `(ts, metric)` | `sleep.hrv`, `sleep.heart_rate` |
| `hypnogram` | `ts`, `night`, `phase` | `(ts)` | `sleep.sleep_phase_5_min` |
| `ring_battery` | `ts`, `level` | `(ts)` | `ring_battery_level` |

- **`hr_samples`** is sparse and irregular by design: the ring measures when it can. `source` is one of `awake`, `workout`, `rest`, `sleep`, `live` and `session`.
- **`sleep_series`** has one row per interval of a night's HRV (`metric = 'hrv'`) and heart-rate (`'hr'`) arrays. A `NULL` `value` is a gap the ring did not measure. Rows advance by the payload's own `interval`, 300 seconds in production.
- **`hypnogram`** has one row per 5-minute step from `bedtime_start`, with `phase` 1 deep, 2 light, 3 REM, 4 awake. When `sleep_phase_5_min` is absent, the projector uses `app_sleep_phase_5_min`.
- **`ring_battery`** keeps only `level`; the charging flags stay in `oura_raw`.

Two sleep periods that cover the same instants, a nap inside a rest period for example, share one set of `sleep_series` and `hypnogram` rows, and the last one written wins ([BulkUpsert.cs](../../src/OuraPlatform.Storage/BulkUpsert.cs)).

## Context

**`context`** holds what Oura does not see ([0004_context_identity.sql](../../db/migrations/0004_context_identity.sql)).

| column | type | meaning |
|---|---|---|
| `ts` | timestamptz | the start, or the moment of a point event |
| `ends_at` | timestamptz | the end of an interval; null for a point event |
| `kind` | text | `meeting`, `alcohol`, `caffeine`, ... |
| `label` | text | the event's summary, or the tag's comment |
| `meta` | jsonb | what the source said that has no column |
| `source` | text | `calendar:<name>`, `oura_tag`, or your own |
| `source_id` | text | the upstream id, unique within `source` |

A partial unique index on `(source, source_id) where source_id is not null` is what the importers upsert on. The calendar importer deletes only rows of its own source, and the tag importer writes only `oura_tag` ([ContextRepository.cs](../../src/OuraPlatform.Storage/ContextRepository.cs)). So you can add rows of your own under another source:

```sql
insert into context (source, source_id, ts, ends_at, kind, label)
values ('manual', 'run-2026-09-28', '2026-09-28 18:00+03', '2026-09-28 19:00+03', 'run', 'evening run');
```

`source` may not be null. A row without a `source_id` never collides with anything, and so can never be updated by an upsert either.

## Views

The rollups are plain views, computed on every read ([0003_rollups.sql](../../db/migrations/0003_rollups.sql), [0004_context_identity.sql](../../db/migrations/0004_context_identity.sql)).

- **`sleep_nightly`**: per `night` and `metric` over `sleep_series`. `samples` counts measured intervals and `gaps` unmeasured ones; then `avg_value`, `min_value`, `max_value`, `median_value`, `stddev_value`, `first_ts` and `last_ts`.
- **`hr_daily`**: per UTC `day` and `source` over `hr_samples`: `samples`, `avg_bpm`, `min_bpm`, `max_bpm`.
- **`hypnogram_nightly`**: per `night` and `phase`: `minutes`, the step count times 5.
- **`context_daily`**: per local `day` and `kind` over `context`: `events`, `hours` (the summed length of events that have an end, else null), `first_start`, `last_end`, and `last_end_hour`, the local hour at which the day's last event finished.

The counts are `bigint`. Code that reads them into an `int`, as Dapper records do, casts them with `::int` ([CLAUDE.md](../../CLAUDE.md#conventions)).

## Which collection fills which table

Every collection lands in `oura_raw` first ([OuraCollections.cs](../../src/OuraPlatform.Oura/OuraCollections.cs), [DocumentProjector.cs](../../src/OuraPlatform.Storage/DocumentProjector.cs)):

| collection | also written to |
|---|---|
| `daily_activity` | `daily` |
| `daily_readiness` | `daily` |
| `daily_sleep` | `daily` |
| `daily_spo2` | `daily` |
| `daily_stress` | `daily` |
| `daily_resilience` | `daily` |
| `daily_cardiovascular_age` | `daily` |
| `vo2_max` | `daily` |
| `sleep` | `sleep_sessions`, `sleep_series`, `hypnogram`, `daily` |
| `workout` | `workouts` |
| `enhanced_tag` | `tags`, and `context` through the tag importer |
| `heartrate` | `hr_samples` |
| `ring_battery_level` | `ring_battery` |
| `session` | nothing yet |
| `sleep_time` | nothing yet |
| `rest_mode_period` | nothing yet |
| `ring_configuration` | nothing yet |
| `personal_info` | nothing yet |

`personal_info` carries age, weight, height and biological sex ([ConfigDocuments.cs](../../src/OuraPlatform.Oura/Models/ConfigDocuments.cs)). The deprecated `tag` collection is modelled but not fetched.

## Days, nights and time zones

- **Every timestamp is `timestamptz`.** The writers convert to UTC before they write; `psql` shows the instants in your session's time zone.
- **`daily.day` and every `night` column are Oura's own `day`.** For a sleep period that is the morning it ended: the night of the 4th into the 5th is the 5th ([OuraTools.cs](../../src/OuraPlatform.Mcp/OuraTools.cs)).
- **`hr_daily.day` is the UTC date.**
- **`context_daily.day` and `last_end_hour` are in `Europe/Kyiv`**, written into the view. `Context:TimeZone` does not change them.
- **Oura reads date query parameters in the user's time zone,** which is why the reconcile job asks through tomorrow ([ReconcileJob.cs](../../src/OuraPlatform.Ingest/ReconcileJob.cs)).

## Example joins

Readiness and HRV on the mornings after an evening whose meetings ran to 19:00 or later:

```sql
select d.day,
       d.readiness_score,
       d.hrv_avg,
       c.last_end_hour as evening_before_ended
from daily d
join context_daily c
  on c.kind = 'meeting'
 and c.day = d.day - 1
where c.last_end_hour >= 19
order by d.day desc;
```

HRV the night after an `alcohol` tag against every other night, the question the MCP `correlate` tool answers with `lagDays` 1:

```sql
select case when c.day is null then 'without' else 'with' end as bucket,
       count(*) as days,
       round(avg(d.hrv_avg), 1) as mean_hrv
from daily d
left join (select distinct day from context_daily where kind = 'alcohol') c
       on c.day = d.day - 1
where d.hrv_avg is not null
group by 1
order by 1;
```

One night's HRV and heart rate, summarised:

```sql
select night, metric, samples, gaps, round(avg_value, 1) as mean
from sleep_nightly
where night = date '2026-09-05'
order by metric;
```
