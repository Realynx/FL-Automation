# FL Automate — one-click three-server deploy

Automates the whole [three-server layout](../marketing/README.md#deploy--three-ubuntu-servers-database--site--ai-gateway)
from your dev machine over SSH keys + scp: builds both bundles, provisions
PostgreSQL, and installs/updates the marketing site and the AI gateway as
systemd services. Built for on-prem Ubuntu VMs (e.g. a Proxmox cluster) proxied
through Cloudflare Tunnels.

```bash
cd deploy
cp servers.env.example servers.env    # SSH targets, key, private IPs, DB passwords
nano servers.env                      # never commit this file
bash install-all.sh                   # one click: db + site + gateway
```

Run it from Git Bash on Windows (or any POSIX shell). Requirements: `ssh`,
`scp`, `node`/`npm` locally; the remote user must be root or have passwordless
sudo, with your SSH key in `authorized_keys`.

What a full run does:

1. **Preflight** — verifies SSH connectivity to every server it will touch.
2. **Build** — runs `npm run build:zip` in `marketing/` and `ai-gateway/`
   (both Prisma schemas are Postgres-only; nothing is flipped or restored),
   then restages the desktop payload (`bootstrap/build-and-stage.ps1
   -Production`) and packages the Windows installer (`installer/package.ps1`)
   — both best-effort, see below.
3. **Server A** — installs PostgreSQL, creates/refreshes the `fl_automate` and
   `fl_gateway` roles + databases, sets `listen_addresses`, adds per-app
   `pg_hba.conf` entries, and opens 5432 only to the two app servers (ufw).
4. **Server B** — installs Node 20 + unzip, scp's the site zip, generates JWT
   keys + `.env` on first install (pointed at Server A), runs the bundled
   `deploy/install.sh` (deps → Prisma → systemd `fl-automate`).
5. **Server C** — scp's the gateway zip, `npm ci` + `prisma generate` +
   `prisma db push`, writes `.env` on first install (grabbing `JWT_PUBLIC_KEY`
   from Server B automatically), upserts any non-empty per-plan routing vars
   (see below), installs + starts the `ai-gateway` unit.
6. **Health checks** — curls both services from inside their boxes.

**Idempotent:** re-running is the update path — remote `.env` files and
databases are never overwritten (it warns if the gateway's JWT key has drifted
from the site's). The one exception: non-empty `GATEWAY_PLAN_*` vars from
`servers.env` are upserted into the gateway's `.env` on every run (see
[per-plan gateway routing](#per-plan-gateway-routing)). Useful flags:

```bash
bash install-all.sh --only site         # redeploy just one part (db|site|gateway)
bash install-all.sh --skip-build        # reuse the newest zips in artifacts/
bash install-all.sh --config other.env  # e.g. a staging cluster
```

## Per-plan gateway routing

Each subscription plan can be routed to its own upstream LLM provider via
`GATEWAY_PLAN_<PLAN>_*` variables in `servers.env`, where `<PLAN>` is one of
`FREE`, `BETA`, `PRO`, `STUDIO`:

| Variable | Meaning |
| --- | --- |
| `GATEWAY_PLAN_<PLAN>_PROVIDER` | `openai` \| `anthropic` \| `mock` |
| `GATEWAY_PLAN_<PLAN>_BASE_URL` | Provider API base URL |
| `GATEWAY_PLAN_<PLAN>_API_KEY` | Provider secret key (server-side only) |
| `GATEWAY_PLAN_<PLAN>_MODEL` | Default model for the plan |
| `GATEWAY_PLAN_<PLAN>_ALLOWED_MODELS` | Comma-separated model allowlist |

A plan with no vars set falls back to the gateway's legacy `UPSTREAM_*` /
`GATEWAY_DEFAULT_MODEL` config (seeded from `GATEWAY_UPSTREAM_*` in
`servers.env`), so leaving the whole block empty keeps single-upstream
behavior. Deploy semantics: on every run (first install *and* re-runs) each
**non-empty** value is upserted into `/opt/ai-gateway/.env` — the existing
line is replaced, otherwise appended — while empty values never touch the
server-side file. This is the one deliberate exception to the ".env is never
modified on re-runs" rule; to *unset* a plan var, edit `/opt/ai-gateway/.env`
on the server and restart `ai-gateway`.

## Windows installer packaging & distribution

The site step also ships the desktop-app installer:

- **`bootstrap/build-and-stage.ps1 -Production`** runs first (needs `cmake` +
  `dotnet`): rebuilds the native DLLs (`version.dll`, `FlClrHost.dll`,
  `FlBridge.dll` — debug pipe OFF) plus the Avalonia plugin closure and syncs
  them into `installer/FruityLink.Installer/payload`. Without it,
  `package.ps1` alone would reuse whatever natives were last staged.
- **`installer/package.ps1`** (run automatically during the build step, or by
  hand with `pwsh -NoProfile -File installer/package.ps1`) publishes
  `installer/FruityLink.Installer` self-contained for win-x64 as a single-file
  exe with the `payload\` tree loose next to it (that is how the exe resolves
  its payload), and zips it to
  `installer/artifacts/fl-automate-installer-v<version>.zip` (`<version>` from
  the csproj). Re-runnable; replaces the zip in place.
- **Upload** — the newest `installer/artifacts/fl-automate-installer-v*.zip`
  is copied to Server B as `/opt/fl-automate/downloads/fl-automate-installer.zip`.
  That stable filename is the contract with the marketing API (`DOWNLOAD_DIR`,
  default `./downloads`), which serves it at
  `https://<site>/api/downloads/installer` (authenticated).
- **Best-effort** — if the payload restage or packaging fails (cmake/dotnet
  busy or missing) the deploy logs a warning and continues: a failed restage
  means the installer may ship a stale native payload; a failed packaging (or
  no artifact) skips the upload entirely. Re-run with `--skip-build --only
  site` after staging + packaging manually. `--skip-build` skips both steps.
- **`SITE_BETA_ACCESS_KEY`** in `servers.env` — static beta key; empty
  disables beta redemption. Written into the site's `.env` as
  `BETA_ACCESS_KEY` on first install, appended later only if the line is
  missing (an existing server-side value is never overwritten).

Not automated (interactive by nature): `cloudflared tunnel login` — the script
prints the tunnel + `make-admin` steps at the end. Fresh installs come up in
**beta mode** (no Helcim key → purchasing disabled); fill in Helcim + email
(the `GRAPH_*` Microsoft Graph vars) in `/opt/fl-automate/.env` and restart
when you start selling.
