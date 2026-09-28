# Oura API notes

Verified against the live sandbox and the 1.37 OpenAPI spec on **2026-08-01**. Where this file
and your memory of the Oura API disagree, this file wins. Where this file and the machine-readable
spec disagree, the observation recorded here wins — every "sandbox says" line below was executed,
not read.

There is **no official changelog**. The only way to find version deltas is to diff spec snapshots,
so keep a local copy of each version you build against. It is not committed here: it is Oura's document.

Spec URL: `https://api.ouraring.com/v2/static/json/openapi-1.37.json`
(the ReDoc page at `/v2/docs` is JS-rendered and useless to `curl`).

---

## Authentication

**OAuth2 authorization-code flow only.** Personal Access Tokens were deprecated in December 2025
and no longer work. There is no PAT code path in this repo, not even as a fallback.

| | |
|---|---|
| Register | `https://developer.ouraring.com` (also live: `https://cloud.ouraring.com/oauth/applications`; unclear which is canonical) |
| Authorize | `https://cloud.ouraring.com/oauth/authorize` |
| Token | `https://api.ouraring.com/oauth/token` |
| Auth code lifetime | 10 minutes |
| Redirect URIs | exact-match allowlist, multiple allowed, `http://localhost:PORT/callback` works |
| Unapproved app cap | 10 users |

### The refresh token rotates and is single-use

Every refresh response contains a **new** `refresh_token`. The old one is dead the moment the
response is issued. Failing to persist the new one atomically means redoing the browser handshake
by hand.

This is why `OuraPlatform.Storage` refreshes inside a single transaction opened with
`SELECT ... FROM oauth_tokens WHERE provider = 'oura' FOR UPDATE`, writes both tokens before
committing, and crashes the process rather than continuing on a token it did not persist.

### Access-token lifetime

**Read `expires_in` from the response.** Oura's own documentation states 24 hours in one place and
30 days in another. Nothing in this repo hardcodes it.

### Scopes — the spec's list is wrong, verified against a live token

The spec's OAuth2 flow declares:

```
email  personal  daily  heartrate  workout  tag  session  spo2Daily
```

**Do not use that list.** Consenting with it produces a token that 401s on four collections. What
Oura's API actually enforces, read off its own 401 bodies on 2026-08-02:

| Collection | Scope demanded | In the spec's list? |
|---|---|---|
| `daily_spo2` | `spo2` | no — spec says `spo2Daily` |
| `daily_resilience` | `stress` | **no** |
| `daily_cardiovascular_age` | `heart_health` | **no** |
| `vO2_max` | `heart_health` | **no** |
| `ring_battery_level` | `ring_configuration` | **no** |
| `ring_configuration` | `ring_configuration` | **no** |

Working set:

```
personal  daily  heartrate  workout  tag  session
spo2  stress  heart_health  ring_configuration
```

Four of the ten are absent from or wrong in the spec. Assume the list is incomplete rather than
authoritative: the only reliable way to find a scope name is to fetch the collection and read the
401.

Two things make this expensive to discover:

1. **The authorize endpoint silently drops scope names it does not recognise.** Consenting with
   `spo2Daily` succeeds, the callback renders a success page, and the granted scope simply is not
   there. Nothing fails until a collection is fetched.
2. **The granted scopes come back prefixed.** A token consented for the working set above reports
   its scope as `extapi:personal extapi:daily extapi:heartrate …`. Request them unprefixed; the
   prefix is Oura's internal naming and must not be sent.

### A missing scope is a 401, not an empty array

The widely repeated claim that missing scopes return empty arrays is **wrong for the daily
collections**. Oura answers:

```
401 {"detail":"Token is not authorized access spo2 scope."}
```

This matters beyond the error message. A 401 normally means "refresh the token", and a scope 401
never resolves — refreshing once per window would spend 80 token rotations per collection for
nothing. `OuraAuthenticationHandler` therefore parses the body and skips the refresh, and
`OuraApiException.MissingScope` carries the scope name up to the backfill, which logs a one-line
instruction instead of a stack trace.

### Re-consenting does not reach a running process by itself

`/oauth/start` writes the new token from the **API** process; the ingest process is holding the old
one in memory and — since `expires_in` came back as 30 days on a live account — would keep
presenting it long after the user has fixed the problem. A 401 therefore always re-reads
`oauth_tokens` before concluding anything, and retries immediately if the stored access token is no
longer the one that just failed. Without that, adding a scope appears to do nothing until someone
restarts the container.

Empty arrays are still worth watching for — `daily_stress` and `sleep_time` returned them for a
scoped, authorized token — but an empty array means "no data", not "no permission".

---

## Rate limits

Two-tier since spec 1.34 — **per access token** and **per application** — both surfacing as `429`.

Response headers: `X-RateLimit-Tier` (which tier fired), `Retry-After`, `X-RateLimit-Limit`,
`X-RateLimit-Window`, `X-RateLimit-Reset`.

The widely-quoted **"5000 requests per 5-minute period" is stale**; it survives only in the FAQ. The
client implements header-driven backoff (`Retry-After` first, exponential fallback), never a
hardcoded budget.

Observed: successful sandbox responses carry **no** `X-RateLimit-*` headers at all, so the client
must treat their absence as "no information", not as "no limit".

---

## Sandbox

```
https://api.ouraring.com/v2/sandbox/usercollection/{collection}
```

Verified behaviour:

| Request | Result |
|---|---|
| No `Authorization` header | `400 {"detail":"Missing auth token. Include any string in 'Authorization' header."}` |
| `Authorization: Bearer test` | `200` |
| `Authorization: literally-anything` | `200` — any string is accepted |
| `/sandbox/usercollection/personal_info` | `404 {"detail":"Not Found"}` |
| `start_date=nonsense` | `422`, FastAPI-style `{"detail":[{loc,msg,type,...}]}` |

### Sandbox traps — do not calibrate anything on sandbox data

1. **`interval: 60` where production returns `300`.** Sandbox `sleep` returns exactly 10 fake items
   per sample; production spans the whole night. Read `interval` from the payload; never assume.
2. **`producer_timestamp` where the spec declares `timestamp_unix`.** Sandbox `heartrate` and
   `ring_battery_level` rows carry a always-null `producer_timestamp` and no `timestamp_unix` at
   all — even though the spec marks `timestamp_unix` *required*. The models bind `timestamp_unix`
   as optional and ignore `producer_timestamp`; `timestamp` is the field that is actually always
   present.
3. **`personal_info` 404s.** It is the only collection with no sandbox coverage.
4. **Single-document endpoints are broken.** `GET /sandbox/usercollection/{collection}/{id}` returns
   `500 Internal Server Error` for the sandbox's *own* document ids
   (`daily_sleep-0-2025-6-1`, …), and `404 {"detail":"Invalid sandbox document ID format."}` for
   anything else. Verified across `sleep`, `daily_sleep`, `daily_activity`, `workout` and
   `enhanced_tag`. There is no way to exercise document-by-id fetch against the sandbox, which
   matters for Stage 3 — webhook events are thin notifications that must be resolved by
   `object_id`.
5. **`next_token` is always null.** Sandbox never pages, so paging cannot be fixture-tested; it is
   covered by a stubbed `HttpMessageHandler` instead.
6. **`ring_configuration` ignores date filters** and returns exactly one document.
7. **Embedded sample timestamps are hardcoded to `2021-01-01T00:00:00.000+00:00`** and do not track
   the document they are attached to. Backfilling three months of sandbox `sleep` yields 91
   documents with 91 distinct `bedtime_start` values and exactly **one** distinct
   `hrv.timestamp`:

   ```
   distinct_sample_ts | sleep_docs |           sample_ts           |            bedtime
   --------------------+------------+-------------------------------+-------------------------------
                     1 |         91 | 2021-01-01T00:00:00.000+00:00 | 2026-05-01T00:00:00.000+00:00
   ```

   So a sandbox backfill produces ~20 `sleep_series` rows rather than ~18,000: every night's
   samples land on the same instants and collapse onto the same primary key. This is the ingest
   behaving correctly on fabricated input, not data loss. `daily_cardiovascular_age` and `vO2_max`
   do the same thing with their `day` field, which is why a sandbox run writes those columns to a
   `daily` row dated 2021-01-01.
8. **A batch can contain duplicate keys.** Two documents whose samples cover the same instants —
   which the hardcoded timestamps above guarantee, and which overlapping sleep periods produce in
   production — make Postgres raise `21000: ON CONFLICT DO UPDATE command cannot affect row a
   second time`. `BulkUpsert` deduplicates with `DISTINCT ON` before the upsert for exactly this
   reason.

---

## Endpoints

Base: `https://api.ouraring.com/v2/usercollection/`.

**Daily documents** — `start_date` / `end_date`, one document per day:
`daily_activity`, `daily_readiness`, `daily_sleep`, `daily_spo2`, `daily_stress`,
`daily_resilience`, `daily_cardiovascular_age`, `vO2_max`, and — despite the name — the
event-shaped `sleep`, `sleep_time`, `session`, `workout`, `enhanced_tag`, `rest_mode_period`,
`tag`.

**Time series** — `start_datetime` / `end_datetime`, plus `latest` and `next_token` paging:
`heartrate`, `ring_battery_level`.

**Neither** — `ring_configuration` accepts only `fields` and `next_token`; `personal_info` accepts
nothing and returns a bare document with no `data` envelope.

Every collection except the two time-series ones also exposes `GET /{collection}/{document_id}`.

`tag` is deprecated in favour of `enhanced_tag`. It is modelled and fixture-tested here for
completeness but excluded from `OuraCollections.Ingested`.

### `vO2_max` — capital O

The REST path is `vO2_max`; the webhook `data_type` enum spells it `vo2_max`. The inconsistency is
real. `OuraCollections` stores both: `Name` uses the webhook spelling (and is what lands in
`oura_raw.doc_type`), `Path` uses the REST spelling.

### `meal`

Present in the webhook `data_type` enum, **no REST endpoint**. Likely upcoming. Nothing polls it.

---

## The `sleep` document

The single most valuable object in the API. Per night it carries:

- **`heart_rate` and `hrv`**, each a `PublicSample` = `{ interval, items[], timestamp }`. In
  production `interval = 300` (5 min), `timestamp` is `bedtime_start` with the user's local UTC
  offset, and `items` spans the whole night with **`null` for gaps**. This is per-interval HRV
  across the night, not one nightly average — the reason this project exists.
- **`sleep_phase_5_min`** and **`app_sleep_phase_5_min`** — hypnogram strings, one digit per
  5 minutes: `1`=deep `2`=light `3`=REM `4`=awake.
- **`sleep_phase_30_sec`**, **`movement_30_sec`** — same idea at 30-second resolution.
- **`average_hrv`**, **`lowest_heart_rate`**.
- **`sleep_algorithm_version`**, **`sleep_analysis_reason`** (Ring 5 background sleep detection),
  **`ring_id`**.

**`lowest_heart_rate` will not equal `min()` of the 5-minute array.** Oura computes it on
30-second samples. This is expected. Do not "fix" it.

---

## `heartrate`

Returns **discrete rows**, not an interval/items array:

```json
{ "timestamp": "2025-06-01T22:15:00.000Z", "timestamp_unix": 1748…, "bpm": 61, "source": "sleep" }
```

`source` ∈ `awake | workout | rest | sleep | live | session`.

The docs claim "5-minute increments", but daytime coverage is sparse and irregular — the ring
measures opportunistically. **Gaps are not data loss and must never be backfilled.**

---

## Breaking changes not to trip over

- **Spec 1.34 removed the `meta` envelope** from every document schema. Any tutorial or library
  written before mid-2026 unwraps `data.meta` and finds nothing. Asserted by
  `Documents_have_no_meta_envelope`.
- **`interbeat_interval`** was added in 1.29 and **removed again in 1.34**. Do not plan around it.

---

## Data-availability semantics

- Nightly data lands **mid-morning the following day**, not in real time. There is no live path
  through this API — which is why Stage 3 treats webhooks as a latency optimisation on top of
  polling, never a replacement.
- Query date params are interpreted in the **user's local timezone**, not UTC.
- Non-wear days produce **missing records**, not zero-valued ones.
- Oura **amends recent days** after the fact, which is why the reconcile job re-fetches a trailing
  window rather than only new days.

---

## Modelling decisions that follow from the above

- **String enums are modelled as `string`.** Oura grows enum members between spec revisions
  (`PublicRingColor` alone lists 16). A closed C# enum throws on the first unseen value and aborts
  the whole page; a string cannot.
- **`JsonNamingPolicy.SnakeCaseLower` mishandles digit boundaries.** It renders `SleepPhase5Min` as
  `sleep_phase5_min`. Every digit-containing property carries an explicit `[JsonPropertyName]`.
  Without it the hypnogram binds to `null` and disappears **silently** — the exact failure mode
  `Digit_boundary_properties_bind_despite_the_naming_policy` exists to catch.
- **Unknown JSON members are ignored on read**, because the verbatim payload is stored in
  `oura_raw` regardless. `Model_binds_every_field_the_sandbox_returns` fails if a re-recorded
  fixture contains a field no model binds, so "ignored" never becomes "unnoticed".
- **Every timestamp binds to `DateTimeOffset`**, never `DateTime`. Oura sends the user's local
  offset (`+02:00`) and it is load-bearing: a sleep period's `bedtime_start` offset is what makes
  "when in the night" comparable across daylight-saving boundaries.
