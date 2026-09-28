#!/usr/bin/env bash
# Re-records the sandbox fixtures used by OuraPlatform.Oura.Tests.
# Needs no credentials: the sandbox accepts any Authorization header value.
#
# After running, `dotnet test` will fail on any collection that gained a field the models do not
# bind — see Model_binds_every_field_the_sandbox_returns. That failure is the point.
set -euo pipefail

SANDBOX="https://api.ouraring.com/v2/sandbox/usercollection"
OUT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/OuraPlatform.Oura.Tests/fixtures"

# Sandbox data is generated per request for whatever window is asked for; the window below just
# has to be wide enough to return a month of documents.
DATE_WINDOW="start_date=2025-06-01&end_date=2025-06-30"
DATETIME_WINDOW="start_datetime=2025-06-01T00:00:00%2B00:00&end_datetime=2025-06-03T00:00:00%2B00:00"

DATED=(
  daily_activity daily_cardiovascular_age daily_readiness daily_resilience daily_sleep
  daily_spo2 daily_stress enhanced_tag rest_mode_period session sleep sleep_time tag
  vO2_max workout
)
SERIES=(heartrate ring_battery_level)

mkdir -p "$OUT/sandbox" "$OUT/errors"

fetch() {
  local path="$1" query="$2" target="$3"
  local code
  code=$(curl -fsS -o "$target" -w '%{http_code}' -H 'Authorization: Bearer test' "$SANDBOX/$path${query:+?$query}")
  printf '%-28s %s %s bytes\n' "$path" "$code" "$(wc -c <"$target" | tr -d ' ')"
}

for collection in "${DATED[@]}"; do
  fetch "$collection" "$DATE_WINDOW" "$OUT/sandbox/$collection.json"
done
for collection in "${SERIES[@]}"; do
  fetch "$collection" "$DATETIME_WINDOW" "$OUT/sandbox/$collection.json"
done
# No date filter accepted on this one.
fetch ring_configuration '' "$OUT/sandbox/ring_configuration.json"

# Error shapes. Each of these is expected to be a non-2xx, so curl runs without -f.
curl -sS -o "$OUT/errors/missing_auth_400.json" "$SANDBOX/daily_sleep"
curl -sS -o "$OUT/errors/personal_info_404.json" -H 'Authorization: Bearer test' "$SANDBOX/personal_info"
curl -sS -o "$OUT/errors/validation_422.json" -H 'Authorization: Bearer test' "$SANDBOX/daily_sleep?start_date=nonsense"
curl -sS -o "$OUT/errors/single_document_404.json" -H 'Authorization: Bearer test' "$SANDBOX/daily_sleep/does-not-exist"
echo 'error fixtures recorded'
