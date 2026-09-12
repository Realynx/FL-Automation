#!/usr/bin/env bash
# =============================================================================
# FL Automate — one-click three-server installer.
#
# Builds the marketing-site and ai-gateway bundles locally, then provisions and
# deploys over SSH/scp (key auth) to three Ubuntu servers:
#
#   Server A (DB_SSH)    PostgreSQL 16 — fl_automate + fl_gateway databases
#   Server B (WEB_SSH)   marketing site  :3001  (systemd: fl-automate)
#   Server C (GW_SSH)    AI gateway      :3002  (systemd: ai-gateway)
#   Server D (ADMIN_SSH) Ops Console     :3005  (systemd: fl-console) VPN-ONLY
#
# Usage:
#   cp servers.env.example servers.env       # fill in, never commit
#   bash install-all.sh                      # full run: db + site + gateway + admin
#   bash install-all.sh --only site          # redeploy just the site
#   bash install-all.sh --only db,admin      # e.g. DB access rules + the console
#   bash install-all.sh --skip-build         # reuse the newest existing zips
#   bash install-all.sh --no-bump            # publish WITHOUT auto-incrementing VERSION
#   bash install-all.sh --config other.env
#
# Versioning: a site publish AUTO-INCREMENTS the repo-root VERSION file (the
# single product version: assembly stamps + installer artifact name), and the
# gateway step advertises the uploaded installer's version via
# /v1/client-version so older plugins show the in-chat update banner.
#
# Idempotent — safe to re-run for updates. Remote .env files and databases are
# never overwritten; only code, deps, and schema are refreshed. Run from Git
# Bash on Windows or any POSIX shell (needs: ssh, scp, node/npm).
#
# Cloudflare Tunnels are NOT set up here (cloudflared login is interactive);
# the script prints the pointers at the end. Everything else is hands-off.
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

CONFIG="$SCRIPT_DIR/servers.env"
SKIP_BUILD=0
NO_BUMP=0
ONLY="db,site,gateway,admin"

usage() {
  sed -n '2,25p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

while [ $# -gt 0 ]; do
  case "$1" in
    --config)     CONFIG="$2"; shift 2 ;;
    --skip-build) SKIP_BUILD=1; shift ;;
    --no-bump)    NO_BUMP=1; shift ;;
    --only)       ONLY="$2"; shift 2 ;;
    -h|--help)    usage; exit 0 ;;
    *) echo "✖ Unknown argument: $1"; usage; exit 1 ;;
  esac
done

if [ ! -f "$CONFIG" ]; then
  echo "✖ Config not found: $CONFIG"
  echo "  cp \"$SCRIPT_DIR/servers.env.example\" \"$CONFIG\"  — then fill it in."
  exit 1
fi
# shellcheck source=/dev/null
source "$CONFIG"

# Optional settings default to empty.
SITE_BETA_ACCESS_KEY="${SITE_BETA_ACCESS_KEY:-}"
DB_LAN_ACCESS_CIDR="${DB_LAN_ACCESS_CIDR:-}"
ADMIN_SEED_EMAIL="${ADMIN_SEED_EMAIL:-}"
ADMIN_SEED_PASSWORD="${ADMIN_SEED_PASSWORD:-}"
ADMIN_DOMAIN="${ADMIN_DOMAIN:-}"
GATEWAY_PUBLIC_HOST="${GATEWAY_PUBLIC_HOST:-}"
GATEWAY_UPSTREAM_PROVIDER="${GATEWAY_UPSTREAM_PROVIDER:-}"
GATEWAY_UPSTREAM_BASE_URL="${GATEWAY_UPSTREAM_BASE_URL:-}"
GATEWAY_UPSTREAM_API_KEY="${GATEWAY_UPSTREAM_API_KEY:-}"

# Gateway routing vars (all optional). Collect every NON-EMPTY
# GATEWAY_PLAN_<PLAN>_<FIELD>, GATEWAY_BACKEND_<NAME>_<FIELD>, and the mapped
# legacy GATEWAY_UPSTREAM_* from the config into KEY=VALUE lines; the gateway
# step upserts exactly these into /opt/ai-gateway/.env. Vars left empty are
# never written (set GATEWAY_UPSTREAM_PROVIDER="none" to explicitly disable
# the legacy fallback — unconfigured plans then return 503 instead of mock).
GATEWAY_PLAN_VARS=""
for _plan in FREE BETA PRO STUDIO LABEL; do
  for _field in PROVIDER BASE_URL API_KEY MODEL ALLOWED_MODELS MODELS EMBEDDING_MODEL; do
    _var="GATEWAY_PLAN_${_plan}_${_field}"
    _val="${!_var:-}"
    if [ -n "$_val" ]; then
      GATEWAY_PLAN_VARS="${GATEWAY_PLAN_VARS}${_var}=${_val}"$'\n'
    fi
  done
done
# Named backend blocks have arbitrary names, and the guardrail / chat-log /
# client-update families are open-ended — pull the var names off the config
# file itself, then read the (already-sourced) values via indirect expansion.
while IFS= read -r _var; do
  _val="${!_var:-}"
  if [ -n "$_val" ]; then
    GATEWAY_PLAN_VARS="${GATEWAY_PLAN_VARS}${_var}=${_val}"$'\n'
  fi
done < <(grep -oE '^GATEWAY_(BACKEND|GUARDRAIL|CHAT_LOG|MODEL_COSTS|COST_FALLBACK|CLIENT)[A-Z0-9_]*' "$CONFIG" | sort -u)
# Legacy upstream fallback: upsert on re-runs too (names map GATEWAY_UPSTREAM_*
# -> UPSTREAM_* in the gateway's .env).
for _field in PROVIDER BASE_URL API_KEY; do
  _var="GATEWAY_UPSTREAM_${_field}"
  _val="${!_var:-}"
  if [ -n "$_val" ]; then
    GATEWAY_PLAN_VARS="${GATEWAY_PLAN_VARS}UPSTREAM_${_field}=${_val}"$'\n'
  fi
done
unset _plan _field _var _val

require() {
  local name
  for name in "$@"; do
    if [ -z "${!name:-}" ]; then
      echo "✖ $name is not set in $CONFIG"
      exit 1
    fi
  done
}
require SSH_KEY DB_SSH WEB_SSH GW_SSH \
        DB_PRIVATE_IP WEB_PRIVATE_IP GW_PRIVATE_IP \
        DB_SITE_PASSWORD DB_GATEWAY_PASSWORD APP_WEB_URL
case ",$ONLY," in
  *",admin,"*) require ADMIN_SSH ADMIN_PRIVATE_IP ;;
esac

if [ "$DB_SITE_PASSWORD" = "CHANGE-ME-SITE" ] || [ "$DB_GATEWAY_PASSWORD" = "CHANGE-ME-GATEWAY" ]; then
  echo "✖ Change the DB_*_PASSWORD placeholders in $CONFIG first."
  exit 1
fi

want() {
  case ",$ONLY," in
    *",$1,"*) return 0 ;;
    *)        return 1 ;;
  esac
}

SSH_OPTS=(-i "$SSH_KEY" -o IdentitiesOnly=yes -o BatchMode=yes
          -o StrictHostKeyChecking=accept-new -o ConnectTimeout=10)
rsh() { local target="$1"; shift; ssh "${SSH_OPTS[@]}" "$target" "$@"; }
rcp() { scp "${SSH_OPTS[@]}" "$@"; }

step() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }

SITE_DB_URL="postgresql://fl_automate:${DB_SITE_PASSWORD}@${DB_PRIVATE_IP}:5432/fl_automate"
GW_DB_URL="postgresql://fl_gateway:${DB_GATEWAY_PASSWORD}@${DB_PRIVATE_IP}:5432/fl_gateway"

# Shared prelude for every remote script: fail-fast, sudo shim (root or
# passwordless sudo), and a node-20 + unzip installer. Single-quoted heredoc —
# nothing here expands locally.
REMOTE_PRELUDE=$(cat <<'PRELUDE'
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
if [ "$(id -u)" = 0 ]; then
  command -v sudo >/dev/null 2>&1 || { apt-get update -qq; apt-get install -y sudo; }
  SUDO=""
  AS_PG="runuser -u postgres --"
else
  SUDO="sudo -n"
  AS_PG="sudo -n -u postgres"
fi
ensure_node() {
  if ! command -v curl >/dev/null 2>&1; then
    $SUDO apt-get update -qq
    $SUDO apt-get install -y curl ca-certificates
  fi
  if ! command -v node >/dev/null 2>&1 || [ "$(node -p 'process.versions.node.split(".")[0]')" -lt 20 ]; then
    # NodeSource first; fall back to Ubuntu's own nodejs (>= 20 on 24.04+)
    # when NodeSource has no repo for this release yet.
    if curl -fsSL https://deb.nodesource.com/setup_20.x | $SUDO bash - \
       && $SUDO apt-get install -y nodejs; then :; else
      echo "NodeSource unavailable — using Ubuntu's nodejs package"
      $SUDO apt-get update -qq
      $SUDO apt-get install -y nodejs npm
    fi
  fi
  command -v unzip >/dev/null 2>&1 || $SUDO apt-get install -y unzip
}
PRELUDE
)

# ---------------------------------------------------------------------------
# Preflight: can we reach every server we're about to touch?
# ---------------------------------------------------------------------------
step "Preflight — SSH connectivity"
for pair in "db:$DB_SSH" "site:$WEB_SSH" "gateway:$GW_SSH" "admin:${ADMIN_SSH:-}"; do
  name="${pair%%:*}"; target="${pair#*:}"
  if want "$name"; then
    if rsh "$target" 'echo ok' >/dev/null; then
      echo "  ✔ $name ($target)"
    else
      echo "  ✖ Cannot SSH to $name ($target) with key $SSH_KEY"
      exit 1
    fi
  fi
done

# ---------------------------------------------------------------------------
# Build both bundles locally (both schemas are Postgres-only).
# ---------------------------------------------------------------------------
if [ "$SKIP_BUILD" = 0 ]; then
  if want site; then
    # ------------------------------------------------------------------
    # Auto-increment the PRODUCT version (repo-root VERSION file) so every
    # publish ships a build older clients can DETECT as newer: the file
    # stamps all assemblies (Directory.Build.props), names the installer
    # artifact (package.ps1), and — once the artifact actually uploads — is
    # advertised through the gateway's /v1/client-version (see below).
    # Bumped BEFORE the payload restage so natives + plugin + installer all
    # build under the same new version. --no-bump re-publishes the current one.
    # ------------------------------------------------------------------
    VERSION_FILE="$ROOT/VERSION"
    PRODUCT_VERSION="$(tr -d ' \r\n' < "$VERSION_FILE")"
    if [ "$NO_BUMP" = 0 ]; then
      if [[ "$PRODUCT_VERSION" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)$ ]]; then
        PRODUCT_VERSION="${BASH_REMATCH[1]}.${BASH_REMATCH[2]}.$(( BASH_REMATCH[3] + 1 ))"
        printf '%s\n' "$PRODUCT_VERSION" > "$VERSION_FILE"
        step "Product version bumped -> $PRODUCT_VERSION (VERSION file; --no-bump re-publishes the same version)"
      else
        echo "!! WARNING: VERSION file content '$PRODUCT_VERSION' is not <major>.<minor>.<patch> — not bumping."
      fi
    else
      step "Product version kept at $PRODUCT_VERSION (--no-bump)"
    fi

    step "Building marketing site bundle"
    (cd "$ROOT/marketing" && npm run build:zip)

    # Windows desktop-app installer (distributed by the site's download API).
    # Two best-effort stages — dotnet/cmake may be busy or missing; a failure
    # here only degrades/skips the installer upload, never the site deploy:
    #
    #   1. bootstrap/build-and-stage.ps1 -Production — rebuilds the NATIVE DLLs
    #      (version.dll, FlClrHost.dll, FlBridge.dll — debug pipe OFF) and the
    #      Avalonia plugin closure, then syncs them into the installer's
    #      payload dir. package.ps1 alone only refreshes the managed bits.
    #   2. installer/package.ps1 — publishes the installer exe + payload and
    #      zips it into installer/artifacts/.
    PS_EXE="$(command -v pwsh.exe || command -v powershell.exe || true)"
    step "Restaging installer payload (bootstrap/build-and-stage.ps1 -Production)"
    STAGE_PS1="$ROOT/bootstrap/build-and-stage.ps1"
    command -v cygpath >/dev/null 2>&1 && STAGE_PS1="$(cygpath -w "$STAGE_PS1")"
    if [ -z "$PS_EXE" ]; then
      echo "!! WARNING: no pwsh.exe/powershell.exe on PATH — payload NOT restaged; installer may ship a stale native payload."
    elif ! "$PS_EXE" -NoProfile -ExecutionPolicy Bypass -File "$STAGE_PS1" -Production; then
      echo "!! WARNING: payload restage failed (needs cmake + dotnet) — continuing; installer may ship a STALE native payload."
      echo "   (Re-run later: pwsh -NoProfile -File bootstrap/build-and-stage.ps1 -Production)"
    fi

    step "Packaging Windows installer (installer/package.ps1)"
    PKG_PS1="$ROOT/installer/package.ps1"
    command -v cygpath >/dev/null 2>&1 && PKG_PS1="$(cygpath -w "$PKG_PS1")"
    if [ -z "$PS_EXE" ]; then
      echo "!! WARNING: no pwsh.exe/powershell.exe on PATH — installer not packaged; upload will be skipped."
    elif ! "$PS_EXE" -NoProfile -ExecutionPolicy Bypass -File "$PKG_PS1"; then
      echo "!! WARNING: installer packaging failed — continuing WITHOUT an installer upload."
      echo "   (Re-run later, or: pwsh -NoProfile -File installer/package.ps1)"
    fi
  fi
  if want gateway; then
    step "Building AI gateway bundle"
    (cd "$ROOT/ai-gateway" && npm run build:zip)
  fi
  if want admin; then
    step "Building Ops Console bundle"
    (cd "$ROOT/admin-console" && npm run build:zip)
  fi
fi

newest_zip() { ls -t -- "$@" 2>/dev/null | head -n 1; }
SITE_ZIP="$(newest_zip "$ROOT/marketing/artifacts"/fl-automate-site-v*.zip)"
GW_ZIP="$(newest_zip "$ROOT/ai-gateway/artifacts"/ai-gateway-v*.zip)"
ADMIN_ZIP="$(newest_zip "$ROOT/admin-console/artifacts"/fl-console-v*.zip || true)"
# The installer artifact is optional by design (see the packaging warning above).
INSTALLER_ZIP="$(newest_zip "$ROOT/installer/artifacts"/fl-automate-installer-v*.zip || true)"
if want site && [ -z "$SITE_ZIP" ]; then echo "✖ No site zip in marketing/artifacts — run without --skip-build."; exit 1; fi
if want gateway && [ -z "$GW_ZIP" ]; then echo "✖ No gateway zip in ai-gateway/artifacts — run without --skip-build."; exit 1; fi
if want admin && [ -z "$ADMIN_ZIP" ]; then echo "✖ No console zip in admin-console/artifacts — run without --skip-build."; exit 1; fi

# ---------------------------------------------------------------------------
# Auto-advertise the published client version. When THIS run uploads an
# installer (site step) AND configures the gateway, the gateway's
# GATEWAY_CLIENT_LATEST_VERSION is set from the ARTIFACT NAME being uploaded —
# not the VERSION file — so the advertised version can never run ahead of a
# failed packaging (packaging is best-effort; a bump with no artifact
# advertises nothing). An explicit GATEWAY_CLIENT_LATEST_VERSION in servers.env
# always wins (manual override / rollback). Older plugins compare this against
# their stamped assembly version and show the in-chat update banner.
# ---------------------------------------------------------------------------
if [ -n "$INSTALLER_ZIP" ] && want site; then
  _iz="$(basename "$INSTALLER_ZIP")"
  PUBLISHED_CLIENT_VERSION="${_iz#fl-automate-installer-v}"
  PUBLISHED_CLIENT_VERSION="${PUBLISHED_CLIENT_VERSION%.zip}"
  if grep -q '^GATEWAY_CLIENT_LATEST_VERSION=' "$CONFIG"; then
    echo "  (GATEWAY_CLIENT_LATEST_VERSION set explicitly in servers.env — keeping the manual value)"
  elif want gateway; then
    GATEWAY_PLAN_VARS="${GATEWAY_PLAN_VARS}GATEWAY_CLIENT_LATEST_VERSION=${PUBLISHED_CLIENT_VERSION}"$'\n'
    step "Gateway will advertise client version $PUBLISHED_CLIENT_VERSION (from $(basename "$INSTALLER_ZIP"))"
  else
    echo "!! NOTE: installer v$PUBLISHED_CLIENT_VERSION is being published but the gateway is NOT in this run —"
    echo "   clients keep seeing the old advertised version until a gateway deploy (--only gateway)."
  fi
  unset _iz
fi

# ---------------------------------------------------------------------------
# Server A — PostgreSQL: install, roles/databases, private-network access.
# ---------------------------------------------------------------------------
if want db; then
  step "Server A ($DB_SSH) — PostgreSQL"
  rsh "$DB_SSH" bash -s <<EOF
$REMOTE_PRELUDE

if ! command -v psql >/dev/null 2>&1; then
  \$SUDO apt-get update -qq
  \$SUDO apt-get install -y postgresql
fi
\$SUDO systemctl enable --now postgresql

# Settings first (scram before role passwords are hashed), then restart below.
\$AS_PG psql -qc "ALTER SYSTEM SET listen_addresses = 'localhost,$DB_PRIVATE_IP'"
\$AS_PG psql -qc "ALTER SYSTEM SET password_encryption = 'scram-sha-256'"

HBA=\$(\$AS_PG psql -tAc "SHOW hba_file")
LINE_SITE="host fl_automate fl_automate $WEB_PRIVATE_IP/32 scram-sha-256"
LINE_GW="host fl_gateway fl_gateway $GW_PRIVATE_IP/32 scram-sha-256"
\$SUDO grep -qF "\$LINE_SITE" "\$HBA" || echo "\$LINE_SITE" | \$SUDO tee -a "\$HBA" >/dev/null
\$SUDO grep -qF "\$LINE_GW"   "\$HBA" || echo "\$LINE_GW"   | \$SUDO tee -a "\$HBA" >/dev/null

# Ops Console (Server D) reads BOTH databases with the app credentials.
if [ -n "${ADMIN_PRIVATE_IP:-}" ]; then
  LINE_ADM_SITE="host fl_automate fl_automate $ADMIN_PRIVATE_IP/32 scram-sha-256"
  LINE_ADM_GW="host fl_gateway fl_gateway $ADMIN_PRIVATE_IP/32 scram-sha-256"
  \$SUDO grep -qF "\$LINE_ADM_SITE" "\$HBA" || echo "\$LINE_ADM_SITE" | \$SUDO tee -a "\$HBA" >/dev/null
  \$SUDO grep -qF "\$LINE_ADM_GW"   "\$HBA" || echo "\$LINE_ADM_GW"   | \$SUDO tee -a "\$HBA" >/dev/null
fi

# Optional LAN/VPN-wide access (DB_LAN_ACCESS_CIDR in servers.env) for dev
# machines and ad-hoc tooling — still password-authenticated, private net only.
if [ -n "$DB_LAN_ACCESS_CIDR" ]; then
  LINE_LAN_SITE="host fl_automate fl_automate $DB_LAN_ACCESS_CIDR scram-sha-256"
  LINE_LAN_GW="host fl_gateway fl_gateway $DB_LAN_ACCESS_CIDR scram-sha-256"
  \$SUDO grep -qF "\$LINE_LAN_SITE" "\$HBA" || echo "\$LINE_LAN_SITE" | \$SUDO tee -a "\$HBA" >/dev/null
  \$SUDO grep -qF "\$LINE_LAN_GW"   "\$HBA" || echo "\$LINE_LAN_GW"   | \$SUDO tee -a "\$HBA" >/dev/null
fi

\$SUDO systemctl restart postgresql

# Roles + databases (idempotent: create or refresh the password).
if \$AS_PG psql -tAc "SELECT 1 FROM pg_roles WHERE rolname='fl_automate'" | grep -q 1; then
  \$AS_PG psql -qc "ALTER ROLE fl_automate WITH LOGIN PASSWORD '$DB_SITE_PASSWORD'"
else
  \$AS_PG psql -qc "CREATE ROLE fl_automate LOGIN PASSWORD '$DB_SITE_PASSWORD'"
fi
if \$AS_PG psql -tAc "SELECT 1 FROM pg_roles WHERE rolname='fl_gateway'" | grep -q 1; then
  \$AS_PG psql -qc "ALTER ROLE fl_gateway WITH LOGIN PASSWORD '$DB_GATEWAY_PASSWORD'"
else
  \$AS_PG psql -qc "CREATE ROLE fl_gateway LOGIN PASSWORD '$DB_GATEWAY_PASSWORD'"
fi
if ! \$AS_PG psql -tAc "SELECT 1 FROM pg_database WHERE datname='fl_automate'" | grep -q 1; then
  \$AS_PG createdb -O fl_automate fl_automate
fi
if ! \$AS_PG psql -tAc "SELECT 1 FROM pg_database WHERE datname='fl_gateway'" | grep -q 1; then
  \$AS_PG createdb -O fl_gateway fl_gateway
fi

# Firewall: the app servers (+ optional LAN CIDR) may reach 5432 (no-op if ufw absent).
if command -v ufw >/dev/null 2>&1; then
  \$SUDO ufw allow from $WEB_PRIVATE_IP to any port 5432 proto tcp >/dev/null || true
  \$SUDO ufw allow from $GW_PRIVATE_IP to any port 5432 proto tcp >/dev/null || true
  if [ -n "${ADMIN_PRIVATE_IP:-}" ]; then
    \$SUDO ufw allow from ${ADMIN_PRIVATE_IP:-127.0.0.1} to any port 5432 proto tcp >/dev/null || true
  fi
  if [ -n "$DB_LAN_ACCESS_CIDR" ]; then
    \$SUDO ufw allow from $DB_LAN_ACCESS_CIDR to any port 5432 proto tcp >/dev/null || true
  fi
fi

echo "postgres ready: fl_automate + fl_gateway, listening on $DB_PRIVATE_IP:5432"
EOF
fi

# ---------------------------------------------------------------------------
# Server B — marketing site.
# ---------------------------------------------------------------------------
if want site; then
  step "Server B ($WEB_SSH) — marketing site ($(basename "$SITE_ZIP"))"
  rcp "$SITE_ZIP" "$WEB_SSH:/tmp/fl-automate-site.zip"
  HAVE_INSTALLER=0
  if [ -n "$INSTALLER_ZIP" ]; then
    echo "  + installer artifact: $(basename "$INSTALLER_ZIP")"
    rcp "$INSTALLER_ZIP" "$WEB_SSH:/tmp/fl-automate-installer.zip"
    HAVE_INSTALLER=1
  else
    echo "  (no installer artifact in installer/artifacts — download upload skipped)"
  fi
  rsh "$WEB_SSH" bash -s <<EOF
$REMOTE_PRELUDE
ensure_node

\$SUDO mkdir -p /opt/fl-automate
\$SUDO unzip -qo /tmp/fl-automate-site.zip -d /opt/fl-automate
cd /opt/fl-automate

# Windows installer download — the API serves this exact path (stable name).
# Staged before the final chown -R below so www-data ends up owning it too.
if [ "$HAVE_INSTALLER" = 1 ] && [ -f /tmp/fl-automate-installer.zip ]; then
  \$SUDO mkdir -p /opt/fl-automate/downloads
  \$SUDO mv -f /tmp/fl-automate-installer.zip /opt/fl-automate/downloads/fl-automate-installer.zip
  echo "installer staged -> downloads/fl-automate-installer.zip"
fi

# A server .env always carries JWT keys (the app refuses to boot without
# them). One that doesn't is a stale leftover from an older layout — keep it
# aside and regenerate rather than failing later.
if [ -f .env ] && ! grep -q '^JWT_PRIVATE_KEY=..' .env; then
  \$SUDO mv .env ".env.stale-\$(date +%Y%m%d%H%M%S)"
  echo "existing .env was stale (no JWT keys) — backed up, regenerating"
fi

if [ ! -f .env ]; then
  # First install: generate JWT keys + a ready .env, then point it at Server A.
  # (Re-runs keep the existing .env untouched — edit it on the server instead.)
  \$SUDO node scripts/gen-keys.mjs
  \$SUDO sed -i "s|^DATABASE_URL=.*|DATABASE_URL=\"$SITE_DB_URL\"|" .env
  \$SUDO sed -i "s|^APP_WEB_URL=.*|APP_WEB_URL=$APP_WEB_URL|" .env
  if [ -n "$SITE_BETA_ACCESS_KEY" ]; then
    if grep -q '^BETA_ACCESS_KEY=' .env; then
      \$SUDO sed -i "s|^BETA_ACCESS_KEY=.*|BETA_ACCESS_KEY=$SITE_BETA_ACCESS_KEY|" .env
    else
      echo "BETA_ACCESS_KEY=$SITE_BETA_ACCESS_KEY" | \$SUDO tee -a .env >/dev/null
    fi
  fi
  echo "wrote fresh .env (Stripe/Graph left empty -> purchasing disabled = beta mode)"
else
  echo "existing .env kept as-is"
  # ...but never leave it without a database URL (prisma would abort).
  if ! grep -q '^DATABASE_URL=' .env; then
    echo "DATABASE_URL=\"$SITE_DB_URL\"" | \$SUDO tee -a .env >/dev/null
    echo "appended missing DATABASE_URL"
  fi
  # Beta key: only ever ADD a missing line — an existing value (even an
  # emptied-out one) is a server-side decision we keep.
  if [ -n "$SITE_BETA_ACCESS_KEY" ] && ! grep -q '^BETA_ACCESS_KEY=' .env; then
    echo "BETA_ACCESS_KEY=$SITE_BETA_ACCESS_KEY" | \$SUDO tee -a .env >/dev/null
    echo "appended missing BETA_ACCESS_KEY"
  fi
fi

\$SUDO bash deploy/install.sh
\$SUDO chown -R www-data:www-data /opt/fl-automate
\$SUDO rm -f /tmp/fl-automate-site.zip
EOF
fi

# ---------------------------------------------------------------------------
# Server C — AI gateway (verifies the JWTs Server B mints, so fetch the
# public key from B first).
# ---------------------------------------------------------------------------
if want gateway; then
  step "Server C ($GW_SSH) — AI gateway ($(basename "$GW_ZIP"))"

  JWT_PUB="$(rsh "$WEB_SSH" 'if [ "$(id -u)" = 0 ]; then grep "^JWT_PUBLIC_KEY=" /opt/fl-automate/.env; else sudo -n grep "^JWT_PUBLIC_KEY=" /opt/fl-automate/.env; fi' | head -n 1 | cut -d= -f2- | tr -d '\r')"
  if [ -z "$JWT_PUB" ]; then
    echo "✖ Could not read JWT_PUBLIC_KEY from $WEB_SSH:/opt/fl-automate/.env"
    echo "  Deploy the site first (it mints the keys): bash install-all.sh --only site"
    exit 1
  fi

  rcp "$GW_ZIP" "$GW_SSH:/tmp/ai-gateway.zip"
  rsh "$GW_SSH" bash -s <<EOF
$REMOTE_PRELUDE
ensure_node

\$SUDO mkdir -p /opt/ai-gateway
\$SUDO unzip -qo /tmp/ai-gateway.zip -d /opt/ai-gateway
cd /opt/ai-gateway

\$SUDO npm ci --omit=dev --no-audit --no-fund
\$SUDO npx prisma generate

if [ ! -f .env ]; then
  \$SUDO cp .env.example .env
  \$SUDO sed -i "s|^DATABASE_URL=.*|DATABASE_URL=\"$GW_DB_URL\"|" .env
  \$SUDO sed -i "s|^JWT_PUBLIC_KEY=.*|JWT_PUBLIC_KEY=$JWT_PUB|" .env
  \$SUDO sed -i "s|^UPSTREAM_PROVIDER=.*|UPSTREAM_PROVIDER=$GATEWAY_UPSTREAM_PROVIDER|" .env
  if [ -n "$GATEWAY_UPSTREAM_BASE_URL" ]; then
    \$SUDO sed -i "s|^UPSTREAM_BASE_URL=.*|UPSTREAM_BASE_URL=$GATEWAY_UPSTREAM_BASE_URL|" .env
  fi
  if [ -n "$GATEWAY_UPSTREAM_API_KEY" ]; then
    \$SUDO sed -i "s|^UPSTREAM_API_KEY=.*|UPSTREAM_API_KEY=$GATEWAY_UPSTREAM_API_KEY|" .env
  fi
  echo "wrote fresh .env"
else
  echo "existing .env kept as-is"
  if ! grep -q '^DATABASE_URL=' .env; then
    echo "DATABASE_URL=\"$GW_DB_URL\"" | \$SUDO tee -a .env >/dev/null
    echo "appended missing DATABASE_URL"
  fi
  CURRENT_PUB=\$(grep '^JWT_PUBLIC_KEY=' .env | head -n 1 | cut -d= -f2-)
  if [ "\$CURRENT_PUB" != "$JWT_PUB" ]; then
    echo "!! WARNING: JWT_PUBLIC_KEY here differs from the site's — token verification will fail."
    echo "   Fix: update JWT_PUBLIC_KEY in /opt/ai-gateway/.env, then systemctl restart ai-gateway"
  fi
fi

# Per-plan routing (GATEWAY_PLAN_<PLAN>_* from servers.env): upsert every var
# that is NON-EMPTY locally — replace the existing line, else append. Vars left
# empty in servers.env are never touched here, so the server-side .env (and the
# gateway's legacy UPSTREAM_* fallback) stays as-is. The values were expanded
# ONCE on the deploy machine; the quoted PLAN_VARS heredoc keeps them literal.
while IFS= read -r kv; do
  [ -n "\$kv" ] || continue
  k="\${kv%%=*}"
  \$SUDO sed -i "/^\${k}=/d" .env
  printf '%s\n' "\$kv" | \$SUDO tee -a .env >/dev/null
  echo "plan routing: set \$k"
done <<'PLAN_VARS'
$GATEWAY_PLAN_VARS
PLAN_VARS

# The committed migrations are SQLite-generated; on Postgres sync the schema.
\$SUDO npx prisma db push --skip-generate

\$SUDO tee /etc/systemd/system/ai-gateway.service >/dev/null <<'UNIT'
[Unit]
Description=FL Automate AI Gateway
After=network.target

[Service]
Type=simple
WorkingDirectory=/opt/ai-gateway
ExecStart=/usr/bin/node dist/main.js
Restart=on-failure
RestartSec=5
Environment=NODE_ENV=production
User=www-data
Group=www-data

[Install]
WantedBy=multi-user.target
UNIT

\$SUDO chown -R www-data:www-data /opt/ai-gateway
\$SUDO systemctl daemon-reload
\$SUDO systemctl enable ai-gateway >/dev/null
\$SUDO systemctl restart ai-gateway
\$SUDO rm -f /tmp/ai-gateway.zip
EOF
fi

# ---------------------------------------------------------------------------
# Server D — Ops Console (VPN-only back office; talks straight to Server A
# with the same app credentials, so the db step must have added its pg_hba
# entries first).
# ---------------------------------------------------------------------------
if want admin; then
  step "Server D ($ADMIN_SSH) — Ops Console ($(basename "$ADMIN_ZIP"))"
  rcp "$ADMIN_ZIP" "$ADMIN_SSH:/tmp/fl-console.zip"
  rsh "$ADMIN_SSH" bash -s <<EOF
$REMOTE_PRELUDE
ensure_node

\$SUDO mkdir -p /opt/fl-console
\$SUDO unzip -qo /tmp/fl-console.zip -d /opt/fl-console
cd /opt/fl-console

if [ ! -f .env ]; then
  # First install: write a ready .env pointed at Server A, with a fresh
  # session secret and the seed login from servers.env. Re-runs never touch it.
  \$SUDO cp .env.example .env
  SECRET=\$(node -e 'console.log(require("crypto").randomBytes(32).toString("hex"))')
  \$SUDO sed -i "s|^ADMIN_SESSION_SECRET=.*|ADMIN_SESSION_SECRET=\$SECRET|" .env
  \$SUDO sed -i "s|^MARKETING_DATABASE_URL=.*|MARKETING_DATABASE_URL=\"$SITE_DB_URL\"|" .env
  \$SUDO sed -i "s|^GATEWAY_DATABASE_URL=.*|GATEWAY_DATABASE_URL=\"$GW_DB_URL\"|" .env
  if [ -n "$ADMIN_SEED_EMAIL" ]; then
    \$SUDO sed -i "s|^ADMIN_SEED_EMAIL=.*|ADMIN_SEED_EMAIL=$ADMIN_SEED_EMAIL|" .env
  fi
  if [ -n "$ADMIN_SEED_PASSWORD" ]; then
    \$SUDO sed -i "s|^ADMIN_SEED_PASSWORD=.*|ADMIN_SEED_PASSWORD=$ADMIN_SEED_PASSWORD|" .env
  else
    echo "!! WARNING: ADMIN_SEED_PASSWORD empty in servers.env — no seed account"
    echo "   will be created; set it in /opt/fl-console/.env and restart fl-console."
  fi
  echo "wrote fresh .env"
else
  echo "existing .env kept as-is"
  if ! grep -q '^MARKETING_DATABASE_URL=' .env; then
    echo "MARKETING_DATABASE_URL=\"$SITE_DB_URL\"" | \$SUDO tee -a .env >/dev/null
    echo "appended missing MARKETING_DATABASE_URL"
  fi
  if ! grep -q '^GATEWAY_DATABASE_URL=' .env; then
    echo "GATEWAY_DATABASE_URL=\"$GW_DB_URL\"" | \$SUDO tee -a .env >/dev/null
    echo "appended missing GATEWAY_DATABASE_URL"
  fi
fi

# deps + three prisma clients + dist/generated sync + self-signed TLS cert
# (CN from ADMIN_DOMAIN in servers.env; kept across re-runs) + console schema
# + systemd (unit serves HTTPS :443 with an HTTP :80 redirect)
\$SUDO env TLS_DOMAIN="${ADMIN_DOMAIN:-fl-console}" bash deploy/install.sh
\$SUDO chown -R www-data:www-data /opt/fl-console
\$SUDO systemctl restart fl-console
\$SUDO rm -f /tmp/fl-console.zip
EOF
fi

# ---------------------------------------------------------------------------
# Health checks (from inside each box — public routing is Cloudflare's job).
# ---------------------------------------------------------------------------
step "Health checks"
wait_health() {
  local target="$1" url="$2" i
  for i in $(seq 1 15); do
    if rsh "$target" "curl -fsSk --max-time 3 $url" >/dev/null 2>&1; then
      echo "  ✔ $target  $url"
      return 0
    fi
    sleep 2
  done
  echo "  ✖ $target  $url is not responding"
  echo "    ssh -i $SSH_KEY $target 'journalctl -u fl-automate -u ai-gateway -u fl-console -n 50'"
  return 1
}
HEALTH_OK=1
if want site;    then wait_health "$WEB_SSH"   "http://localhost:3001/api/health" || HEALTH_OK=0; fi
if want gateway; then wait_health "$GW_SSH"    "http://localhost:3002/health"     || HEALTH_OK=0; fi
if want admin;   then wait_health "$ADMIN_SSH" "https://localhost/api/health" || HEALTH_OK=0; fi

# ---------------------------------------------------------------------------
# Network summary — everything you need for the Cloudflare dashboard and your
# firewall/UI tooling. Informational only; no commands to run.
# ---------------------------------------------------------------------------
SITE_HOST="${APP_WEB_URL#*://}"; SITE_HOST="${SITE_HOST%%/*}"
GW_HOST_PUBLIC="${GATEWAY_PUBLIC_HOST:-ai.$SITE_HOST}"

step "Network details — deployed: $ONLY"
row() { printf '  %-26s %-26s %s\n' "$@"; }
echo
echo "CLOUDFLARE (dashboard: Zero Trust -> Networks -> Tunnels -> Public Hostnames)"
row "Public hostname" "Service URL" "Tunnel runs on"
row "$SITE_HOST" "http://localhost:3001" "Server B ($WEB_SSH)"
row "www.$SITE_HOST" "http://localhost:3001" "Server B (same tunnel)"
row "$GW_HOST_PUBLIC" "http://localhost:3002" "Server C ($GW_SSH)"
echo
echo "  If you run ONE shared cloudflared connector (e.g. on the Proxmox host)"
echo "  instead of one per box, point the service URLs at the private IPs instead —"
echo "  both apps listen on all interfaces:"
row "$SITE_HOST" "http://$WEB_PRIVATE_IP:3001" ""
row "$GW_HOST_PUBLIC" "http://$GW_PRIVATE_IP:3002" ""
cat <<SUMMARY

PORTS PER SERVER
  Server A  db       $DB_PRIVATE_IP
    5432/tcp   PostgreSQL — PRIVATE network only. Accepts:
               $WEB_PRIVATE_IP (site), $GW_PRIVATE_IP (gateway)${ADMIN_PRIVATE_IP:+, $ADMIN_PRIVATE_IP (console)}${DB_LAN_ACCESS_CIDR:+, and $DB_LAN_ACCESS_CIDR (LAN/VPN)}.
               Never expose this through Cloudflare or a public NIC.
    22/tcp     SSH (used by this script)
  Server B  site     $WEB_PRIVATE_IP
    3001/tcp   marketing site + /api (systemd: fl-automate).
               Reached only via the Cloudflare Tunnel — no inbound port needed.
    22/tcp     SSH
  Server C  gateway  $GW_PRIVATE_IP
    3002/tcp   AI gateway /v1 + /admin (systemd: ai-gateway).
               Reached only via the Cloudflare Tunnel — no inbound port needed.
    22/tcp     SSH
  Server D  console  ${ADMIN_PRIVATE_IP:-"(not configured)"}
    3005/tcp   Ops Console UI + /api (systemd: fl-console).
               VPN/LAN ONLY — no Cloudflare hostname, no public exposure, ever.
    22/tcp     SSH

DATABASES (on Server A — passwords are in $CONFIG)
  site      postgresql://fl_automate:***@$DB_PRIVATE_IP:5432/fl_automate
  gateway   postgresql://fl_gateway:***@$DB_PRIVATE_IP:5432/fl_gateway

PUBLIC ENDPOINTS (once the Cloudflare hostnames above are in place)
SUMMARY
printf '  %-40s %s\n' "https://$SITE_HOST/api/health" "site heartbeat"
printf '  %-40s %s\n' "https://$GW_HOST_PUBLIC/health" "gateway heartbeat"
printf '  %-40s %s\n' "https://$GW_HOST_PUBLIC/v1/..." "what the desktop agent calls"
printf '  %-40s %s\n' "https://$SITE_HOST/api/downloads/installer" "Windows installer (authenticated)"
if want site; then
  if [ "$HAVE_INSTALLER" = 1 ]; then
    echo "    installer artifact uploaded this run: $(basename "$INSTALLER_ZIP")"
  else
    echo "    installer artifact NOT uploaded this run — stage + package it first:"
    echo "      pwsh -NoProfile -File bootstrap/build-and-stage.ps1 -Production"
    echo "      pwsh -NoProfile -File installer/package.ps1"
    echo "    then: bash install-all.sh --skip-build --only site"
  fi
fi
cat <<SUMMARY

OTHER ONE-TIME STEPS
  * Admin tooling lives ONLY on the VPN-only Ops Console (Server D):
      https://$ADMIN_PRIVATE_IP/ — log in with the seed account from servers.env.
  * Currently BETA mode (STRIPE_SECRET_KEY empty -> purchasing disabled).
    To start selling: fill in Stripe + email (GRAPH_*) in
    /opt/fl-automate/.env on Server B, then restart the fl-automate service.
SUMMARY
if [ "$HEALTH_OK" = 0 ]; then
  echo "!! One or more health checks failed — see above."
  exit 1
fi
