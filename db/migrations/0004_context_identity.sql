-- `context` was declared as a bare (ts, kind, label, meta) log with no key. That is fine for rows
-- typed in by hand and wrong for anything imported on a schedule: re-reading a calendar feed would
-- append a second copy of every event, and the whole point of the table is to be joined against
-- `daily`, where a duplicate silently doubles a count.
--
-- Two additions, both load-bearing:
--
--   source / source_id  the natural key an importer can upsert on. `source_id` is whatever the
--                       upstream calls the thing (an iCalendar UID, an Oura document id), so
--                       re-running an import converges instead of accumulating.
--
--   ends_at             a meeting is an interval, not an instant. "Did the evening run late?" is a
--                       question about when something *finished* — a 18:00-20:30 call does not
--                       start after 19:00 but certainly ends after it.

alter table context add column if not exists source    text;
alter table context add column if not exists source_id text;
alter table context add column if not exists ends_at   timestamptz;

-- Anything already here predates importers and was entered by hand.
update context set source = 'manual' where source is null;
alter table context alter column source set not null;

-- Partial, so hand-entered rows without an upstream id are still allowed and simply never collide.
create unique index if not exists context_source_id_idx
    on context (source, source_id)
    where source_id is not null;

create index if not exists context_kind_ends_at_idx on context (kind, ends_at desc)
    where ends_at is not null;

comment on column context.source is
    'Which importer produced the row: calendar | oura_tag | manual.';
comment on column context.source_id is
    'Upstream identifier, unique within source. Null for hand-entered rows.';
comment on column context.ends_at is
    'End of the interval. Null for point-in-time events (a coffee, a deploy).';

-- Per-day rollup of the calendar, which is the shape every dashboard actually wants: how loaded was
-- the day, and how late did it run. Kept as a view because `context` is small and the definition is
-- likelier to change than the data.
create or replace view context_daily as
select
    (ts at time zone 'Europe/Kyiv')::date              as day,
    kind,
    count(*)                                           as events,
    sum(extract(epoch from (ends_at - ts)) / 3600.0)
        filter (where ends_at is not null)             as hours,
    min(ts)                                            as first_start,
    max(coalesce(ends_at, ts))                         as last_end,
    -- Local hour the day's last event finished. This is the "did the evening run late" number.
    max(extract(hour from (coalesce(ends_at, ts) at time zone 'Europe/Kyiv')))
                                                       as last_end_hour
from context
group by 1, 2;

comment on view context_daily is
    'Per-day, per-kind summary of context. `last_end_hour` is local time, which is the only way the '
    '"meeting after 19:00" question means anything.';
