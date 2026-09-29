---
title: "Overview"
description: "What oura-platform stores, how its three processes fit together, what it leaves out, and where the project stands."
order: 0
section: "Get started"
---

oura-platform copies your Oura Ring data out of Oura's cloud into a Postgres/TimescaleDB database that you run. It keeps every document Oura returns verbatim, projects it into tables you can query with SQL, and writes calendar events and Oura's own tags into a `context` table beside it. The point is the join: readiness, HRV and sleep against what happened the evening before, which Oura never sees.

It is written in C# on .NET 10 and runs under Docker Compose on one host ([README](../../README.md), [Directory.Build.props](../../Directory.Build.props), [docker-compose.yml](../../docker-compose.yml)).

## Who it is for

One person with one Oura account. The token table holds a single row (`provider = 'oura'`), and the design allows exactly one ingest instance, because two would race to spend the same single-use refresh token ([0001_init.sql](../../db/migrations/0001_init.sql), [IngestWorker.cs](../../src/OuraPlatform.Ingest/IngestWorker.cs)).

You register your own Oura OAuth application. The README notes that registration is free and that unapproved applications are capped at 10 users.

## How it works

1. You authorize once in a browser. The `api` process sends you to Oura from `/oauth/start` and stores the token pair it gets back at `/oauth/callback` in `oauth_tokens`.
2. The `ingest` process applies the database migrations and waits for that token. It then backfills every Oura collection from `Oura:BackfillFrom` to today, newest window first, and from then on re-fetches the trailing 7 days every 6 hours, because Oura amends recent days.
3. Every document lands verbatim in `oura_raw` first. The projector then writes the typed tables: `daily`, `sleep_sessions`, `workouts`, `tags`, and the TimescaleDB hypertables `sleep_series`, `hypnogram`, `hr_samples` and `ring_battery`.
4. After each cycle, Oura's `enhanced_tag` documents and any iCalendar feeds you configure are written to `context`.
5. You read the result with SQL, with the three Grafana dashboards in `grafana/dashboards/`, or through the `mcp` process: seven read-only MCP tools at `/mcp` that answer with aggregates.

[Architecture](architecture.md) follows one cycle through the code. Sources: [README](../../README.md#what-it-stores), [IngestWorker.cs](../../src/OuraPlatform.Ingest/IngestWorker.cs), [DocumentProjector.cs](../../src/OuraPlatform.Storage/DocumentProjector.cs), [OuraTools.cs](../../src/OuraPlatform.Mcp/OuraTools.cs).

## What it does not do

- **No real-time data.** Oura's nightly data lands mid-morning the next day ([Oura API notes](../oura-api-notes.md#data-availability-semantics)). Webhooks, Stage 3 of the roadmap, are skipped on purpose: `/webhooks/oura` answers `501` ([Program.cs](../../src/OuraPlatform.Api/Program.cs)).
- **No Grafana in the stack.** The dashboards are JSON files for a Grafana you already run ([README](../../README.md#6-grafana)).
- **No authentication.** Neither the `api` nor the `mcp` process checks its callers. Compose publishes both on `127.0.0.1` only ([docker-compose.yml](../../docker-compose.yml)).
- **No Personal Access Tokens.** Oura deprecated them in December 2025, and OAuth2 is the only path in the code ([Oura API notes](../oura-api-notes.md#authentication)).
- **No deletions.** Nothing removes a document from `oura_raw`. The only `delete` in the code prunes calendar events that have left their feed ([ContextRepository.cs](../../src/OuraPlatform.Storage/ContextRepository.cs)).
- **No raw arrays over MCP.** The MCP server reads the warehouse, never the Oura API, and returns aggregates, never 5-minute samples ([OuraTools.cs](../../src/OuraPlatform.Mcp/OuraTools.cs)).
- **No medical advice.** The README calls it a personal data tool, not a medical device ([README](../../README.md#license)).

## Where it stands

- **Stages 1, 2 and 4 of 8 are built:** OAuth, storage, backfill and the scheduled poll; three Grafana dashboards; the calendar and tag importers; the MCP server. Stage 3, webhooks, is skipped deliberately. Stages 5 to 8 (Home Assistant and environment sensors, Polar H10 RR/ECG, local BLE, body metrics and CGM) are not started ([README](../../README.md), [CLAUDE.md](../../CLAUDE.md#roadmap)).
- **No releases.** The repository has no tags, and these docs describe the `master` branch. `scripts/publish.sh` pushes images under a version you pass it ([publish.sh](../../scripts/publish.sh)).
- **Two test suites.** The Oura client suite runs with no credentials, network or Docker. The storage suite starts TimescaleDB with Testcontainers. CI runs both on every pull request and every push to `master` ([ci.yml](../../.github/workflows/ci.yml)). Nothing tests the ingest loop, the OAuth endpoints or the MCP tool layer directly.
- **Some configuration disagrees with the code.** The Compose fallback for `Oura__Scopes` differs from the code's default, and the `Context__*` variables in `.env.example` never reach a container. [Status and evidence](status.md#where-the-repository-disagrees-with-itself) lists each case.

## Where to go next

- [Quickstart](quickstart.md): run the stack, authorize, watch the first backfill, query it.
- [Operate the warehouse](operations.md): calendar feeds, Grafana, re-authorizing, re-projecting, moving hosts.
- [Architecture](architecture.md): the processes, the ingest cycle, the token, storage and joins.
- [Reference](reference.md): settings, Compose variables, the command line, HTTP routes, MCP tools, metrics.
- [Database schema](database.md): every table and view, and how days and nights line up.
- [Status and evidence](status.md): what works, what is partial, what is not built.
