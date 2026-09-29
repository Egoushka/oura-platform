---
title: "Reference"
description: "Every setting and its default, the variables docker-compose.yml passes, the ingest command line, the scripts, HTTP routes, MCP tools and metric names."
order: 4
section: "Reference"
---

## Configuration

The three processes read standard .NET configuration: each project's `appsettings.json`, then environment variables, in which `__` separates the levels, so `Oura__ClientId` sets `Oura:ClientId`. Under Compose only the variables listed in [Compose variables](#compose-variables) reach a container.

### Oura

`Oura:*`, read by `ingest` and `api` ([OuraOptions.cs](../../src/OuraPlatform.Oura/OuraOptions.cs)). `ClientId`, `ClientSecret` and `RedirectUri` are validated at startup: a process with any of them empty does not start.

| key | default | effect |
|---|---|---|
| `ClientId` | none | the OAuth client id |
| `ClientSecret` | none | the client secret, sent in a Basic header |
| `RedirectUri` | `http://localhost:8080/oauth/callback` | must equal a URI registered with the app |
| `Scopes` | the working set, below | scopes requested at `/oauth/start`, space-separated |
| `UseSandbox` | `false` | call the sandbox; no token needed |
| `BackfillFrom` | `2020-01-01` | the earliest day the backfill asks for |
| `ReconcileTrailingDays` | `7` | days the reconcile job re-fetches each cycle |
| `RefreshSkew` | `00:05:00` | how long before expiry the handler asks for a refresh |
| `SpecVersion` | `1.37` | written to `oura_raw.spec_ver` |
| `AuthorizeEndpoint` | `https://cloud.ouraring.com/oauth/authorize` | Oura's consent page |
| `TokenEndpoint` | `https://api.ouraring.com/oauth/token` | Oura's token endpoint |

The default `Scopes` is `personal daily heartrate workout tag session spo2 stress heart_health ring_configuration`: the names Oura's API enforces, read off its `401` bodies, rather than the list in its OpenAPI spec ([Oura API notes](../oura-api-notes.md#scopes--the-specs-list-is-wrong-verified-against-a-live-token)). `UseSandbox` switches the API base address from `https://api.ouraring.com/v2/usercollection/` to `https://api.ouraring.com/v2/sandbox/usercollection/`.

### Ingest

`Ingest:*`, read by `ingest` only ([IngestWorker.cs](../../src/OuraPlatform.Ingest/IngestWorker.cs)).

| key | default | effect |
|---|---|---|
| `ReconcileInterval` | `06:00:00` | pause between cycles, and so between context imports |
| `RetryDelay` | `00:05:00` | wait after a failed cycle |
| `MigrateOnStartup` | `true` | apply migrations before the loop starts |

### Context

`Context:*`, read by `ingest` only ([CalendarImporter.cs](../../src/OuraPlatform.Ingest/CalendarImporter.cs)). Nothing validates this section. [Add a calendar feed](operations.md#add-a-calendar-feed) describes what the importer does with it.

| key | default | effect |
|---|---|---|
| `Calendars:N:Name` | none | rows get the source `calendar:<Name>` |
| `Calendars:N:Url` | none | the iCalendar URL |
| `Calendars:N:Kind` | `meeting` | the value written to `context.kind` |
| `Calendars:N:ExcludeContaining:N` | empty | skip events whose summary contains an entry |
| `Calendars:N:IncludeAllDay` | `false` | import all-day events too |
| `LookbackDays` | `400` | how far back occurrences are imported and pruned |
| `LookaheadDays` | `14` | how far ahead |
| `Interval` | `03:00:00` | declared, read by nothing |
| `TimeZone` | `Europe/Kyiv` | declared, read by nothing |

### Database, logging and ports

- **`ConnectionStrings:Postgres`** is read by all three processes. Each `appsettings.json` sets `Host=localhost;Port=5432;Database=oura;Username=oura;Password=oura`, and Compose replaces it with one built from the `POSTGRES_*` variables with `Host=timescaledb` ([StorageServiceCollectionExtensions.cs](../../src/OuraPlatform.Storage/StorageServiceCollectionExtensions.cs), [docker-compose.yml](../../docker-compose.yml)).
- **`Serilog:MinimumLevel`** is `Information` in each `appsettings.json`, with `Microsoft`, `Npgsql` and the other noisy namespaces at `Warning`. Output is compact JSON on stdout.
- **Ports.** The images set `ASPNETCORE_HTTP_PORTS=8080` ([Dockerfile](../../Dockerfile)). Outside Docker, `dotnet run --project src/OuraPlatform.Api` uses the `http` launch profile, `http://localhost:5210` ([launchSettings.json](../../src/OuraPlatform.Api/Properties/launchSettings.json)), while the default `Oura:RedirectUri` points at port 8080. Register and set a redirect URI that matches the port the api really listens on.

## Compose variables

[docker-compose.yml](../../docker-compose.yml) reads these from `.env` or your shell:

| variable | default | used for |
|---|---|---|
| `POSTGRES_DB` | `oura` | the database name |
| `POSTGRES_USER` | `oura` | the database role |
| `POSTGRES_PASSWORD` | none, required | the role's password |
| `POSTGRES_PORT` | `5432` | the database's port on `127.0.0.1` |
| `API_PORT` | `8080` | the api's port on `127.0.0.1` |
| `MCP_PORT` | `8081` | the mcp port on `127.0.0.1`, container port 8080 |
| `TZ` | `UTC` | the containers' time zone |
| `Oura__ClientId` | none, required | `Oura:ClientId` |
| `Oura__ClientSecret` | none, required | `Oura:ClientSecret` |
| `Oura__RedirectUri` | `http://localhost:8080/oauth/callback` | `Oura:RedirectUri` |
| `Oura__Scopes` | the spec's list, below | `Oura:Scopes` |
| `Oura__UseSandbox` | `false` | `Oura:UseSandbox` |
| `Oura__BackfillFrom` | `2020-01-01` | `Oura:BackfillFrom` |
| `Oura__ReconcileTrailingDays` | `7` | `Oura:ReconcileTrailingDays` |

A required variable that is unset or empty stops Compose with a message saying what to set.

The `x-dotnet-env` block passes `ConnectionStrings__Postgres`, the `Oura__*` variables above and `TZ` to all three .NET services, `mcp` included, although the MCP process reads only the connection string. No other variable reaches a container: not `Context__*`, not `Ingest__*`, not the `GRAFANA_*` lines in `.env.example`.

> [!WARNING]
> The Compose fallback for `Oura__Scopes` is `personal daily heartrate workout tag session spo2Daily`, which is not the code's default. It is the spec's list, which the [Oura API notes](../oura-api-notes.md#scopes--the-specs-list-is-wrong-verified-against-a-live-token) say fails four collections. `.env.example` sets the working set; keep that line.

`TZ` in `.env.example` is `Europe/Kyiv`. The backfill and the reconcile job compute today in UTC regardless ([BackfillJob.cs](../../src/OuraPlatform.Ingest/BackfillJob.cs), [ReconcileJob.cs](../../src/OuraPlatform.Ingest/ReconcileJob.cs)); the MCP `weekly_digest` tool uses the container's local date for its default week ([OuraTools.cs](../../src/OuraPlatform.Mcp/OuraTools.cs)).

## Ingest command line

| arguments | effect |
|---|---|
| none | the ingest loop |
| `--reproject` | rebuild the projections of every ingested collection from `oura_raw`, then exit 0 |
| `--reproject <collection> ...` | only the named collections, by their `Name` in `OuraCollections` |

Under Compose: `docker compose run --rm ingest --reproject`. The image's entrypoint passes the arguments through to the process ([Dockerfile](../../Dockerfile)). `--reproject` always applies pending migrations first and never calls Oura ([ReprojectCommand.cs](../../src/OuraPlatform.Ingest/ReprojectCommand.cs)).

The ingest exits with code 70 when a rotated token pair could not be written to the database ([IngestWorker.cs](../../src/OuraPlatform.Ingest/IngestWorker.cs)).

## Scripts

| script | does | needs |
|---|---|---|
| `scripts/export-warehouse.sh [file]` | dumps `oura_raw` and `ingest_window` | the running stack |
| `scripts/publish.sh <version>` | tests, builds and pushes the three images | docker buildx, the .NET SDK, a registry login |
| `grafana/verify-dashboards.py` | runs every panel's query through Grafana | `GRAFANA_ADMIN_PASSWORD` |
| `tests/record-sandbox-fixtures.sh` | re-records the sandbox fixtures | curl and network access |
| `.githooks/selftest.sh` | checks the commit hooks in a scratch repository | gitleaks |

- `export-warehouse.sh` reads `POSTGRES_DB` and `POSTGRES_USER` from the shell, default `oura`, and writes `oura-warehouse-<date>.sql.gz` unless you pass a path ([export-warehouse.sh](../../scripts/export-warehouse.sh)).
- `publish.sh` pushes to `OURA_REGISTRY`, default `ghcr.io/egoushka`, and runs only the Oura test suite before it builds ([publish.sh](../../scripts/publish.sh)).
- `verify-dashboards.py` runs from the repository root; `GRAFANA_URL` defaults to `http://localhost:3000` and `GRAFANA_ADMIN_USER` to `admin` ([verify-dashboards.py](../../grafana/verify-dashboards.py)).
- `record-sandbox-fixtures.sh` needs no credentials: the sandbox accepts any `Authorization` value ([record-sandbox-fixtures.sh](../../tests/record-sandbox-fixtures.sh)).

## HTTP routes

| process | route | behaviour |
|---|---|---|
| api | `GET /oauth/start` | redirects to Oura's consent page with a new `state` |
| api | `GET /oauth/callback` | exchanges the `code`, stores the token pair, renders a page |
| api | `GET /healthz` | see below |
| api | `GET`, `POST /webhooks/oura` | `501 Not Implemented` |
| mcp | `/mcp` | MCP over HTTP |
| mcp | `GET /healthz` | see below |

Sources: [Program.cs](../../src/OuraPlatform.Api/Program.cs) for the api, [Program.cs](../../src/OuraPlatform.Mcp/Program.cs) for mcp. No route authenticates its caller.

- **`/oauth/start`** issues a random `state` that the api keeps in memory for 10 minutes and accepts once ([OAuthEndpoints.cs](../../src/OuraPlatform.Api/OAuthEndpoints.cs)).
- **`/oauth/callback`** renders "Not connected" with the reason when Oura returns an `error`, when there is no `code`, or when the `state` is unknown, expired or used. It does not catch a failed code exchange, such as an authorization code past its 10-minute lifetime, which ends in an error response; start again at `/oauth/start`.
- **`/webhooks/oura`** exists so a callback URL can be registered with Oura ahead of Stage 3. The code notes that it must never answer `410`, which cancels the subscription at Oura's end.
- **`/healthz`** answers `Healthy` or `Degraded` with status 200, and `Unhealthy` with 503 when Postgres is unreachable. The api is `Degraded` until a token is stored; mcp is `Degraded` while `daily` is empty ([DatabaseHealthCheck.cs](../../src/OuraPlatform.Api/DatabaseHealthCheck.cs)).

## MCP tools

The server introduces itself as `oura-platform`, version `0.3.0`, with instructions to start with `coverage`. Every tool is marked read-only and idempotent ([OuraTools.cs](../../src/OuraPlatform.Mcp/OuraTools.cs)).

| tool | parameters | returns |
|---|---|---|
| `coverage` | none | the span and row counts held, and the metric catalogue |
| `context_kinds` | none | each context kind with its events and first and last day |
| `compare_periods` | `metric`, `from`, `to`, `baselineFrom`, `baselineTo` | one metric over a period against a baseline |
| `correlate` | `metric`, `contextKind`, `lagDays` | one metric on days with and without a context kind |
| `outlier_nights` | `metric`, `count` | the days furthest from the mean |
| `weekly_digest` | `weekStart` | every metric for a week, beside the week before |
| `night_detail` | `night` | one night's scores, phase minutes and hourly bands |

- **Dates** are `yyyy-MM-dd`. A full timestamp is accepted and cut to its date.
- **`coverage`** reports the first and last day in `daily`, and counts of days, `Nights` (rows in `sleep_sessions`, naps included), workouts and context events, then every metric's name, label, unit and direction.
- **`compare_periods`** summarises the period and the baseline: days, mean, median, standard deviation, minimum and maximum. `from` and `to` are inclusive. Give both baseline dates or neither; with neither, the baseline is the equally long window just before the period.
- **`correlate`** splits the days that have the metric into those `lagDays` after a day with the context kind and the rest. `lagDays` defaults to 1, the night after the event; 0 is the same day. With no matching day, `note` says so instead of reporting an effect.
- **`outlier_nights`** ranks days by absolute z-score in both directions. `count` defaults to 5 and is clamped to 1 to 30.
- **`weekly_digest`** takes the seven days from `weekStart`. Without it, the week is the last complete Monday to Sunday. Metrics with no data that week are left out, and `note` flags a week that has not finished.
- **`night_detail`** keys a night on the day it ended: the night of the 4th into the 5th is the 5th. It returns the headline scores, HRV, resting heart rate, temperature deviation and bedtimes, minutes per sleep phase, and HRV and heart rate averaged per hour from the first sample. It returns nothing when `daily` has no row for that night.

`compare_periods`, `correlate` and each metric of `weekly_digest` carry a `direction` of `better`, `worse`, `unchanged` or `unknown` that accounts for metrics where lower is better. `compare_periods` and `correlate` add a `note` when the smaller side has fewer than 14 days. Invalid input returns an MCP error that names the problem; an unknown metric lists the valid ones.

## Metrics

The names the tools accept, case-insensitive, and the `daily` column behind each ([DailyMetric.cs](../../src/OuraPlatform.Storage/DailyMetric.cs)):

| metric | column | unit | higher is better |
|---|---|---|---|
| `readiness` | `readiness_score` | score | yes |
| `sleep_score` | `sleep_score` | score | yes |
| `activity` | `activity_score` | score | yes |
| `hrv` | `hrv_avg` | ms | yes |
| `resting_hr` | `rhr_lowest` | bpm | no |
| `temperature` | `temp_deviation` | C | no |
| `spo2` | `spo2_avg` | % | yes |
| `breathing_disturbance` | `breathing_disturbance_index` | index | no |
| `stress_high` | `stress_high_sec` | s | no |
| `recovery_high` | `recovery_high_sec` | s | yes |
| `steps` | `steps` | steps | yes |
| `active_calories` | `active_calories` | kcal | yes |
| `vo2_max` | `vo2_max` | ml/kg/min | yes |
| `vascular_age` | `vascular_age` | years | no |

`daily` also has `pulse_wave_velocity` and `resilience_level`, which no tool takes.
