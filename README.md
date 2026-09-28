# oura-platform

A self-hosted personal health warehouse. It pulls Oura Ring data out of Oura's cloud into your own
Postgres/TimescaleDB so it can be joined against everything Oura will never see — calendar load,
training, bedroom CO₂, body composition, glucose — and queried with SQL and Grafana.

Runs on a single VPS under Docker Compose. Two processes, one database, no Kubernetes.

**Stages 1, 2 and 4 of 8.** OAuth, storage, backfill and the scheduled poll, three Grafana
dashboards, the calendar and tag importers, and an MCP server over the warehouse. Webhooks
(Stage 3) are deliberately skipped: they are a latency optimisation on a pipeline that already
works, and Oura's nightly data lands mid-morning either way.

---

## What it stores

`oura_raw` is the source of truth: every document Oura returns, verbatim, as jsonb. Everything else
is a projection that can be rebuilt from it without re-calling the API.

| Table | What is in it |
|---|---|
| `oura_raw` | Every document, verbatim, keyed on `(doc_type, doc_id)` |
| `daily` | One wide row per day — scores, HRV, resting HR, temperature deviation, SpO₂, stress, VO₂max, steps |
| `sleep_series` | The 5-minute HRV and heart-rate arrays from each night, one row per interval |
| `hypnogram` | One row per 5-minute sleep phase (1=deep 2=light 3=REM 4=awake) |
| `hr_samples` | Daytime heart rate, sparse and irregular by design |
| `ring_battery` | Battery level over time |
| `sleep_sessions`, `workouts`, `tags` | Event documents |
| `context` | **The reason this exists.** Calendar meetings and Oura's own tags, keyed and upsertable, joined against `daily` |

Plus four rollup views: `sleep_nightly`, `hr_daily`, `hypnogram_nightly`, `context_daily`.

### Context

Two importers fill `context`, and neither needs an API credential:

- **Calendar** — any iCalendar URL, typically Google's private "Secret address in iCal format".
  Recurring events are expanded per occurrence, all-day events skipped, and anything that has left
  the feed is pruned, because a cancelled meeting simply stops appearing rather than being marked
  deleted.
- **Oura tags** — `enhanced_tag` is already in `oura_raw`; this projects it into the same table so a
  caffeine tag and a calendar meeting answer the same query.

`context_daily` is what the dashboards read: events, hours, and **`last_end_hour` in local time** —
an 18:00–20:30 call does not *start* after 19:00 but certainly ends after it.

---

## Setup

### 1. Register an Oura application

At <https://developer.ouraring.com> (<https://cloud.ouraring.com/oauth/applications> also works;
it is unclear which is canonical). Free, no approval needed — unapproved apps are capped at 10
users, which is nine more than this needs.

Register **both** redirect URIs, since Oura allowlists them verbatim and supports several:

```
http://localhost:8080/oauth/callback
https://oura.your-domain.example/oauth/callback
```

> Personal Access Tokens do not work. They were deprecated in December 2025 and there is no PAT
> code path in this repo.

### 2. Configure

```bash
cp .env.example .env
```

Fill in `Oura__ClientId`, `Oura__ClientSecret`, `POSTGRES_PASSWORD`, `GRAFANA_ADMIN_PASSWORD`.
Set `Oura__BackfillFrom` to roughly when you got the ring — the default of 2020-01-01 just means
walking a few hundred extra empty windows.

**Leave `Oura__Scopes` alone unless you know better than the 401s.** The default is the set Oura's
API actually enforces, which is not the set its OpenAPI spec declares. The authorize endpoint
accepts unknown scope names without complaint, so a typo there consents cleanly and then fails four
collections at fetch time.

### 3. Start

```bash
docker compose up -d
```

The ingest container applies the migrations, then waits and logs a warning until a token exists.
That is expected.

### 4. Authorize — one browser round-trip

Open <http://localhost:8080/oauth/start>. Approve at Oura. You land back on a page confirming the
token is stored, and ingest starts on its next cycle.

```bash
docker compose logs -f ingest
```

The backfill walks newest-first, so Grafana has recent data long before the 2020 windows finish.
Progress is recorded per window in `ingest_window`; killing the container and restarting resumes
rather than starting over.

### 5. Ask it questions

The `mcp` container speaks MCP over HTTP at `/mcp`, against the warehouse rather than against
Oura's API — proxying the API would only reproduce the phone app in text form; the value is in
joining the ring against everything else. Point a client at it:

```json
{ "mcpServers": { "oura": { "url": "http://localhost:8081/mcp" } } }
```

Seven tools, all read-only, all returning aggregates — never a 5-minute array:

| Tool | Answers |
|---|---|
| `coverage` | What is actually held, and the catalogue of metric names the others take |
| `context_kinds` | What non-Oura context exists to correlate against |
| `compare_periods` | One metric over a range, against a baseline range |
| `correlate` | One metric split by whether something happened, with a day lag |
| `outlier_nights` | The days furthest from the series mean, both directions |
| `weekly_digest` | Every metric for a week, beside the week before |
| `night_detail` | One night as phase minutes and hour-long HRV/HR bands |

`coverage` and `context_kinds` exist so an empty answer can be read as *no data* rather than *no
effect*. Every comparison reports a direction that already accounts for metrics where lower is
better, and flags a window too thin to mean anything.

It holds no Oura credential and cannot write. The container needs only the connection string.

### 6. Grafana

**This stack does not run Grafana.** It is meant to sit beside an existing one, and a second
instance would mean a second set of datasources, a second auth story and dashboards in two places.

The dashboards in `grafana/dashboards/` are the source of truth; copy them into your Grafana's
dashboard provisioning directory. They read a Postgres datasource by uid `oura-timescaledb`, which
`grafana/provisioning/datasources/timescaledb.yml` provisions. Verify them against the running
Grafana with:

```bash
GRAFANA_ADMIN_PASSWORD=... ./grafana/verify-dashboards.py
```

For a throwaway local Grafana while developing a panel, run one by hand against this stack's
network rather than adding it back to the compose file.

---

## Operating it

```bash
docker compose logs -f ingest          # backfill and reconcile progress
docker compose ps                      # /healthz drives the api healthcheck
curl -s localhost:8080/healthz         # Degraded until the handshake is done — still a 200
curl -s localhost:8081/healthz         # Degraded until ingest has backfilled anything to read
```

## Publishing

The homelab deliberately does not build first-party source at deploy time, so images go to GHCR
(`ghcr.io/egoushka/oura-{ingest,api,mcp}`, public) and it pulls them like any third-party image:

```bash
./scripts/publish.sh 0.1.0
```

It refuses to run on a dirty tree, runs the tests before pushing, builds `linux/amd64` regardless
of the machine you run it on, and prints each published digest for the record.

Once backfilled, the reconcile job re-fetches a trailing 7 days every 6 hours. That window is not
padding: nightly data lands mid-morning the following day, and Oura amends recent days after the
fact — a bedtime edit rewrites a sleep period that was already ingested.

### Re-authorizing

If ingest logs `Oura authorization is unrecoverable` and exits, the token pair rotated at Oura's
end but failed to persist locally. Visit `/oauth/start` again; nothing else is lost, because
`oura_raw` is untouched.

### Changing a projection

Edit `DocumentProjector`, then re-project from stored payloads instead of re-downloading history —
that is what `oura_raw` is for:

```bash
docker compose run --rm ingest --reproject                 # everything
docker compose run --rm ingest --reproject sleep daily_spo2 # named collections only
```

It applies pending migrations, rebuilds the typed tables and exits without touching the API. The
ingest loop does not start, so it is safe to run against a stopped stack.

### Moving to another host

```bash
./scripts/export-warehouse.sh
```

Dumps `oura_raw` and `ingest_window` only, and prints the restore commands. The projections are
deliberately left out — the receiving side runs `--reproject` and derives them against whatever
schema it is on, rather than inheriting tables built by an older projector.

`oauth_tokens` is deliberately left out too. **Never run two ingests against one Oura app**: the
refresh token is single-use, one instance will spend it, and the other is left holding a token
Oura has already invalidated. Stop the old one before starting the new one.

---

## Development

```bash
dotnet build
dotnet test tests/OuraPlatform.Oura.Tests       # no credentials, no network, no Docker
dotnet test tests/OuraPlatform.Storage.Tests    # needs a Docker daemon (Testcontainers)
dotnet run --project src/OuraPlatform.Api
```

Before committing, enable the hooks: `git config core.hooksPath .githooks`, then copy
`.private-terms.example` to `.private-terms` and list what must never appear here.

The Oura test suite replays responses recorded from Oura's sandbox, which needs no credentials —
it accepts any string as an `Authorization` header. Re-record them with:

```bash
./tests/record-sandbox-fixtures.sh
```

If Oura adds a field, the re-recorded fixture fails `Model_binds_every_field_the_sandbox_returns`
until a model binds it or it is explicitly ignored. That failure is the point.

**Do not calibrate anything on sandbox data.** It lies about at least four things — see
[docs/oura-api-notes.md](docs/oura-api-notes.md).

### Layout

```
src/OuraPlatform.Oura/      API client: models, OAuth, paging, rate-limit handling
src/OuraPlatform.Storage/   Dapper repositories, DbUp runner, COPY writers, token store
src/OuraPlatform.Ingest/    Worker: backfill + reconcile
src/OuraPlatform.Api/       Minimal API: /oauth/*, /healthz, webhook stub
src/OuraPlatform.Mcp/       MCP server over the warehouse: /mcp, /healthz
db/migrations/              Numbered SQL, embedded into OuraPlatform.Storage at build
grafana/                    Provisioning and dashboards, version-controlled
docs/                       API notes, ADRs
```

---

## Design notes worth knowing before changing anything

- **The refresh token is single-use and rotates.** Every refresh response carries a new one and
  kills the old. `OuraTokenStore.RefreshAsync` therefore runs the HTTP exchange *inside* a
  transaction holding `SELECT ... FOR UPDATE` on the token row, and refuses to continue if the
  write fails. This is the most correctness-critical code in the repo; read the class comment
  before touching it.
- **No EF Core** — [docs/adr/0001-no-ef-core.md](docs/adr/0001-no-ef-core.md).
- **Single ingest instance.** No distributed locking. Two replicas would race to spend the same
  single-use refresh token.
- **Missing ≠ zero.** Non-wear days produce absent rows and unmeasured intervals produce nulls.
  Nothing synthesises a zero, because a zero looks plausible in an average and a null does not.
- **The MCP server reads the warehouse, never the API,** and every tool returns an aggregate. The
  caller is a language model: a night is ~200 samples it would only average back down again. Metric
  names reach SQL by interpolation, so they come from the fixed whitelist in `DailyMetric` and
  nothing else.
- **Dapper matches a record's constructor parameters positionally against the reader's columns,**
  and will not narrow `bigint` to `int`. A column out of order or a bare `count(*)` throws at
  runtime about a missing constructor, having compiled and deployed perfectly happily. Alias *and*
  order the columns, and cast counts to `::int`.
- **Rollups are plain views, not continuous aggregates.** A day is ~300 heart-rate rows and a night
  ~200 sleep-series rows; materialising that buys nothing and `time_bucket` would cut nights in
  half at midnight. The reasoning is in `db/migrations/0003_rollups.sql`.

---

## License

Apache-2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).

Not affiliated with or endorsed by Oura; "Oura" is a trademark of its owner. This is a personal
data tool, not a medical device, and nothing it shows is medical advice.
