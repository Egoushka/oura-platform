---
title: "Status and evidence"
description: "What works, what is partial and what is not built yet, with the test, file or roadmap entry behind each, and where the repository disagrees with itself."
order: 6
section: "Project"
---

Each row names its evidence. `works` means a test in this repository covers it. `partial` means the code exists but part of it is untested or incomplete, and the section below says which part. `not yet` means it is not built.

The tests call neither Oura's production API nor the network. The Oura suite replays responses recorded from Oura's sandbox on 2026-08-01 and scripted HTTP responses ([Fixtures.cs](../../tests/OuraPlatform.Oura.Tests/Fixtures.cs), [StubHttp.cs](../../tests/OuraPlatform.Oura.Tests/StubHttp.cs)). The storage suite runs the real migrations against `timescale/timescaledb:2.17.2-pg17` in Testcontainers ([TimescaleFixture.cs](../../tests/OuraPlatform.Storage.Tests/TimescaleFixture.cs)).

| Capability | Status | Evidence |
|---|---|---|
| Token storage and single-use refresh | works | [OuraTokenStoreTests](../../tests/OuraPlatform.Storage.Tests/OuraTokenStoreTests.cs) |
| Re-consent pickup and scope `401`s | works | [OuraAuthenticationHandlerTests](../../tests/OuraPlatform.Oura.Tests/OuraAuthenticationHandlerTests.cs) |
| Authorization URL | works | [OuraOAuthClientTests](../../tests/OuraPlatform.Oura.Tests/OuraOAuthClientTests.cs) |
| Paging, query styles, API errors, windows | works | [OuraApiClientTests](../../tests/OuraPlatform.Oura.Tests/OuraApiClientTests.cs) |
| Models bind every sandbox field | works | [SandboxFixtureTests](../../tests/OuraPlatform.Oura.Tests/SandboxFixtureTests.cs), [ModelBindingTests](../../tests/OuraPlatform.Oura.Tests/ModelBindingTests.cs) |
| Migrations, hypertables and views | works | [MigrationTests](../../tests/OuraPlatform.Storage.Tests/MigrationTests.cs) |
| Raw storage and projections | works | [ProjectionTests](../../tests/OuraPlatform.Storage.Tests/ProjectionTests.cs) |
| Context upserts, pruning, `context_daily` | works | [ContextTests](../../tests/OuraPlatform.Storage.Tests/ContextTests.cs) |
| Analytics queries behind the MCP tools | works | [AnalyticsTests](../../tests/OuraPlatform.Storage.Tests/AnalyticsTests.cs) |
| Refresh before expiry and after a `401` | partial | [OuraTokenStore.cs](../../src/OuraPlatform.Storage/OuraTokenStore.cs) |
| Ingest loop: backfill, reconcile, retries | partial | [IngestWorker.cs](../../src/OuraPlatform.Ingest/IngestWorker.cs) |
| `--reproject` | partial | [ReprojectCommand.cs](../../src/OuraPlatform.Ingest/ReprojectCommand.cs) |
| OAuth endpoints and health checks | partial | [Program.cs](../../src/OuraPlatform.Api/Program.cs) |
| Retry pipeline and rate limits | partial | [OuraServiceCollectionExtensions.cs](../../src/OuraPlatform.Oura/OuraServiceCollectionExtensions.cs) |
| Calendar import | partial | [CalendarImporter.cs](../../src/OuraPlatform.Ingest/CalendarImporter.cs), [IcsParsingTests](../../tests/OuraPlatform.Oura.Tests/IcsParsingTests.cs) |
| Oura tags in `context` | partial | [OuraTagContextImporter.cs](../../src/OuraPlatform.Ingest/OuraTagContextImporter.cs) |
| MCP tool layer | partial | [OuraTools.cs](../../src/OuraPlatform.Mcp/OuraTools.cs) |
| Grafana dashboards | partial | [verify-dashboards.py](../../grafana/verify-dashboards.py) |
| Compose deployment and images | partial | [ci.yml](../../.github/workflows/ci.yml) |
| Warehouse export and restore | partial | [export-warehouse.sh](../../scripts/export-warehouse.sh) |
| Webhooks (Stage 3, skipped) | not yet | [Program.cs](../../src/OuraPlatform.Api/Program.cs) |
| Tables for the five raw-only collections | not yet | [DocumentProjector.cs](../../src/OuraPlatform.Storage/DocumentProjector.cs) |
| Home Assistant and environment sensors (Stage 5) | not yet | [CLAUDE.md](../../CLAUDE.md#roadmap) |
| Polar H10 RR/ECG ingest (Stage 6) | not yet | [CLAUDE.md](../../CLAUDE.md#roadmap) |
| Local BLE with `open_oura` (Stage 7) | not yet | [CLAUDE.md](../../CLAUDE.md#roadmap) |
| Body metrics and CGM (Stage 8) | not yet | [CLAUDE.md](../../CLAUDE.md#roadmap) |

## Works

- **Token storage and single-use refresh.** `Concurrent_refreshes_spend_the_refresh_token_exactly_once` runs two store instances against one database and sees one exchange. Also `Refresh_persists_both_halves_of_the_new_pair`, `A_failed_write_latches_the_store_instead_of_continuing`, `A_failed_write_leaves_the_stored_pair_untouched`, `Expiry_comes_from_expires_in_not_from_a_constant` and `Tokens_never_appear_in_a_string_representation`.
- **Re-consent pickup and scope `401`s.** `A_401_re_reads_the_store_so_a_re_consent_takes_effect_without_a_restart`, `A_scope_401_never_refreshes_the_token` and `The_bearer_token_is_attached`.
- **Authorization URL.** The scopes are escaped, the code-flow parameters and the redirect URI survive intact, and the default scopes hold `spo2`, `stress` and `heart_health` but neither `email` nor `spo2Daily`.
- **Paging, query styles, API errors, windows.** The client follows `next_token`, sends dates or datetimes by collection, sends no filter for `ring_configuration`, reads `personal_info` without an envelope, gives series rows a stable id, keeps the payload verbatim, names a missing scope from the `401` body and reports a `403` as a possibly lapsed subscription. The `OuraWindowsTests` class in the same file checks that windows are contiguous, one month or 7 days long, and single for undated collections.
- **Models bind every sandbox field.** Every recorded collection deserializes, and `Model_binds_every_field_the_sandbox_returns` fails when a fixture holds a field no model binds. `ModelBindingTests` covers the digit-boundary names such as `sleep_phase_5_min`, nulls kept in sample arrays, local UTC offsets, and the absence of the `meta` envelope removed in spec 1.34.
- **Migrations, hypertables and views.** All twelve tables exist, the four series tables are hypertables, the three rollup views in `0003` answer a query, and running the migrations twice succeeds. `ContextTests` queries `context_daily`.
- **Raw storage and projections.** A night fans out into `sleep_sessions`, `sleep_series` and `hypnogram`; gaps stay `NULL`; the interval comes from the payload; projecting twice converges; overlapping periods and duplicate documents in one batch upsert cleanly; a zero SpO2 average becomes `NULL`; naps leave the night's summary alone; an unreadable document is skipped; and the tables rebuild from `oura_raw` alone.
- **Context upserts, pruning, `context_daily`.** Re-importing a feed converges, an edited event updates in place, an event that left the feed is pruned without touching other sources, and `last_end_hour` is the local hour, 20 for an event ending 17:30 UTC in August.
- **Analytics queries behind the MCP tools.** Every query reads back into its record, which is where a Dapper column mismatch would throw. The tests also pin the period bounds, the lag shift in `correlate`, events counted rather than days, two-sided outliers, the weekly comparison, hour bands for a night, coverage, and the rejection of a metric name that is not in the list.

## Partial

**Refresh before expiry and after a `401`.** The handler asks the store for a refresh from `Oura:RefreshSkew` before expiry, and once after an ordinary `401`. `OuraTokenStore.RefreshAsync` spends the refresh token only when the stored pair has expired, and treats an unexpired pair as one another caller already rotated (`Refresh_is_skipped_when_another_caller_already_rotated`). Together they rotate the pair only after it expires, and a `401` on an unexpired token is retried with the same token and then fails. `An_ordinary_401_refreshes_once_and_retries` passes because its fake store always rotates; no test runs the handler against the real store ([OuraAuthenticationHandler.cs](../../src/OuraPlatform.Oura/Auth/OuraAuthenticationHandler.cs)).

**Ingest loop.** `OuraPlatform.Ingest` has no test project, so `IngestWorker`, `BackfillJob`, `ReconcileJob` and `IngestPipeline` are untested. The client, windows, repositories and projector underneath them are tested.

**`--reproject`.** `ReprojectCommand` has no test. `Projections_can_be_rebuilt_from_raw_alone` covers the projector half, after emptying the tables itself; the command upserts over existing rows and empties nothing.

**OAuth endpoints and health checks.** `/oauth/start`, `/oauth/callback`, the in-memory `state` store and both `/healthz` checks have no tests ([OAuthEndpoints.cs](../../src/OuraPlatform.Api/OAuthEndpoints.cs), [DatabaseHealthCheck.cs](../../src/OuraPlatform.Api/DatabaseHealthCheck.cs)). The callback does not catch a failed code exchange.

**Retry pipeline and rate limits.** The retry count, backoff and `Retry-After` handling have no test. `Rate_limit_details_survive_into_the_exception` exercises the client without the pipeline.

**Calendar import.** `CalendarImporter` has no test. `IcsParsingTests` pins the Ical.Net behaviour it relies on (recurrences sharing one UID, `DURATION` without `DTEND`, all-day detection, occurrences that are ordered and bounded only by the caller), and `ContextTests` covers the repository. Compose does not pass the `Context__*` settings, `Context:Interval` and `Context:TimeZone` are read by nothing, and `context_daily` fixes the time zone to `Europe/Kyiv` ([Add a calendar feed](operations.md#add-a-calendar-feed)).

**Oura tags in `context`.** `OuraTagContextImporter` and its kind normalisation have no test, and neither does the `enhanced_tag` projection into `tags`.

**MCP tool layer.** `OuraTools` has no test: date parsing, the baseline rules, `direction`, the 14-day note, the clamp on `count` and the error mapping. No test starts the MCP server. `AnalyticsTests` covers the queries underneath.

**Grafana dashboards.** `verify-dashboards.py` needs a running Grafana, and CI does not run it. The two context panels join on the same day, with no lag ([Install the Grafana dashboards](operations.md#install-the-grafana-dashboards)).

**Compose deployment and images.** CI restores, builds and tests the solution, and scans for secrets; it never builds the Dockerfile or starts the stack. `scripts/publish.sh` runs by hand.

**Warehouse export and restore.** The script has no test, and the restore commands it prints name services that this repository's `docker-compose.yml` calls something else ([Move to another host](operations.md#move-to-another-host)).

## Not yet

- **Webhooks, Stage 3, are skipped deliberately.** The README calls them a latency optimisation on a pipeline that already works, and CLAUDE.md adds that they would need a public HTTPS endpoint. `/webhooks/oura` answers `501`. The sandbox's single-document endpoints are broken, so the fetch by id that webhook events need cannot be tested against it ([Oura API notes](../oura-api-notes.md#sandbox-traps--do-not-calibrate-anything-on-sandbox-data)).
- **Tables for `session`, `sleep_time`, `rest_mode_period`, `ring_configuration` and `personal_info`.** They are stored in `oura_raw` with no projection. The projector's comment says a projection gets written when a dashboard needs one, and replayed from raw (`Collections_without_a_projection_are_stored_but_not_projected`).
- **Stages 5 to 8.** Home Assistant and environment sensors, Polar H10 RR/ECG ingest, local BLE with `open_oura` (marked experimental), and body metrics and CGM. The roadmap lists them; nothing in `src/` implements any of them.
- **`meal`.** Oura's webhook enum lists it, but it has no REST endpoint, so nothing polls it ([Oura API notes](../oura-api-notes.md#meal)).

## Where the repository disagrees with itself

Where these conflict, the docs on this site follow the code.

- **`Oura__Scopes`.** `OuraOptions` and `.env.example` hold the working set; the fallback in `docker-compose.yml` is the spec's list with `spo2Daily`. The README's "The default is the set Oura's API actually enforces" holds only while `.env` keeps the line.
- **`Context__*` in `.env.example`.** Its header says Compose passes these variables "straight through to both containers"; `docker-compose.yml` passes none of them.
- **`Context:Interval` and `Context:TimeZone`.** Declared in `ContextOptions` and set in `.env.example`, read by nothing. The import runs every `Ingest:ReconcileInterval`, and `context_daily` fixes `Europe/Kyiv`.
- **How many processes.** The README says "Two processes, one database" and the Dockerfile says "One Dockerfile, two images". Compose runs three .NET services, and `publish.sh` builds three images.
- **The MCP container.** The README says it "needs only the connection string"; Compose hands it the whole `x-dotnet-env` block, Oura client id and secret included, which the process does not read. The README also says it "cannot write": its tools only read, but it connects as the same database role as the ingest.
- **A missing scope.** The API notes, `OuraApiClient` and its tests say Oura answers with a `401`. The page `/oauth/callback` renders, and comments in `BackfillJob` and `IngestWindowRepository`, say Oura answers with empty arrays.
- **Refresh ahead of expiry.** `Oura:RefreshSkew` is documented in `OuraOptions` as refreshing "this far ahead of `expires_at`"; the store refreshes only an expired pair (see [Partial](#partial)).
- **`TZ`.** `.env.example` says it affects the worker's idea of "today"; the backfill and the reconcile job use UTC. Only the MCP `weekly_digest` default follows `TZ`.
- **`--reproject`.** The README says it "rebuilds the typed tables"; it upserts over them without emptying them.
- **`publish.sh`.** The README says it "runs the tests before pushing"; it runs `tests/OuraPlatform.Oura.Tests` only.
- **`export-warehouse.sh`.** Its restore instructions name the services `oura-timescaledb` and `oura-ingest`; `docker-compose.yml` names them `timescaledb` and `ingest`.
- **ADR 0001.** Its context lists continuous aggregates and compression policies among the schema work; the migrations create neither, and `0003` explains why the rollups are views.
- **The Distributions dashboard.** Its description says `context` is "empty until Stage 4 fills it"; the importers already fill it, and Stage 4 is the MCP server.
- **`GRAFANA_ADMIN_PASSWORD`.** The README's setup asks you to fill it in; the stack runs no Grafana, and only `verify-dashboards.py` reads it.

## Run the tests

With the .NET 10 SDK, from a clone:

```bash
dotnet test tests/OuraPlatform.Oura.Tests       # no credentials, no network, no Docker
dotnet test tests/OuraPlatform.Storage.Tests    # needs a Docker daemon (Testcontainers)
```

Warnings are errors in every project ([Directory.Build.props](../../Directory.Build.props)). CI runs both suites on every pull request and every push to `master`, and a second job runs gitleaks and checks that every commit uses a GitHub noreply address ([ci.yml](../../.github/workflows/ci.yml)).

To re-record the sandbox fixtures, run `./tests/record-sandbox-fixtures.sh`, then the Oura suite. `Model_binds_every_field_the_sandbox_returns` fails for each field Oura has added until a model binds it or `IgnoredFields` in [SandboxFixtureTests.cs](../../tests/OuraPlatform.Oura.Tests/SandboxFixtureTests.cs) lists it. Calibrate nothing on sandbox data: it differs from production in at least four ways ([Oura API notes](../oura-api-notes.md#sandbox-traps--do-not-calibrate-anything-on-sandbox-data)).

Before you commit, enable the hooks with `git config core.hooksPath .githooks` and copy `.private-terms.example` to `.private-terms`, listing what must never appear in the repository. The hooks check staged files and commit messages against that list, run gitleaks, and require a noreply address; `.githooks/selftest.sh` checks them. Commit messages follow Conventional Commits ([README](../../README.md#development), [CLAUDE.md](../../CLAUDE.md#conventions)).
