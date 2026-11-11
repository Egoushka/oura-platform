---
title: "Troubleshooting"
description: "The failures a first run hits: compose refusing to start, the redirect URI, empty dashboards, 401s, missing scopes, GHCR pulls and Grafana."
order: 7
section: "Project"
---

Start with `docker compose ps` and `docker compose logs --tail 100 ingest`.

## `docker compose up` stops with "set POSTGRES_PASSWORD" or "register an app"

A required variable is unset or empty in `.env`. Run `./setup.sh`, or fill in `Oura__ClientId`, `Oura__ClientSecret` and `POSTGRES_PASSWORD` by hand. Compose reads `.env` from the directory you run it in.

## `pull access denied` or `manifest unknown` for `ghcr.io/egoushka/...`

The images are published when a release tag is pushed. If you hit this, either no release exists yet or the GHCR package is still private. Build from source instead:

```bash
docker compose -f docker-compose.yml -f compose.build.yml up -d --build
```

To pin a version, set `OURA_VERSION=0.1.0` in `.env`.

## Oura says "redirect_uri mismatch" or "invalid redirect"

The redirect URI is compared verbatim. It must equal `Oura__RedirectUri` in `.env` (default `http://localhost:8080/oauth/callback`): same scheme, host, port and path, no trailing slash. Register the same string on the Oura application, then start again at `/oauth/start`.

## The callback page says "Unknown, expired or already-used state value"

The `state` lives in the api's memory for 10 minutes and works once. Restarting the api also drops it. Open `/oauth/start` again.

## The ingest keeps logging "No Oura token stored"

You have not completed the browser handshake. Open <http://localhost:8080/oauth/start> and approve at Oura. `curl -s localhost:8080/healthz` answers `Healthy` once a token exists.

## `401`, empty collections, or "skipped: the token lacks the '...' scope"

- A missing scope is a `401` from Oura, not an empty array. Re-check `Oura__Scopes` against the default in `.env.example`, run `docker compose up -d`, authorize again at `/oauth/start`, then `docker compose restart ingest`.
- If every collection fails, check that your Oura Membership is active. Several sources report that Gen3 and newer rings need one for API access; we have not confirmed this on Oura's developer pages.
- Personal Access Tokens no longer work; use the OAuth flow.

## Ingest exits with "Oura authorization is unrecoverable"

The refresh token rotated at Oura but did not persist. Visit `/oauth/start` again; `oura_raw` is untouched. Never run two ingests against one Oura app.

## Grafana panels are empty

The backfill runs newest-first, but nightly data lands mid-morning, and a brand-new ring has few days. Check progress:

```bash
docker compose exec timescaledb psql -U oura -d oura -c "select doc_type, count(*) from ingest_window group by 1;"
```

With the bundled Grafana, confirm the datasource under Connections, and run `GRAFANA_ADMIN_PASSWORD=... ./grafana/verify-dashboards.py`. The Postgres password in `.env` must be the one the database volume was first created with; if you changed it later, either change it back or `docker compose down -v` (this deletes the warehouse).

## Grafana does not start, or you cannot log in

Grafana is behind a profile: `docker compose --profile grafana up -d`. With an empty `GRAFANA_ADMIN_PASSWORD` it uses `admin`/`admin` and asks you to change it. The admin password only applies on first start; a later `.env` change does not alter the stored one (`docker compose down` then remove the `grafana-data` volume to reset).

## The calendar importer does nothing

Uncomment the `Context__Calendars__0__*` block in `.env`, then `docker compose up -d`. It runs once per ingest cycle, after an Oura token exists. `did not return parseable iCalendar data` means the URL is not a valid iCal feed (for example a revoked Google secret address).

## Ports already in use

Set `API_PORT`, `MCP_PORT`, `POSTGRES_PORT` or `GRAFANA_PORT` in `.env`. If you change `API_PORT`, also change `Oura__RedirectUri` and the URI on the Oura application.

## Still stuck

Open an issue with the relevant `docker compose logs` lines. Remove client secrets, tokens and calendar URLs first.
