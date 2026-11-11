---
title: "Quickstart"
description: "Five minutes from a clone to a populated warehouse: register an Oura app, run ./setup.sh, authorize once, ask questions with SQL, Grafana or MCP."
order: 1
section: "Get started"
---

You register an Oura application, run `./setup.sh`, and authorize once in a browser. The first backfill then fills the warehouse, newest data first. Plan on five minutes of your time plus however long Oura's API takes to hand over your history.

## Before you start

- Docker with the Compose plugin. The stack pulls prebuilt images from `ghcr.io/egoushka`, so you need no .NET SDK.
- An Oura account with a ring, and a browser on the machine that runs the stack (ports are published on `127.0.0.1` only).

> [!NOTE]
> **Membership (reported, not verified by us).** Oura's support pages and several third-party integration guides say that without an active Oura Membership you cannot use the Oura API, and that this applies to Gen3 and newer rings (Gen2 is said to be exempt). We found no statement of this on Oura's developer pages. If your backfill returns only empty or `401` responses, check your membership first.

## 1. Register an Oura application (2 minutes)

Personal Access Tokens were retired in December 2025, so an OAuth application is required.

1. Open <https://developer.ouraring.com> (the older <https://cloud.ouraring.com/oauth/applications> also works) and sign in with your Oura account.
2. Under **My Applications**, create a new application. Any name and description will do.
3. Add this redirect URI, exactly (Oura compares it verbatim):

   ```text
   http://localhost:8080/oauth/callback
   ```

   `setup.sh` prints the exact URI again at the end, in case you pick another port.
4. Tick the scopes if the form asks. The stack requests `personal daily heartrate workout tag session spo2 stress heart_health ring_configuration`.
5. Copy the **client id** and **client secret**.

A new app is limited to 10 users until Oura approves it. That is plenty for one person.

## 2. Run setup (1 minute)

```bash
git clone https://github.com/Egoushka/oura-platform.git
cd oura-platform
./setup.sh --grafana
```

It asks for the client id and secret, the day you got the ring (this bounds the backfill) and your timezone, generates the database and Grafana passwords, writes `.env`, and starts the stack. Drop `--grafana` if you run your own Grafana, add `--build` to build the images from source, `--no-start` to only write `.env`. It never overwrites an existing `.env`.

Prefer to do it by hand: `cp .env.example .env`, set `Oura__ClientId`, `Oura__ClientSecret`, `POSTGRES_PASSWORD` (compose refuses to start without one) and `docker compose up -d`. The [reference](reference.md#compose-variables) lists every variable.

> [!WARNING]
> Do not change `Oura__Scopes` unless you know why. Oura's OpenAPI spec lists `spo2Daily` and omits `stress` and `heart_health`; the authorize endpoint silently ignores unknown names and the affected collections then fail with `401`. The default in `docker-compose.yml`, `.env.example` and the code is the working set from the [Oura API notes](../oura-api-notes.md#scopes--the-specs-list-is-wrong-verified-against-a-live-token). Of these, Oura's public auth page documents `personal daily heartrate workout tag session spo2`; `stress`, `heart_health` and `ring_configuration` come from observed 401 responses.

Follow the ingest log:

```bash
docker compose logs -f ingest
```

The ingest applies the migrations, then warns once, which is expected:

```text
No Oura token stored. Waiting — complete the handshake at /oauth/start.
```

`curl -s localhost:8080/healthz` answers `Degraded`, status 200, until a token is stored.

## 3. Authorize (1 minute)

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

## Look at it

With `--grafana`, open <http://localhost:3000> (user `admin`, password in `.env` as `GRAFANA_ADMIN_PASSWORD`): the three dashboards are already provisioned. Panels fill in as the backfill reaches recent days.

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

The `mcp` container serves MCP over HTTP at `/mcp`, published on port 8081 ([Program.cs](../../src/OuraPlatform.Mcp/Program.cs)). Add it to an MCP client (see [which MCP to use](mcp.md)):

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

Next: [Which MCP to use](mcp.md), [Operate the warehouse](operations.md) to add calendar context, and [Troubleshooting](troubleshooting.md) if something above did not match.

## No API access? Export a CSV instead

If you cannot get API access (no membership, or Oura has not enabled it for your account), Oura lets you download your own data from the Membership Hub / account settings on the Oura website. That export is a set of CSV files that you can open in a spreadsheet or load into any database. **This repository does not import them**, so the dashboards and MCP tools will not see that data. The file layout is Oura's and we have not checked it against the current export.
