---
title: "Operate the warehouse"
description: "Add a calendar feed, install the Grafana dashboards, re-authorize, re-project after a projection change, move hosts, publish images, read the logs."
order: 2
section: "Guides"
---

Each section is one task, start to finish, against the Compose stack from the [quickstart](quickstart.md).

## Add a calendar feed

The calendar importer reads iCalendar URLs, such as Google Calendar's private "Secret address in iCal format", and writes each event into `context` ([CalendarImporter.cs](../../src/OuraPlatform.Ingest/CalendarImporter.cs)). It does not use the Google Calendar API, so there is no second OAuth client and no second refresh token. The URL itself is a credential, and the importer's own messages name the feed instead of printing it.

A feed is an entry in the indexed list `Context:Calendars`. As environment variables:

```dotenv title=".env"
Context__Calendars__0__Name=work
Context__Calendars__0__Url=https://calendar.example/your-secret-address/basic.ics
Context__Calendars__0__Kind=meeting
Context__Calendars__0__ExcludeContaining__0=birthday
Context__Calendars__0__ExcludeContaining__1=out of office
```

| key | default | effect |
|---|---|---|
| `Name` | none | rows get the source `calendar:<Name>` |
| `Url` | none | the iCalendar URL |
| `Kind` | `meeting` | the value written to `context.kind` |
| `ExcludeContaining` | empty | skip events whose summary contains an entry, ignoring case |
| `IncludeAllDay` | `false` | import all-day events too |

A second feed is `Context__Calendars__1__*`. Nothing validates this section at startup, so a feed with an empty `Url` shows up only as an import error in the log.

> [!WARNING]
> `docker-compose.yml` passes no `Context__*` variable to any container. Compose reads `.env` to fill in the compose file, and the `ingest` service's environment is only the `x-dotnet-env` block. Setting the variables in `.env` alone does nothing.

Add them to the `ingest` service yourself. Compose merges a `docker-compose.override.yml` next to `docker-compose.yml` into it, and the URL can stay in `.env`:

```yaml title="docker-compose.override.yml"
services:
  ingest:
    environment:
      Context__Calendars__0__Name: "work"
      Context__Calendars__0__Url: "${Context__Calendars__0__Url}"
      Context__Calendars__0__Kind: "meeting"
      Context__Calendars__0__ExcludeContaining__0: "birthday"
```

Then run `docker compose up -d`. The recreated ingest runs a cycle as soon as it starts and logs `Calendar 'work': {Written} event(s) upserted, {Pruned} no longer in the feed removed.`

What the importer does with a feed:

- It expands recurring events into one row per occurrence, from `Context:LookbackDays` (400) days back to `Context:LookaheadDays` (14) days ahead, at most 20,000 per feed.
- It skips all-day events unless `IncludeAllDay` is set, events with `STATUS:CANCELLED`, and events whose summary matches `ExcludeContaining`.
- Each row's `source_id` is the event's UID plus the occurrence's start in UTC, so re-reading the feed updates rows instead of adding copies. `label` is the summary; `meta` holds the calendar name, location, attendee count and the all-day and recurring flags.
- Rows of that source inside the window that the feed no longer lists are deleted: a cancelled meeting simply stops appearing in a feed ([ContextRepository.cs](../../src/OuraPlatform.Storage/ContextRepository.cs)).
- A feed that answers with something other than iCalendar, such as the HTML page a revoked Google address returns, fails with `did not return parseable iCalendar data`. A failing feed logs `Calendar '<Name>' failed to import; continuing.` and stops neither the other feeds nor the Oura ingest ([IngestWorker.cs](../../src/OuraPlatform.Ingest/IngestWorker.cs)).

The import runs once per ingest cycle, after the reconcile, so every `Ingest:ReconcileInterval` (6 hours). `Context:Interval` in `.env.example` is read by nothing. The cycle starts only once an Oura token exists, so feeds are not read before you authorize.

> [!NOTE]
> `context_daily` computes each day and its `last_end_hour` in `Europe/Kyiv`, written into the view in [0004_context_identity.sql](../../db/migrations/0004_context_identity.sql). `Context:TimeZone` is declared but read by nothing. In another time zone, that view's days and hours are still Kyiv's, and changing it takes a new migration: applied migrations are never edited ([CLAUDE.md](../../CLAUDE.md#conventions)).

Oura's own tags need no configuration. Every cycle, the tag importer copies each stored `enhanced_tag` into `context` with the source `oura_tag`. It strips the prefixes `tag_generic_`, `generic_` and `tag_` from the tag code, so `generic_alcohol` becomes the kind `alcohol` ([OuraTagContextImporter.cs](../../src/OuraPlatform.Ingest/OuraTagContextImporter.cs)).

## Install the Grafana dashboards

The stack runs no Grafana. The dashboards are meant for one you already run ([README](../../README.md#6-grafana)).

1. Provision the datasource from [timescaledb.yml](../../grafana/provisioning/datasources/timescaledb.yml). It defines a Postgres datasource with the uid `oura-timescaledb` at `timescaledb:5432`, and fills `${POSTGRES_DB}`, `${POSTGRES_USER}` and `${POSTGRES_PASSWORD}` from Grafana's environment. Grafana therefore needs a route to the `timescaledb` service and those three variables.
2. Copy the three JSON files in [grafana/dashboards/](../../grafana/dashboards/) into the directory your Grafana provisions dashboards from. The repository ships no dashboard provider file, so that provider comes from your Grafana's own configuration. Every panel queries the datasource by the uid `oura-timescaledb`.
3. From the repository root, run every panel's query through Grafana:

```bash
GRAFANA_ADMIN_PASSWORD=... ./grafana/verify-dashboards.py
```

`GRAFANA_URL` (default `http://localhost:3000`) and `GRAFANA_ADMIN_USER` (default `admin`) point it at another Grafana. The script sends each panel's first query to `/api/ds/query` over 2020 to 2030 and prints `ok` with a row count or `FAIL` with the error. It exits 1 if any panel fails; a panel that returns 0 rows prints `ok` ([verify-dashboards.py](../../grafana/verify-dashboards.py)).

| dashboard | uid | default range | reads |
|---|---|---|---|
| Sleep & recovery — when, not how much | `oura-sleep-recovery` | 90 days | `sleep_series`, `hypnogram`, `sleep_nightly` |
| Early warning — resting HR and temperature | `oura-early-warning` | 180 days | `daily` |
| Distributions — spread, not averages | `oura-distributions` | 365 days | `daily`, `context_daily` |

**Sleep & recovery** shows HRV across each night aligned on that night's bedtime, an HRV heatmap in 30-minute bands, the hypnogram, heart rate and HRV on the clock, and how much of each night the ring measured.

**Early warning** puts the latest night's resting heart rate and HRV beside their means over up to 29 earlier days, with its temperature deviation. It draws 30, 90 and 365-day rolling means of resting heart rate and temperature deviation, and HRV's 7-day mean against its 30-day mean.

**Distributions** shows readiness by weekday as a five-number summary and as quartiles, readiness on days whose meetings ended at or after 19:00, readiness by context kind, the spread of the three scores, and meeting hours per day. An empty "Calendar load per day" panel means no calendar feed is configured.

> [!NOTE]
> The two context panels in Distributions join `context_daily` on the same `day` as the `daily` row, with no lag. The MCP `correlate` tool shifts context by one day by default, because a late evening affects the night after it ([AnalyticsRepository.cs](../../src/OuraPlatform.Storage/AnalyticsRepository.cs)).

The JSON files are the source of truth. Change a dashboard in its file, or export it back after editing it in Grafana ([CLAUDE.md](../../CLAUDE.md#conventions)).

## Re-authorize

If the ingest logs `Oura authorization is unrecoverable. Stopping.` and exits with code 70, a rotated token pair reached Oura but was not written to the database, and the old refresh token is already dead ([IngestWorker.cs](../../src/OuraPlatform.Ingest/IngestWorker.cs), [OuraTokenStore.cs](../../src/OuraPlatform.Storage/OuraTokenStore.cs)).

Visit `/oauth/start` again. `oura_raw` is untouched, so nothing else is lost ([README](../../README.md#re-authorizing)).

Re-consenting while the ingest runs, to add a scope for instance, takes effect without a restart: the api writes the new pair, and the ingest's next `401` re-reads `oauth_tokens` and retries with it ([OuraAuthenticationHandler.cs](../../src/OuraPlatform.Oura/Auth/OuraAuthenticationHandler.cs)). A collection that was skipped for a missing scope is tried again on the next cycle.

## Change a projection

The projector reads only stored payloads. After you change `DocumentProjector`, rebuild the tables from `oura_raw` instead of downloading history again ([README](../../README.md#changing-a-projection)):

```bash
docker compose stop ingest
docker compose build ingest
docker compose run --rm ingest --reproject                   # every ingested collection
docker compose run --rm ingest --reproject sleep daily_spo2  # named collections only
docker compose up -d ingest
```

`--reproject` applies pending migrations and projects every stored document of each collection again. It re-imports Oura tags into `context` when you name no collection or name `enhanced_tag`. It then logs `Nothing was fetched from Oura.` and exits 0 without starting the ingest loop ([ReprojectCommand.cs](../../src/OuraPlatform.Ingest/ReprojectCommand.cs), [Program.cs](../../src/OuraPlatform.Ingest/Program.cs)).

Collection names are the `Name` values in [OuraCollections.cs](../../src/OuraPlatform.Oura/OuraCollections.cs): `vo2_max`, not the REST path `vO2_max`. An unknown name stops the command with `Unknown Oura collection.`

> [!NOTE]
> Re-projecting upserts. It does not empty the tables first, so a row that the changed projection no longer produces stays until you delete it.

## Move to another host

On the old host, with the stack running:

```bash
./scripts/export-warehouse.sh
```

The script dumps the rows of `oura_raw` and `ingest_window`, and nothing else, into `oura-warehouse-<date>.sql.gz` (the date comes from the database container), or into the path you pass it ([export-warehouse.sh](../../scripts/export-warehouse.sh)). It reads `POSTGRES_DB` and `POSTGRES_USER` from your shell, not from `.env`, so export them first if you changed them. The projections stay behind, because the new host rebuilds them with `--reproject`. So does `oauth_tokens`, because the new host authorizes itself. `.gitignore` keeps `oura-warehouse-*.sql.gz` out of git: the dump is personal health data.

The restore commands the script prints name the services `oura-timescaledb` and `oura-ingest`. In this repository's `docker-compose.yml` they are `timescaledb` and `ingest`. On the new host, with `.env` in place:

```bash
docker compose up -d        # the ingest applies the migrations, then waits for a token
docker compose stop ingest
gunzip -c oura-warehouse-<date>.sql.gz | docker compose exec -T timescaledb psql -U oura -d oura
docker compose run --rm ingest --reproject
docker compose up -d
```

Restore before you authorize the new host. The dump holds rows only, so it collides with anything an ingest has already written. Then authorize at `/oauth/start`. The backfill skips the windows recorded in the restored `ingest_window`.

> [!CAUTION]
> Stop the ingest on the old host before the new one runs, and leave it stopped. The refresh token is single-use: two ingests against one Oura application race to spend it, and the loser holds a token Oura has already invalidated ([README](../../README.md#moving-to-another-host)).

## Publish images

For a host that pulls images instead of building them from source:

```bash
./scripts/publish.sh 0.1.0
```

[publish.sh](../../scripts/publish.sh) refuses a dirty working tree and runs `dotnet test tests/OuraPlatform.Oura.Tests` first, which needs the .NET SDK locally; the storage suite does not run. It then builds `oura-api`, `oura-ingest` and `oura-mcp` for `linux/amd64` with `docker buildx`, labels each with the commit, version and source, pushes them to `OURA_REGISTRY` (default `ghcr.io/egoushka`) and prints each digest. Log in to the registry first.

The repository's `docker-compose.yml` builds from source and has no variant that pulls these images.

## Read the logs

All three processes log compact JSON to stdout, with the message template in `@mt`. `docker compose logs -f ingest` follows the one that does the work. Both web processes also answer `GET /healthz`:

| endpoint | `Healthy` | `Degraded` | `Unhealthy`, status 503 |
|---|---|---|---|
| api, port 8080 | a token is stored | no token yet | Postgres unreachable |
| mcp, port 8081 | `daily` has rows | `daily` is empty | Postgres unreachable |

Sources: [DatabaseHealthCheck.cs](../../src/OuraPlatform.Api/DatabaseHealthCheck.cs), [Program.cs](../../src/OuraPlatform.Mcp/Program.cs). Messages worth knowing, from the ingest:

- `No Oura token stored. Waiting — complete the handshake at /oauth/start.` Authorize at `/oauth/start`.
- `{Collection}: skipped — the token lacks the '{Scope}' scope.` Add the scope to `Oura__Scopes` and authorize again ([quickstart](quickstart.md#watch-the-first-backfill)).
- `{Collection}: backfilled {Windows} window(s) from {From} and found no documents at all.` Either the ring never produced that data or the grant lacks a scope ([BackfillJob.cs](../../src/OuraPlatform.Ingest/BackfillJob.cs)).
- `Oura rate limit hit (tier {Tier}); retry {Attempt} in {Delay}.` The client retries up to 6 times and obeys `Retry-After` ([OuraServiceCollectionExtensions.cs](../../src/OuraPlatform.Oura/OuraServiceCollectionExtensions.cs)).
- `Could not project {DocType}/{DocId}; the raw payload is stored and can be replayed.` A model could not read a document. Fix the model, then [re-project](#change-a-projection).
- `Ingest cycle failed; retrying in {Delay}.` The whole cycle runs again after `Ingest:RetryDelay`, 5 minutes by default.
- `Context import failed; health ingest is unaffected.` A tag or calendar import threw; the Oura ingest carries on.
- `Oura authorization is unrecoverable. Stopping.` See [Re-authorize](#re-authorize).
