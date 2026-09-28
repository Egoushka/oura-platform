-- TimescaleDB hypertables for everything sampled rather than summarised.
--
-- Timescale requires every unique index to include the partitioning column, so
-- these use (ts, ...) uniqueness rather than a surrogate key. That is also what
-- makes the writers idempotent: re-ingesting an overlapping window collapses
-- onto the same rows.

create table if not exists hr_samples (
    ts     timestamptz not null,
    bpm    smallint    not null,
    source text        not null,   -- awake | workout | rest | sleep | live | session
    unique (ts, source)
);

select create_hypertable('hr_samples', 'ts', if_not_exists => true);

-- Per-interval series carved out of the sleep document. metric in ('hrv','hr').
-- `night` is the sleep document's `day`, which is the morning the period ended --
-- it is what joins these rows back to `daily`.
create table if not exists sleep_series (
    ts     timestamptz not null,
    night  date        not null,
    metric text        not null,
    value  numeric,                -- null marks a gap; never synthesise a zero
    unique (ts, metric)
);

select create_hypertable('sleep_series', 'ts', if_not_exists => true);
create index if not exists sleep_series_night_metric_idx on sleep_series (night desc, metric);

-- Hypnogram, one row per 5-minute step. 1=deep 2=light 3=REM 4=awake.
create table if not exists hypnogram (
    ts    timestamptz not null,
    night date        not null,
    phase smallint    not null,
    unique (ts)
);

select create_hypertable('hypnogram', 'ts', if_not_exists => true);
create index if not exists hypnogram_night_idx on hypnogram (night desc);

create table if not exists ring_battery (
    ts    timestamptz not null,
    level smallint    not null,
    unique (ts)
);

select create_hypertable('ring_battery', 'ts', if_not_exists => true);
