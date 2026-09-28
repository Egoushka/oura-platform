# 0001 — No EF Core

**Status:** accepted · 2026-08-01

## Context

`oura-platform` stores three shapes of data:

1. **Raw documents.** `oura_raw` is an immutable jsonb landing zone and the declared source of
   truth. Every typed table is a projection that must be rebuildable from it without re-calling
   the API.
2. **Time series.** `hr_samples`, `sleep_series`, `hypnogram`, `ring_battery` are TimescaleDB
   hypertables. A single night of `sleep` expands to ~200 `sleep_series` rows; a full backfill is
   hundreds of thousands of rows written in one pass.
3. **A handful of narrow projections.** `daily`, `sleep_sessions`, `workouts`, `tags`, `context` —
   a few thousand rows each, queried far more often from Grafana than from C#.

The schema work is TimescaleDB DDL: `create_hypertable`, `add_dimension`, continuous aggregates,
compression policies. None of it is expressible as EF migrations without dropping to raw SQL
anyway.

## Decision

**Dapper for reads and upserts, `NpgsqlBinaryImporter` (COPY) for the series, DbUp for migrations.
No EF Core.**

## Consequences

**What this buys:**

- Migrations are ordinary `.sql` files. `create_hypertable('sleep_series', 'ts')` and a continuous
  aggregate are written once, reviewed as SQL, and applied in order by DbUp. There is no
  model-first fiction to reconcile against a schema Timescale partly owns.
- Bulk series writes go through `COPY` rather than parameterised `INSERT`s. Backfill writes
  hundreds of thousands of rows; the change-tracker overhead and per-row round trips would dominate
  it.
- The jsonb landing zone needs no mapping at all — the payload is written as a string and read back
  with Postgres' own jsonb operators. Modelling it through EF would mean either an opaque `string`
  property (all of the ceremony, none of the benefit) or a POCO graph that defeats the purpose of
  keeping the raw document.
- Upserts are `INSERT … ON CONFLICT … DO UPDATE`, which is what idempotent-and-resumable ingest
  actually needs. EF's equivalent is a read-then-write round trip or a third-party bulk extension.

**What this costs:**

- SQL is written by hand and typo'd at runtime, not compile time. Mitigated by Testcontainers-backed
  storage tests that run the real migrations against a real TimescaleDB image.
- No change tracking, no lazy loading, no `IQueryable` composition. Nothing in this repo wants them:
  the query surface is a fixed set of upserts plus Grafana talking directly to Postgres.
- Adding a column means touching a migration, a record and an upsert statement instead of one class.
  With ~8 tables that is cheaper than the alternative.

## Rejected alternative

**EF Core with raw SQL escape hatches.** This is the shape most .NET projects land on, and it is
worse here than either pure option: it still needs raw SQL for every hypertable and continuous
aggregate, still needs a bulk extension for the series, still needs jsonb handled specially — and
now there is a second source of truth about the schema (the model snapshot) that drifts from the
migrations that actually ran.
