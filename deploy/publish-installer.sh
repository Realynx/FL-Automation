#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# FL Automate — publish the Windows installer ONLY (no marketing redeploy).
#
#   bash deploy/publish-installer.sh [servers.env]
#
# install-all.sh uploads the installer as part of its `site` step, which also
# rebuilds/redeploys the whole marketing app. When you only want to push a
# freshly-packaged installer (installer/package.ps1) to the download server,
# use this: it scp's the newest installer/artifacts/fl-automate-installer-v*.zip
# to Server B and swaps it into /opt/fl-automate/downloads/fl-automate-installer.zip
# (the stable path the marketing API serves), leaving the running site untouched.
#
# Reads SSH_KEY + WEB_SSH from deploy/servers.env (gitignored). No secrets here.
# ---------------------------------------------------------------------------
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
CONFIG="${1:-$SCRIPT_DIR/servers.env}"

[ -f "$CONFIG" ] || { echo "✖ config not found: $CONFIG (cp servers.env.example servers.env)"; exit 1; }
# shellcheck source=/dev/null
source "$CONFIG"
: "${SSH_KEY:?SSH_KEY not set in $CONFIG}"
: "${WEB_SSH:?WEB_SSH not set in $CONFIG}"

SSH_OPTS=(-i "$SSH_KEY" -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=accept-new)

ZIP="$(ls -t "$ROOT/installer/artifacts"/fl-automate-installer-v*.zip 2>/dev/null | head -n 1 || true)"
[ -n "$ZIP" ] || { echo "✖ no installer zip in installer/artifacts — run: pwsh -NoProfile -File installer/package.ps1"; exit 1; }

echo "==> Publishing $(basename "$ZIP") ($(du -h "$ZIP" | cut -f1)) -> $WEB_SSH (installer only, no site redeploy)"
scp "${SSH_OPTS[@]}" "$ZIP" "$WEB_SSH:/tmp/fl-automate-installer.zip"

ssh "${SSH_OPTS[@]}" "$WEB_SSH" bash -s <<'EOF'
set -e
SUDO=; [ "$(id -u)" -ne 0 ] && SUDO=sudo
$SUDO mkdir -p /opt/fl-automate/downloads
$SUDO mv -f /tmp/fl-automate-installer.zip /opt/fl-automate/downloads/fl-automate-installer.zip
# The marketing API runs as www-data — make sure it can read the new file.
$SUDO chown -R www-data:www-data /opt/fl-automate/downloads
echo "staged:"
ls -la /opt/fl-automate/downloads/fl-automate-installer.zip
EOF

echo "==> Done. Served (authenticated) at: ${APP_WEB_URL:-https://fl-automate.com}/api/downloads/installer"
