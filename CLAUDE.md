# oura-platform

Self-hosted personal health warehouse. Pulls Oura Ring 5 data into Postgres/TimescaleDB so it
can be joined against calendar, training, environment and lab data. Runs under Docker Compose.

## Stack

.NET 10 · Worker Service (ingest) + Minimal API (OAuth callback, webhooks) + MCP server
(`ModelContextProtocol.AspNetCore`, HTTP transport, over the database) · Postgres 17 +
TimescaleDB · Dapper + `NpgsqlBinaryImporter` · DbUp migrations · Polly · Serilog · Grafana
(file-provisioned) · Docker Compose.

**No EF Core** — see `docs/adr/0001-no-ef-core.md`. jsonb + hypertables + bulk COPY make it a
liability here.

## Commands

```bash
dotnet build
dotnet test                              # runs with zero credentials; uses sandbox fixtures
dotnet run --project src/OuraPlatform.Api
docker compose up -d
docker compose logs -f ingest
```

## Architecture invariants

1. **`oura_raw` is the source of truth.** Every typed table is a projection and must be
   rebuildable from raw jsonb without re-calling the API. Oura has broken its schema before
   (the `meta` envelope vanished in spec 1.34) and will again.
2. **Single ingest instance.** No distributed locking. Do not add a second replica.
3. **All timestamps are `timestamptz`.** Oura's date query params are interpreted in the
   user's local timezone; convert explicitly at the boundary and never store naive local time.
4. **Ingest is idempotent and resumable.** Every write is an upsert keyed on the API document
   id. Killing the backfill mid-run and restarting must converge.
5. **Missing ≠ zero.** Non-wear days produce absent rows. Never synthesise zeros.
6. **The MCP server reads the warehouse, never the Oura API, and holds no credential.** Every tool
   returns an aggregate — never a 5-minute array. Metric names reach SQL by interpolation, so they
   come from the `DailyMetric` whitelist and nowhere else; the caller is a language model.
   Caller-facing validation failures must be `McpException`, or the SDK replaces the message with
   "An error occurred invoking 'x'" and the model has nothing to correct against.

## Oura API — hard-won facts (verified 2026-07-31)

Full notes in `docs/oura-api-notes.md`. The short version:

- **OAuth2 only.** Personal Access Tokens were deprecated December 2025 and no longer work.
- **The refresh token is single-use and rotates.** The new one arrives in the refresh response
  and must be persisted in the same transaction, behind `SELECT ... FOR UPDATE`. This is the
  single most correctness-critical path in the codebase.
- **Read `expires_in`.** Oura's docs say both 24 hours and 30 days in different places.
- **Rate limiting is two-tier and header-driven** (`X-RateLimit-Tier`, `Retry-After`,
  `X-RateLimit-Reset`). The widely-quoted "5000 per 5 minutes" is stale.
- **Sandbox** at `/v2/sandbox/usercollection/{endpoint}` accepts any Authorization string.
  It lies about two things: `interval: 60` where production returns `300`, and
  `producer_timestamp` where the spec says `timestamp_unix`. `personal_info` 404s there.
- **`sleep` carries 5-minute `hrv` and `heart_rate` arrays** for the whole night, plus
  `sleep_phase_5_min` (1=deep 2=light 3=REM 4=awake). This is the highest-value object.
- **`lowest_heart_rate` is computed on 30-second samples** and will not equal `min()` of the
  5-minute array. This is expected — do not "fix" it.
- **`heartrate` returns sparse discrete rows**, not a regular series. Gaps are normal.
- **Capital-O `vO2_max`** on the REST path; lowercase `vo2_max` in the webhook enum.
- **`meal`** appears in the webhook enum with no REST endpoint.
- **Nightly data lands mid-morning the next day.** There is no real-time path through this API.
- **The spec's scope list is wrong.** It declares `spo2Daily` and omits `stress` and
  `heart_health` entirely. The working set is
  `personal daily heartrate workout tag session spo2 stress heart_health ring_configuration`, established from Oura's
  own 401 bodies. The authorize endpoint silently drops names it does not recognise, so a wrong
  scope consents cleanly and then 401s four collections.
- **A missing scope is a 401, not an empty array** — `{"detail":"Token is not authorized access
  spo2 scope."}`. Never refresh the token in response; refreshing cannot fix it and would burn a
  rotation per request.
- Live spec: `https://api.ouraring.com/v2/static/json/openapi-1.37.json`. There is no official
  changelog; diff spec snapshots to find deltas.

## Secrets

`.env` only, never committed. `Oura__ClientId`, `Oura__ClientSecret`, `Oura__RedirectUri`, `Oura__Scopes`,
`POSTGRES_*`, `GRAFANA_*`. Tokens live in the `oauth_tokens` table and must never appear in
logs, exception messages or Serilog properties.

## Conventions

- Nullable enabled, warnings as errors.
- Records for API DTOs, classes for domain types.
- **Dapper matches record constructors positionally against the reader's columns and will not
  narrow `bigint` to `int`.** Alias *and* order columns to match the record, and cast every
  `count(*)`/`sum(...)` to `::int`. Getting it wrong throws at runtime about a missing
  constructor — nothing catches it at compile time.
- One SQL file per migration, numbered, never edited after being applied.
- Grafana dashboards are committed JSON — no editing in the UI without exporting back.
- Conventional commits.

## Roadmap

Stage 0 sandbox recon ✅ · Stage 1 OAuth + backfill + poll ✅ · Stage 2 Grafana ✅ ·
Stage 3 webhooks — **skipped deliberately** (latency optimisation on a working pipeline; would
need a public HTTPS endpoint) · Stage 4 MCP server over this database ✅ ·
Stage 5 Home Assistant + environment sensors ·
Stage 6 Polar H10 RR/ECG ingest · Stage 7 local BLE (`open_oura`, experimental) ·
Stage 8 body metrics + CGM.
