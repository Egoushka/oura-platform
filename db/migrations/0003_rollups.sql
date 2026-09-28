-- Rollups over the hypertables.
--
-- These are plain views, not continuous aggregates. Two reasons, both specific
-- to this dataset:
--
--   1. A continuous aggregate must bucket on the partitioning column, and
--      time_bucket('1 day', ts) cuts a night in half at midnight. Grouping on
--      `night` is the only correct grouping for sleep, and a CAGG cannot do it
--      without a noon-anchored, timezone-and-DST-aware bucket that would still
--      be an approximation of the column already sitting in the table.
--   2. The volume does not justify materialising. A night is ~200 sleep_series
--      rows and a day is ~300 hr_samples rows -- roughly 100k rows a year in
--      total. A view over that answers in milliseconds and never needs a
--      refresh policy, a refresh lag window, or a backfill after a re-projection.
--
-- If sub-second dashboards over a decade of data ever become the constraint,
-- promote hr_daily to a continuous aggregate; sleep_nightly should stay a view.

create or replace view sleep_nightly as
select
    night,
    metric,
    count(value)                                            as samples,
    count(*) filter (where value is null)                   as gaps,
    avg(value)                                              as avg_value,
    min(value)                                              as min_value,
    max(value)                                              as max_value,
    percentile_cont(0.5) within group (order by value)      as median_value,
    stddev_samp(value)                                      as stddev_value,
    min(ts)                                                 as first_ts,
    max(ts)                                                 as last_ts
from sleep_series
group by night, metric;

comment on view sleep_nightly is
    'Per-night summary of the 5-minute HRV/HR arrays. Grouped on `night`, not on a time bucket, '
    'because a night spans midnight.';

create or replace view hr_daily as
select
    (ts at time zone 'UTC')::date as day,
    source,
    count(*)                      as samples,
    avg(bpm)                      as avg_bpm,
    min(bpm)                      as min_bpm,
    max(bpm)                      as max_bpm
from hr_samples
group by 1, source;

comment on view hr_daily is
    'Daily heart-rate summary split by source. Gaps are expected: the ring samples '
    'opportunistically and non-wear produces no rows at all.';

-- Hypnogram phase minutes per night, the shape Grafana actually wants.
create or replace view hypnogram_nightly as
select
    night,
    phase,
    count(*) * 5 as minutes
from hypnogram
group by night, phase;

comment on view hypnogram_nightly is
    'Minutes per sleep phase per night. phase: 1=deep 2=light 3=REM 4=awake.';
