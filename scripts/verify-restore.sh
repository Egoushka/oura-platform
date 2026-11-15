#!/usr/bin/env bash
# Proves a warehouse dump (scripts/export-warehouse.sh) comes back, in a throwaway container.
#
# Starts a scratch TimescaleDB with no published ports and no volume, applies db/migrations, loads
# the dump, and checks two bounds on oura_raw:
#   freshness  the newest daily_* document is at most 36 hours old (nightly data lands mid-morning,
#              so 36h is one missed night plus slack)
#   monotone   the raw document count is not below the count of the previous check (--prev-count)
# Never touches the live warehouse. Last line: "OK <n> raw documents, newest day <date>" or exit 1.
#
#   ./scripts/verify-restore.sh oura-warehouse-20261115.sql.gz
#   ./scripts/verify-restore.sh dump.sql.gz --prev-count 41873
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DUMP="${1:?usage: verify-restore.sh <dump.sql.gz> [--prev-count N]}"
shift
PREV=0
if [[ "${1:-}" == "--prev-count" ]]; then PREV="${2:?--prev-count needs a number}"; fi
[[ -r "$DUMP" ]] || { echo "FAIL cannot read $DUMP" >&2; exit 1; }

# Same image as docker-compose.yml, so the dump loads into the version it came from.
IMAGE="${OURA_RESTORE_IMAGE:-$(sed -n 's/^ *image: *\(timescale\/timescaledb:[^ ]*\).*/\1/p' "$REPO_ROOT/docker-compose.yml" | head -1)}"
[[ -n "$IMAGE" ]] || { echo "FAIL no timescaledb image found in docker-compose.yml" >&2; exit 1; }

NAME="oura-restore-check-$$"
trap 'docker rm -fv "$NAME" >/dev/null 2>&1 || true' EXIT
psql_() { docker exec -i "$NAME" psql -q -v ON_ERROR_STOP=1 -U oura -d oura "$@"; }

docker run -d --name "$NAME" --network none \
    -e POSTGRES_PASSWORD=scratch -e POSTGRES_USER=oura -e POSTGRES_DB=oura \
    --tmpfs /var/lib/postgresql:rw "$IMAGE" >/dev/null

# The entrypoint starts a temporary server for init, then restarts: wait for the second one.
for _ in $(seq 1 60); do
    if docker exec "$NAME" pg_isready -U oura -d oura >/dev/null 2>&1 \
        && psql_ -Atc 'select 1' >/dev/null 2>&1 \
        && ! docker logs "$NAME" 2>&1 | tail -3 | grep -q 'init process complete' ; then
        break
    fi
    sleep 1
done
sleep 2
psql_ -Atc 'select 1' >/dev/null || { echo "FAIL scratch database did not start" >&2; exit 1; }

for m in "$REPO_ROOT"/db/migrations/*.sql; do
    psql_ <"$m" >/dev/null || { echo "FAIL migration $(basename "$m")" >&2; exit 1; }
done

gunzip -c "$DUMP" | psql_ >/dev/null || { echo "FAIL dump did not load" >&2; exit 1; }

read -r count newest age_h < <(psql_ -At -F ' ' -c "
    select count(*)::int,
           coalesce(max(day) filter (where doc_type like 'daily\_%')::text, 'none'),
           coalesce((extract(epoch from now() - max(day) filter (where doc_type like 'daily\_%')::timestamptz) / 3600)::int, -1)
    from oura_raw")

fail=0
if [[ "$count" -eq 0 ]]; then echo "FAIL oura_raw is empty after restore"; fail=1; fi
if [[ "$age_h" -lt 0 ]]; then echo "FAIL no daily_* document in the dump"; fail=1
elif [[ "$age_h" -gt 36 ]]; then echo "FAIL newest daily day $newest is ${age_h}h old (limit 36h)"; fail=1; fi
if [[ "$count" -lt "$PREV" ]]; then echo "FAIL raw count $count is below the previous check $PREV"; fail=1; fi

[[ "$fail" -eq 0 ]] || exit 1
echo "OK $count raw documents, newest day $newest"
