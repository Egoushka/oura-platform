#!/usr/bin/env bash
# Dumps the warehouse for a move to another host.
#
# Only `oura_raw` and `ingest_window` are dumped. That is not a shortcut — oura_raw is the source
# of truth and every typed table is a projection of it, so the receiving side runs `--reproject`
# and derives the rest against whatever schema it is on. A full dump would instead carry
# projections built by an older version of the projector.
#
# `oauth_tokens` is deliberately NOT dumped. Oura's refresh token is single-use and rotates: two
# instances holding the same pair will race, one will spend it, and the other will hold a token
# Oura has already invalidated. The new host does its own handshake.
#
#   ./scripts/export-warehouse.sh                 # -> oura-warehouse-<date>.sql.gz
#   ./scripts/export-warehouse.sh /tmp/out.sql.gz
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

# Read the date from the database rather than the clock, so the filename matches the data.
OUT="${1:-oura-warehouse-$(docker compose exec -T timescaledb date -u +%Y%m%d).sql.gz}"
DB="${POSTGRES_DB:-oura}"
USER="${POSTGRES_USER:-oura}"

echo "Dumping oura_raw + ingest_window from the running stack..."

# Via `docker compose exec`, not a host psql: the host may have its own Postgres on 5432, which is
# how this was first written and why it failed with `role "oura" does not exist`.
docker compose exec -T timescaledb pg_dump \
    --username="$USER" \
    --dbname="$DB" \
    --data-only \
    --table=oura_raw \
    --table=ingest_window \
    | gzip > "$OUT"

rows=$(docker compose exec -T timescaledb psql -U "$USER" -d "$DB" -t -A -c 'select count(*) from oura_raw')

cat <<EOF

Wrote $OUT ($(du -h "$OUT" | cut -f1), $rows raw documents).

To restore on the target host:

  scp $OUT <vps>:/tmp/
  ssh <vps>

  # The stack must already be up once so the migrations have created the schema.
  cd <stack directory>
  docker compose up -d oura-timescaledb

  # Stop the ingest first. Two ingests against one Oura app race on the single-use
  # refresh token, and the loser is left holding an invalidated one.
  docker compose stop oura-ingest

  gunzip -c /tmp/$(basename "$OUT") \\
    | docker compose exec -T oura-timescaledb psql -U $USER -d $DB

  # Rebuild every typed table from the restored raw documents. Fetches nothing.
  docker compose run --rm oura-ingest --reproject

  docker compose up -d

Then stop the ingest on the old host for good — leaving both running is the one way
to lose the authorization.
EOF
