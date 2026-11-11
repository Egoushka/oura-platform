#!/usr/bin/env bash
# One-shot setup: asks for your Oura app's client id and secret, generates the database and Grafana
# passwords, writes .env, starts the stack and prints the redirect URI to register at Oura.
#
#   ./setup.sh                 # interactive
#   OURA_CLIENT_ID=... OURA_CLIENT_SECRET=... ./setup.sh --yes --grafana     # no prompts
#
# Flags: --grafana  also start the bundled Grafana   --build  build images from source
#        --yes      never prompt (needs the two OURA_* variables)   --no-start  write .env only
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

GRAFANA="" BUILD="" YES="" START=1
for arg in "$@"; do
  case "$arg" in
    --grafana) GRAFANA=1 ;;
    --build) BUILD=1 ;;
    --yes|-y) YES=1 ;;
    --no-start) START="" ;;
    -h|--help) sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown option: $arg (try --help)" >&2; exit 2 ;;
  esac
done

die() { echo "error: $*" >&2; exit 1; }

if [[ -f .env ]]; then
  die ".env already exists. Move it aside (mv .env .env.bak) to run setup again; it will not be overwritten."
fi
[[ -f .env.example ]] || die ".env.example not found: run this from the repository root."

# Fail before asking anything if the stack cannot start.
if [[ -n "$START" ]]; then
  command -v docker >/dev/null || die "docker not found. Install Docker with the Compose plugin, or use --no-start."
  docker compose version >/dev/null 2>&1 || die "'docker compose' not available. Install the Compose plugin."
fi

ask() { # ask VAR "prompt" [default] [secret]
  local var="$1" prompt="$2" default="${3:-}" secret="${4:-}" reply=""
  if [[ -n "${!var:-}" ]]; then return; fi
  if [[ -n "$YES" ]]; then
    [[ -n "$default" ]] || die "$var is required with --yes"
    printf -v "$var" '%s' "$default"; return
  fi
  if [[ -n "$secret" ]]; then
    read -r -s -p "$prompt: " reply; echo
  else
    read -r -p "$prompt${default:+ [$default]}: " reply
  fi
  printf -v "$var" '%s' "${reply:-$default}"
}

gen() { # 32 hex chars, no shell-special characters
  if command -v openssl >/dev/null; then openssl rand -hex 16
  else LC_ALL=C tr -dc 'a-f0-9' </dev/urandom | head -c 32; echo; fi
}

cat <<'MSG'
Before you start, register an app at https://developer.ouraring.com (My Applications -> New).
The redirect URI to give it is http://localhost:8080/oauth/callback (the port below changes it;
the exact URI is printed again at the end).
Personal access tokens no longer work: an OAuth app is required.

MSG

ask OURA_CLIENT_ID "Oura client id"
ask OURA_CLIENT_SECRET "Oura client secret (hidden)" "" secret
ask API_PORT_IN "API port for the OAuth callback" "8080"
ask BACKFILL_FROM "Day you got the ring (YYYY-MM-DD, bounds the backfill)" "2020-01-01"
ask TZ_IN "Timezone (IANA name)" "${TZ:-UTC}"

for v in OURA_CLIENT_ID OURA_CLIENT_SECRET; do
  [[ -n "${!v}" ]] || die "$v is empty"
  [[ "${!v}" =~ ^[A-Za-z0-9._~+/=-]+$ ]] || die "$v has characters this script will not write into .env unquoted; edit .env by hand."
done
[[ "$API_PORT_IN" =~ ^[0-9]+$ ]] || die "port must be a number"
[[ "$BACKFILL_FROM" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}$ ]] || die "date must be YYYY-MM-DD"
[[ "$TZ_IN" =~ ^[A-Za-z0-9_/+-]+$ ]] || die "timezone must be an IANA name such as Europe/Berlin"

REDIRECT="http://localhost:${API_PORT_IN}/oauth/callback"
PG_PASSWORD="$(gen)"
GF_PASSWORD="$(gen)"

# Start from .env.example so every comment survives, then set the values.
umask 077
cp .env.example .env
setvar() { # replace the first uncommented KEY= line
  local key="$1" value="$2"
  grep -q "^${key}=" .env || die "${key} missing from .env.example"
  awk -v k="$key" -v v="$value" 'BEGIN{done=0} !done && index($0,k"=")==1 {print k"="v; done=1; next} {print}' .env > .env.tmp
  mv .env.tmp .env
}
setvar Oura__ClientId "$OURA_CLIENT_ID"
setvar Oura__ClientSecret "$OURA_CLIENT_SECRET"
setvar Oura__RedirectUri "$REDIRECT"
setvar Oura__BackfillFrom "$BACKFILL_FROM"
setvar POSTGRES_PASSWORD "$PG_PASSWORD"
setvar GRAFANA_ADMIN_PASSWORD "$GF_PASSWORD"
setvar API_PORT "$API_PORT_IN"
setvar TZ "$TZ_IN"
echo "Wrote .env (mode 600). Passwords are generated; Grafana admin password is in it too."

if [[ -n "$START" ]]; then
  files=(-f docker-compose.yml)
  [[ -n "$BUILD" ]] && files+=(-f compose.build.yml)
  profile=()
  [[ -n "$GRAFANA" ]] && profile=(--profile grafana)
  up=(up -d); [[ -n "$BUILD" ]] && up+=(--build)
  echo "Starting the stack..."
  docker compose "${files[@]}" "${profile[@]}" "${up[@]}"
fi

cat <<MSG

Done. Two steps left:

 1. Register this exact redirect URI on your Oura app (My Applications -> edit):

      ${REDIRECT}

 2. Open http://localhost:${API_PORT_IN}/oauth/start and approve at Oura. Ingest starts within 30 seconds.
    Watch it:  docker compose logs -f ingest

MCP endpoint:  http://localhost:8081/mcp
MSG
if [[ -n "$GRAFANA" ]]; then
  echo "Grafana:       http://localhost:3000   user admin, password: grep GRAFANA_ADMIN_PASSWORD .env"
else
  echo "Grafana (optional):  docker compose --profile grafana up -d"
fi
