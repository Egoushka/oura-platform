-- Landing zone, OAuth token store, ingest bookkeeping and the narrow projections.
-- Hypertables live in 0002; they need the timescaledb extension created here first.

create extension if not exists timescaledb;

-- ---------------------------------------------------------------------------
-- oura_raw: the source of truth. Every table below is a projection of this and
-- must be rebuildable from it without re-calling the API.
-- ---------------------------------------------------------------------------
create table if not exists oura_raw (
    doc_type   text        not null,
    doc_id     text        not null,
    day        date,                      -- null for pure time-series rows
    payload    jsonb       not null,
    spec_ver   text        not null,      -- which spec revision this was parsed under
    fetched_at timestamptz not null default now(),
    primary key (doc_type, doc_id)
);

create index if not exists oura_raw_doc_type_day_idx on oura_raw (doc_type, day desc)
    where day is not null;
create index if not exists oura_raw_fetched_at_idx on oura_raw (fetched_at desc);

-- ---------------------------------------------------------------------------
-- OAuth. Single row, provider = 'oura'.
--
-- The refresh token is single-use and rotates on every refresh. Refresh runs
-- inside one transaction opened with SELECT ... FOR UPDATE so a concurrent
-- caller cannot spend the same refresh token twice.
-- ---------------------------------------------------------------------------
create table if not exists oauth_tokens (
    provider      text        primary key,
    access_token  text        not null,
    refresh_token text        not null,
    expires_at    timestamptz not null,
    scope         text        not null,
    rotated_at    timestamptz not null default now()
);

-- ---------------------------------------------------------------------------
-- Ingest bookkeeping. A window is recorded only after every document inside it
-- has been written, so a backfill killed mid-window redoes that window and
-- nothing else.
-- ---------------------------------------------------------------------------
create table if not exists ingest_window (
    doc_type       text        not null,
    window_start   date        not null,
    window_end     date        not null,
    document_count int         not null,
    completed_at   timestamptz not null default now(),
    primary key (doc_type, window_start)
);

-- ---------------------------------------------------------------------------
-- Projections
-- ---------------------------------------------------------------------------

-- One row per day, wide. Assembled from several daily_* collections, so every
-- writer upserts only its own columns.
create table if not exists daily (
    day                         date primary key,
    readiness_score             int,
    sleep_score                 int,
    activity_score              int,
    hrv_avg                     numeric,
    rhr_lowest                  numeric,
    temp_deviation              numeric,
    spo2_avg                    numeric,
    breathing_disturbance_index numeric,
    stress_high_sec             int,
    recovery_high_sec           int,
    resilience_level            text,
    vascular_age                numeric,
    pulse_wave_velocity         numeric,
    vo2_max                     numeric,
    steps                       int,
    active_calories             int,
    updated_at                  timestamptz not null default now()
);

create table if not exists sleep_sessions (
    id             text primary key,
    night          date,
    bedtime_start  timestamptz,
    bedtime_end    timestamptz,
    payload        jsonb not null
);

create index if not exists sleep_sessions_night_idx on sleep_sessions (night desc);

create table if not exists workouts (
    id        text primary key,
    start_ts  timestamptz,
    end_ts    timestamptz,
    activity  text,
    intensity text,
    payload   jsonb not null
);

create index if not exists workouts_start_ts_idx on workouts (start_ts desc);

create table if not exists tags (
    id       text primary key,
    ts       timestamptz,
    tag_type text,
    comment  text,
    payload  jsonb not null
);

create index if not exists tags_ts_idx on tags (ts desc);

-- ---------------------------------------------------------------------------
-- The reason this project exists: everything Oura will never see. Populated
-- from later stages (calendar, training, environment, labs).
-- ---------------------------------------------------------------------------
create table if not exists context (
    ts    timestamptz not null,
    kind  text        not null,   -- meeting | yoga | alcohol | caffeine | deploy | oncall | travel | ...
    label text,
    meta  jsonb
);

create index if not exists context_ts_idx on context (ts desc);
create index if not exists context_kind_ts_idx on context (kind, ts desc);
