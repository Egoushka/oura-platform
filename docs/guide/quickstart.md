---
title: "Quickstart"
description: "Run the stack with Docker Compose, authorize your Oura account once, watch the first backfill, and query the warehouse with SQL and an MCP client."
order: 1
section: "Get started"
---

You clone the repository, register an Oura application, fill in `.env`, start four containers and authorize once in a browser. The first backfill then fills the warehouse, newest data first.

## Before you start

- Docker with the Compose plugin. The images build from the repository's [Dockerfile](../../Dockerfile), so you need no .NET SDK for this page.
- An Oura account with a ring.
- A browser that can reach port 8080 on the machine that runs the stack. Compose publishes the `api`, `mcp` and database ports on `127.0.0.1` only ([docker-compose.yml](../../docker-compose.yml)).

## Register an Oura application

Create an application at <https://developer.ouraring.com>. The README notes that <https://cloud.ouraring.com/oauth/applications> also works. Add this redirect URI to it:

```text
http://localhost:8080/oauth/callback
```

Oura compares redirect URIs verbatim and accepts several, so also register any other address you will reach the stack by ([.env.example](../../.env.example)). The URI the stack sends is `Oura__RedirectUri`, and the one above is its default ([OuraOptions.cs](../../src/OuraPlatform.Oura/OuraOptions.cs)).

> [!NOTE]
> Personal Access Tokens do not work. Oura deprecated them in December 2025, and the code has no path for them.

## Configure

```bash
git clone https://github.com/Egoushka/oura-platform.git
cd oura-platform
cp .env.example .env
```

In `.env`, fill in these four:

| variable | value |
|---|---|
| `Oura__ClientId` | your application's client id |
| `Oura__ClientSecret` | its client secret |
| `POSTGRES_PASSWORD` | a password for the database |
| `Oura__BackfillFrom` | about the day you got the ring |

The backfill walks every window back to `Oura__BackfillFrom`, empty or not, so a date near your first night with the ring saves requests. Its default is `2020-01-01`. Leave the rest as `.env.example` has it; the [reference](reference.md#compose-variables) lists each variable.

> [!WARNING]
> Keep the `Oura__Scopes` line from `.env.example`. Without it, `docker-compose.yml` falls back to `personal daily heartrate workout tag session spo2Daily`, the list in Oura's OpenAPI spec, and overrides the code's default. The [Oura API notes](../oura-api-notes.md#scopes--the-specs-list-is-wrong-verified-against-a-live-token) record that this list consents without an error and then fails four collections with `401`.

Two blocks in `.env.example` do nothing under Compose. `GRAFANA_ADMIN_USER` and `GRAFANA_ADMIN_PASSWORD` are read only by `grafana/verify-dashboards.py`, from your shell, and `GRAFANA_PORT` is read by nothing. The `Context__*` block never reaches a container, because `docker-compose.yml` passes each service only the variables in its `x-dotnet-env` block. [Add a calendar feed](operations.md#add-a-calendar-feed) shows how to pass them.

## Start the stack

```bash
docker compose up -d
```

The first run builds three images from the one Dockerfile, for `OuraPlatform.Ingest`, `OuraPlatform.Api` and `OuraPlatform.Mcp`, and starts `timescale/timescaledb:2.17.2-pg17`. The three .NET services start once the database passes its healthcheck.

Follow the ingest log:

```bash
docker compose logs -f ingest
```

The processes log compact JSON, one object per line, with the message template in `@mt` ([Program.cs](../../src/OuraPlatform.Ingest/Program.cs)). The ingest applies the migrations, logs `Database schema is up to date`, and then warns once:

```text
No Oura token stored. Waiting — complete the handshake at /oauth/start.
```

That is expected: it checks for a token every 30 seconds ([IngestWorker.cs](../../src/OuraPlatform.Ingest/IngestWorker.cs)). The api reports the same state:

```bash
curl -s localhost:8080/healthz
```

It answers `Degraded`, with status 200, until a token is stored ([DatabaseHealthCheck.cs](../../src/OuraPlatform.Api/DatabaseHealthCheck.cs)).

## Authorize

Open <http://localhost:8080/oauth/start> in the browser and approve the request at Oura. You land on a page headed "Connected" that shows when the access token expires and which scopes Oura granted ([OAuthEndpoints.cs](../../src/OuraPlatform.Api/OAuthEndpoints.cs)).

The `state` value that `/oauth/start` issues lives in the api's memory for 10 minutes and works once. If the callback page says "Unknown, expired or already-used state value.", start again at `/oauth/start`.

Within 30 seconds the ingest finds the token and starts the backfill. `curl -s localhost:8080/healthz` now answers `Healthy`.

## Watch the first backfill

The backfill goes collection by collection and, within each, from the newest window back, so recent days arrive long before 2020 ([BackfillJob.cs](../../src/OuraPlatform.Ingest/BackfillJob.cs)). Each collection logs `{Collection}: backfilling {Count} window(s).` and then `backfill complete`. After the last collection, the reconcile job re-fetches the last 7 days and logs `Reconciled {From}..{To}`.

Every finished window is recorded in `ingest_window`, so you can stop and restart the container and it resumes. To see progress from the database:

```bash
docker compose exec timescaledb psql -U oura -d oura -c "select doc_type, count(*) as windows, sum(document_count) as documents from ingest_window group by doc_type order by doc_type;"
```

This and the other `psql` commands on this page assume the default `POSTGRES_USER` and `POSTGRES_DB`, both `oura`.

If a collection logs `skipped — the token lacks the '...' scope`, the token was granted without a scope that collection needs. To fix it:

1. Add the scope to `Oura__Scopes` in `.env`.
2. Run `docker compose up -d`, so the api builds its authorize link with the new list.
3. Authorize again at `/oauth/start`.
4. Run `docker compose restart ingest`. Otherwise the skipped collection is retried on the next cycle, up to 6 hours later.

## Query the warehouse

Open `psql` inside the database container:

```bash
docker compose exec timescaledb psql -U oura -d oura
```

The last week of the wide `daily` table:

```sql
select day, readiness_score, sleep_score, hrv_avg, rhr_lowest, temp_deviation
from daily
order by day desc
limit 7;
```

A day with no data has no row, and a value Oura did not measure is `NULL`: nothing writes a zero in its place ([CLAUDE.md](../../CLAUDE.md#architecture-invariants)). [Database schema](database.md) lists every table and view, with example joins.

## Ask it questions over MCP

The `mcp` container serves MCP over HTTP at `/mcp`, published on port 8081 ([Program.cs](../../src/OuraPlatform.Mcp/Program.cs)). Add it to an MCP client:

```json
{ "mcpServers": { "oura": { "url": "http://localhost:8081/mcp" } } }
```

Its health check answers `Degraded` until `daily` has a row, then `Healthy`:

```bash
curl -s localhost:8081/healthz
```

Start with the `coverage` tool: it reports the date span the warehouse holds and the metric names the other tools accept. The [reference](reference.md#mcp-tools) lists all seven tools.

> [!IMPORTANT]
> Neither port authenticates its callers, and the MCP server serves health data. Compose binds both to `127.0.0.1`. Put an authenticating proxy in front before you expose either.

## Try it against the Oura sandbox

With `Oura__UseSandbox=true` the client calls Oura's sandbox, which accepts any `Authorization` value and returns fabricated data ([Oura API notes](../oura-api-notes.md#sandbox)). The ingest then skips the wait for a token and fetches every collection except `personal_info`, which the sandbox does not serve ([IngestWorker.cs](../../src/OuraPlatform.Ingest/IngestWorker.cs), [OuraCollections.cs](../../src/OuraPlatform.Oura/OuraCollections.cs)).

`docker-compose.yml` and `OuraOptions` still require `Oura__ClientId` and `Oura__ClientSecret`, so set both to any non-empty placeholder. A recent `Oura__BackfillFrom` keeps the run short. The api's health check stays `Degraded`, since no token is ever stored.

> [!CAUTION]
> Use a separate database for the sandbox. `ingest_window` does not record which API a window came from, so a later production backfill against the same database skips every window the sandbox run completed, and the fabricated documents stay in `oura_raw` ([BackfillJob.cs](../../src/OuraPlatform.Ingest/BackfillJob.cs)). `docker compose down -v` deletes the `timescale-data` volume, and everything the warehouse holds with it.

Next: [Operate the warehouse](operations.md) to add calendar context and the Grafana dashboards.
