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
from the site's). The one exception: non-empty `GATEWAY_PLAN_*`,
`GATEWAY_BACKEND_*`, and `GATEWAY_UPSTREAM_*` (mapped to `UPSTREAM_*`) vars
from `servers.env` are upserted into the gateway's `.env` on every run (see
[per-plan gateway routing](#per-plan-gateway-routing)). Useful flags:

```bash
bash install-all.sh --only site         # redeploy just one part (db|site|gateway)
bash install-all.sh --skip-build        # reuse the newest zips in artifacts/
bash install-all.sh --config other.env  # e.g. a staging cluster
```

## Per-plan gateway routing

Each subscription plan can be routed to its own upstream LLM provider(s) via
`GATEWAY_PLAN_<PLAN>_*` variables in `servers.env`, where `<PLAN>` is one of
`FREE`, `BETA`, `PRO`, `STUDIO`:

| Variable | Meaning |
| --- | --- |
| `GATEWAY_PLAN_<PLAN>_MODELS` | **Preferred.** CSV of `pseudonym[=upstreamModel[@BACKEND][=Display Label]]`; first entry = plan default. Clients only see the pseudonym + label. Supersedes `_MODEL`/`_ALLOWED_MODELS`. |
| `GATEWAY_PLAN_<PLAN>_PROVIDER` | `openai` \| `anthropic` \| `ollama` \| `mock` \| `none` |
| `GATEWAY_PLAN_<PLAN>_BASE_URL` | Provider API base URL |
| `GATEWAY_PLAN_<PLAN>_API_KEY` | Provider secret key (server-side only) |
| `GATEWAY_PLAN_<PLAN>_MODEL` | Legacy default model for the plan |
| `GATEWAY_PLAN_<PLAN>_ALLOWED_MODELS` | Legacy comma-separated model allowlist |

Reusable named backends (`GATEWAY_BACKEND_<NAME>_{PROVIDER,BASE_URL,API_KEY}`,
any UPPERCASE name) let a single plan offer models from several upstreams —
bind a model with `@NAME` in `_MODELS`. `ollama` is OpenAI-compatible and
needs no API key.

A plan with no vars set falls back to the gateway's legacy `UPSTREAM_*` /
`GATEWAY_DEFAULT_MODEL` config (seeded from `GATEWAY_UPSTREAM_*` in
`servers.env`). Provider `none`/empty means explicitly unconfigured: chat on
that plan returns a 503 "no backend configured" error instead of silent mock
replies — mock only serves when explicitly set. Deploy semantics: on every
run (first install *and* re-runs) each
**non-empty** value is upserted into `/opt/ai-gateway/.env` — the existing
line is replaced, otherwise appended — while empty values never touch the
server-side file. This is the one deliberate exception to the ".env is never
modified on re-runs" rule; to *unset* a plan var, edit `/opt/ai-gateway/.env`
on the server and restart `ai-gateway`.

## Enabling the AI topic guardrail

The gateway ships with a second-model guardrail that keeps conversations
on-topic (music production in FL Studio) and steers or blocks off-topic use —
programming, hacking/piracy, homework, general chatbot traffic — **before** it
burns main-model tokens. It is **off by default**; a deploy with an unchanged
`servers.env` changes nothing.

To enable it, add these to `servers.env` and re-run
`bash install-all.sh --only gateway` (guardrail vars are upserted into
`/opt/ai-gateway/.env` on every run, exactly like the plan-routing vars):

```bash
# Week 1 — shadow mode: evaluates + logs every verdict, never blocks anyone.
GATEWAY_GUARDRAIL_MODE="log"
GATEWAY_GUARDRAIL_BACKEND="OPENAI"      # reuse a GATEWAY_BACKEND_<NAME>_* block
GATEWAY_GUARDRAIL_MODEL="gpt-4o-mini"   # cheap/fast classifier is plenty
```

After watching the verdict log lines (`journalctl -u ai-gateway | grep guardrail`)
for a few days, flip to enforcement:

```bash
GATEWAY_GUARDRAIL_MODE="enforce"
```

What enforcement does per turn: `allow` → untouched; `warn` → a system note is
injected telling the model to redirect the user back to music production;
`block` → the main model is never called and the user gets a polite refusal
(zero token spend, metered as `blocked`); 3 blocks inside 30 minutes → the
conversation is locked for 30 minutes (403 `guardrail_locked`). Guardrail-model
spend is a cost of service — never billed against the user's quota — and if the
guardrail backend itself is down, requests pass through (fail-open) so an
outage can't take down paying customers.

Optional tuning knobs (all have sane defaults; full reference in
`ai-gateway/README.md` → "Topic guardrail" and `ai-gateway/.env.example`):
`GATEWAY_GUARDRAIL_STRIKE_LIMIT` (3), `GATEWAY_GUARDRAIL_STRIKE_TTL_MIN` (30),
`GATEWAY_GUARDRAIL_TIMEOUT_MS` (4000), `GATEWAY_GUARDRAIL_FAIL` (`open`|`closed`),
`GATEWAY_GUARDRAIL_WINDOW` (6 prior messages of context). Instead of
`_BACKEND` you can point the guardrail at its own upstream with
`GATEWAY_GUARDRAIL_PROVIDER` + `_BASE_URL` + `_API_KEY`.

Related, same deploy semantics: `GATEWAY_CHAT_LOG` (`off` | `metadata` |
`full`, default `metadata`) and `GATEWAY_CHAT_LOG_RETENTION_DAYS` (30) control
per-user request logging in the gateway DB. `full` stores compressed
prompts/completions — make sure the ToS/privacy policy covers that before
enabling it.

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
prints the tunnel steps at the end. (Admin tooling lives on the VPN-only Ops
Console, Server D — the public services have no admin surface.) Fresh installs come up in
**beta mode** (no Helcim key → purchasing disabled); fill in Helcim + email
(the `GRAPH_*` Microsoft Graph vars) in `/opt/fl-automate/.env` and restart
when you start selling.
