---
title: "Architecture"
description: "The processes, one ingest cycle from the Oura API to oura_raw and the typed tables, the rotating token, storage in TimescaleDB, and how context joins."
order: 3
section: "Concepts"
---

oura-platform is four containers and two shared libraries. This page follows the data from Oura's API to a query, and names the file that does each step.

## The moving parts

| service | built from | does |
|---|---|---|
| `timescaledb` | `timescale/timescaledb:2.17.2-pg17` | holds everything |
| `ingest` | `OuraPlatform.Ingest`, a Worker Service | migrations, backfill, reconcile, context import |
| `api` | `OuraPlatform.Api`, a Minimal API | the OAuth handshake, `/healthz`, a webhook stub |
| `mcp` | `OuraPlatform.Mcp` | seven read-only MCP tools, `/healthz` |

Two libraries sit under them. `OuraPlatform.Oura` is the API client: the collection list, date windows, JSON models, the OAuth client, the bearer-token handler and the retry pipeline. `OuraPlatform.Storage` holds the Dapper repositories, the DbUp migrator, the COPY-based upsert, the token store, the projector and the analytics queries. The SQL in `db/migrations/` is embedded into the storage assembly at build ([OuraPlatform.Storage.csproj](../../src/OuraPlatform.Storage/OuraPlatform.Storage.csproj)).

One [Dockerfile](../../Dockerfile) builds all three .NET images and picks the project with the `PROJECT` build argument. The `mcp` process registers storage and nothing from the Oura library, so it has no Oura client to call ([Program.cs](../../src/OuraPlatform.Mcp/Program.cs)).

## One ingest cycle

[IngestWorker.cs](../../src/OuraPlatform.Ingest/IngestWorker.cs) runs the whole loop:

1. Apply the migrations, when `Ingest:MigrateOnStartup` is true (the default).
2. Wait until `oauth_tokens` has a row, checking every 30 seconds. Sandbox mode skips this.
3. Backfill, until one pass finishes without a failed collection.
4. Reconcile the trailing days.
5. Import context: Oura tags, then calendar feeds.
6. Sleep for `Ingest:ReconcileInterval`, 6 hours by default, and go back to step 3.

An exception in a cycle is logged, and the cycle runs again after `Ingest:RetryDelay`, 5 minutes by default. A token that could not be persisted stops the process with exit code 70 instead.

**Collections.** [OuraCollections.cs](../../src/OuraPlatform.Oura/OuraCollections.cs) models 19 collections and ingests 18 of them: the deprecated `tag` is modelled but not fetched, and against the sandbox `personal_info` is skipped too. Each has a query style. Daily-style collections take `start_date` and `end_date`, the time series `heartrate` and `ring_battery_level` take `start_datetime` and `end_datetime`, `ring_configuration` takes no dates, and `personal_info` is a single document.

**Windows.** `OuraWindows.Split` cuts a date range into one-month windows for daily-style collections, 7-day windows for the time series, and a single window for the other two ([OuraRawDocument.cs](../../src/OuraPlatform.Oura/OuraRawDocument.cs)). The client follows `next_token` to the end of each window ([OuraApiClient.cs](../../src/OuraPlatform.Oura/OuraApiClient.cs)).

**Backfill.** [BackfillJob.cs](../../src/OuraPlatform.Ingest/BackfillJob.cs) walks each collection from `Oura:BackfillFrom` to today in UTC, newest window first, and skips every window already in `ingest_window`. A collection that fails is logged and skipped, so one lapsed endpoint does not stop the rest, and the backfill runs again next cycle.

**Reconcile.** [ReconcileJob.cs](../../src/OuraPlatform.Ingest/ReconcileJob.cs) re-fetches every collection for the `Oura:ReconcileTrailingDays` (7) days before today, plus today and tomorrow, UTC. Tomorrow, because Oura reads date parameters in the user's time zone, which may already be a day ahead. Oura lands nightly data mid-morning and amends recent days, so re-fetching and upserting a week is simpler than tracking which days changed.

**Retries.** The API client's pipeline retries up to 6 times with exponential backoff and jitter from 2 seconds, and obeys `Retry-After` when Oura sends it. It retries connection errors, timeouts and status codes 408, 429, 500, 502, 503 and 504, with a 90-second timeout per attempt ([OuraServiceCollectionExtensions.cs](../../src/OuraPlatform.Oura/OuraServiceCollectionExtensions.cs)). The token endpoint has its own client with no retry, because a refresh calls it while holding the token row's lock ([OuraOAuthClient.cs](../../src/OuraPlatform.Oura/Auth/OuraOAuthClient.cs)).

## From API document to table

[IngestPipeline.cs](../../src/OuraPlatform.Ingest/IngestPipeline.cs) handles one window in four steps: fetch every document, write them to `oura_raw`, project them, and, for the backfill only, record the window in `ingest_window`.

**Raw first.** The client pulls each document's key out of the JSON without binding it to a model, so a document no model can read still reaches `oura_raw`. The key is Oura's `id` or, for time-series rows that have none, `<collection>:<timestamp in UTC>`. The day is the document's `day`, else its `start_day` ([OuraApiClient.cs](../../src/OuraPlatform.Oura/OuraApiClient.cs)). The write copies the batch into a temporary table, keeps the last row per key, and upserts from there ([BulkUpsert.cs](../../src/OuraPlatform.Storage/BulkUpsert.cs)). Each row records `Oura:SpecVersion`, 1.37, so a later projection knows which spec shape it holds.

**Projection.** [DocumentProjector.cs](../../src/OuraPlatform.Storage/DocumentProjector.cs) reads only stored payloads, never the network, which is what lets `--reproject` rebuild the tables without the API. Several daily collections share the `daily` row of their day, each upserting only its own columns. A document that fails to parse is logged and skipped, and its raw row stays. [Database schema](database.md#which-collection-fills-which-table) maps each collection to its tables.

**Missing is not zero.** The projector keeps a gap as `NULL` and never writes a zero in its place:

- A `null` in a night's 5-minute HRV or heart-rate array becomes a `NULL` row in `sleep_series`, and the step between rows is the payload's own `interval`, never an assumed one.
- A `daily_spo2` average of `0.0`, which Oura sends for a night it did not measure, is stored as `NULL`.
- Only the main sleep period, `type` `long_sleep` or `sleep`, writes `daily.hrv_avg` and `daily.rhr_lowest`, so a nap cannot overwrite the night.

[ProjectionTests.cs](../../tests/OuraPlatform.Storage.Tests/ProjectionTests.cs) covers each of these.

## The token pair

Oura's refresh token is single-use and rotates: every refresh returns a new pair and kills the old refresh token ([Oura API notes](../oura-api-notes.md#the-refresh-token-rotates-and-is-single-use)). Losing the new one means authorizing again by hand.

[OuraTokenStore.cs](../../src/OuraPlatform.Storage/OuraTokenStore.cs) therefore refreshes inside one database transaction:

1. `SELECT ... FOR UPDATE` locks the single `oauth_tokens` row.
2. If the stored pair has not expired, it takes that as a pair another caller already rotated, returns it and spends nothing.
3. Otherwise it exchanges the refresh token at Oura while holding the lock, writes both new tokens and commits.
4. If the write fails, the store latches: every later call throws `OuraTokenPersistenceException` without touching the network, and the ingest exits with code 70.

The expiry comes from each response's `expires_in`, since Oura's documentation gives both 24 hours and 30 days.

[OuraAuthenticationHandler.cs](../../src/OuraPlatform.Oura/Auth/OuraAuthenticationHandler.cs) attaches the bearer token to every API request. From `Oura:RefreshSkew` (5 minutes) before expiry it asks the store for a refresh, but step 2 hands back the stored pair until that pair has actually expired, so in practice the pair rotates on the first request after expiry. On a `401` the handler first re-reads the table, so a re-consent written by the api takes effect in the running ingest. If the body names a missing scope, it returns the `401` without a refresh, because no refresh can add a scope. Otherwise it asks for one refresh and retries; by step 2, that rotates only an expired pair. Its in-process lock only stops parallel requests from refreshing together; the row lock is what keeps the refresh token from being spent twice.

The token records print `redacted` in place of their secrets, and a failed token request logs only its status code ([OuraTokens.cs](../../src/OuraPlatform.Oura/Auth/OuraTokens.cs), [OuraOAuthClient.cs](../../src/OuraPlatform.Oura/Auth/OuraOAuthClient.cs)).

## Storage in TimescaleDB

The database is Postgres 17 with TimescaleDB 2.17.2, the image tag in [docker-compose.yml](../../docker-compose.yml). The storage tests start the same image ([TimescaleFixture.cs](../../tests/OuraPlatform.Storage.Tests/TimescaleFixture.cs)).

[DatabaseMigrator.cs](../../src/OuraPlatform.Storage/DatabaseMigrator.cs) creates the database if it is missing and applies `db/migrations/*.sql` in name order with DbUp, journalled in `schemaversions`. It runs them without a transaction, because TimescaleDB refuses some statements inside one, so every script is written to run twice safely ([MigrationTests.cs](../../tests/OuraPlatform.Storage.Tests/MigrationTests.cs)).

The sampled data lives in hypertables partitioned on `ts`: `hr_samples`, `sleep_series`, `hypnogram` and `ring_battery`. Their unique keys include `ts`, as TimescaleDB requires, and that key is what makes a re-fetched window collapse onto the same rows ([0002_hypertables.sql](../../db/migrations/0002_hypertables.sql)).

The rollups are plain views, not continuous aggregates. A continuous aggregate has to bucket on `ts`, and a one-day bucket cuts a night in half at midnight, while the data, about 100,000 rows a year, is small enough to aggregate on read ([0003_rollups.sql](../../db/migrations/0003_rollups.sql)).

## Context and joins

`context` holds what Oura never sees. Calendar occurrences arrive with the source `calendar:<name>`, Oura tags with `oura_tag`, and each row has a `source_id` that is unique within its source, so every import upserts instead of appending. `ends_at` makes an event an interval: whether an evening ran late is a question about when it finished ([0004_context_identity.sql](../../db/migrations/0004_context_identity.sql)). Rows you type in yourself need a `source` too; the migration marked rows that existed before it as `manual`.

The join rests on days. `daily` is keyed on Oura's own `day`, and for a sleep period that is the morning it ended: the night of the 4th into the 5th belongs to the 5th ([OuraTools.cs](../../src/OuraPlatform.Mcp/OuraTools.cs)). `context_daily` rolls context up per local day, in `Europe/Kyiv`, and per kind. So the night after an evening on day D is the `daily` row for D + 1, and the MCP `correlate` tool shifts context by `lagDays`, 1 by default, before it joins ([AnalyticsRepository.cs](../../src/OuraPlatform.Storage/AnalyticsRepository.cs)). [Database schema](database.md#example-joins) has the same join in SQL.

## The MCP server

The `mcp` process reads the warehouse and never calls Oura. Every tool returns an aggregate with a unit: a night is about 200 samples, and a language model would only average them again ([OuraTools.cs](../../src/OuraPlatform.Mcp/OuraTools.cs)).

Metric names reach SQL by string interpolation, so they come from the fixed list in [DailyMetric.cs](../../src/OuraPlatform.Storage/DailyMetric.cs) and nowhere else. Each entry also says whether higher is better, so a comparison can report `better` or `worse` without the caller knowing that a lower resting heart rate is good. Invalid input is raised as an `McpException`, whose message reaches the client; the SDK would replace any other exception's message with a generic one.

The tools only read, but the process connects with the same database role as the ingest: Compose gives all three .NET services the same connection string ([docker-compose.yml](../../docker-compose.yml)). The database does not enforce read-only access.

## Decisions

The repository has one architecture decision record, [0001 — No EF Core](../adr/0001-no-ef-core.md), accepted on 2026-08-01. It chooses Dapper for reads and upserts, `NpgsqlBinaryImporter` (COPY) for bulk writes and DbUp for plain SQL migrations. The jsonb landing zone needs no mapping, and `INSERT ... ON CONFLICT` is the upsert an idempotent ingest needs. The cost it accepts is SQL checked at runtime rather than compile time, answered by storage tests that run the real migrations against a real TimescaleDB. The ADR's context lists continuous aggregates and compression policies among the TimescaleDB DDL; the migrations create neither.

The other rules are written down as invariants in [CLAUDE.md](../../CLAUDE.md#architecture-invariants):

| invariant | where the code holds it |
|---|---|
| `oura_raw` is the source of truth | `IngestPipeline`, `ReprojectCommand` |
| one ingest instance | no locking beyond the token row |
| all timestamps are `timestamptz` | `0001` and `0002` migrations |
| ingest is idempotent and resumable | upserts on document keys, `ingest_window` |
| missing is not zero | `DocumentProjector` |
| the MCP server reads only the warehouse | `OuraPlatform.Mcp/Program.cs` |

A Dapper rule sits beside them: Dapper matches a record's constructor to the query's columns by position and will not narrow `bigint` to `int`, so every query aliases and orders its columns to match and casts counts to `::int` ([CLAUDE.md](../../CLAUDE.md#conventions)).
