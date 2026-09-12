# FL Automate — usage billing, margin pricing & dollar quotas (spec)

Business model: profit margin on token usage. Users buy a subscription; their monthly
budget is the subscription's dollar amount; they spend it on any model. We pay backends
(some per-token, some per-GPU-time), charge the budget at `cost / (1 - margin)`, and keep
margin ≥ the configured minimum. Cheap backends (Ollama) → more usage per user dollar.

**Money unit everywhere: integer MICROS of USD** (`1_000_000 micros = $1.00`). Column
suffix `Micros`. Prisma `Int` (caps at ~$2,147 per counter — fine for monthly/user scale).

## 0. NEW TIER: `label` — $100/month

A fourth tier above studio (internal id `label`; display name "Label"). Applies to the
whole tier vocabulary: marketing `TIERS`/`TIER_RANK` (`{free:0,pro:1,studio:2,label:3}`),
plans (`free|beta|pro|studio|label`), gateway `tiers.ts` + `plans.ts` + plan-config
(`GATEWAY_PLAN_LABEL_*` env family), console `Tier` type + TierBadge (give it the magenta
family — pro=brand violet, studio=cyan today; keep whatever mapping exists consistent),
subscription editors, DTO @IsIn lists. Marketing pricing page gets a fourth card
(copy tone from existing cards; "everything in Studio, plus…" — highest budget, priority
framing; do NOT mark it highlighted — studio keeps the highlight). Prices: monthly $100,
yearly per-month price follows the same discount ratio the existing tiers use in
content.ts. Stripe: add `STRIPE_PRICE_LABEL_MONTHLY/_YEARLY` env plumbing exactly like
pro/studio (empty until prices exist in Stripe — purchasing that tier then shows the same
"unavailable" path an unconfigured plan takes today). Seeds: TierBudget label=100_000_000;
PlanPrice label monthly=100_000_000, yearly per content.ts ratio.

## 1. Cost configuration (ENV, gateway — lives next to the model config)

```
# Per-backend cost in $ per MILLION tokens. Single number = same for prompt+completion;
# in=/out= splits it. For time-billed backends (Ollama) this is the ARBITRARY tunable
# knob approximating GPU-time cost per Mtok — the admin calibrates it against reality.
GATEWAY_BACKEND_<NAME>_COST="1.00"                  # or "in=2.50,out=10.00"

# Per-model overrides (semicolon-separated; model matched against the UPSTREAM model
# name; '*' suffix glob allowed). Wins over the backend default.
GATEWAY_MODEL_COSTS="OPENAI/gpt-4o=in=2.50,out=10.00;OPENAI/gpt-4o-mini=in=0.15,out=0.60;OLLAMA/qwen3.5*=1.50"

# Safety net for UNPRICED models (no backend COST, no override): never serve for free.
GATEWAY_COST_FALLBACK="5.00"
```

On top of the env family sits the DB layer: `CostOverride` rows (§2), edited live
from the Ops Console Pricing page (~30s gateway cache, no restart). Resolution
order for a request routed to backend B / upstream model M:
DB exact `B/M` → exact `B/M` in GATEWAY_MODEL_COSTS → longest glob match `B/M*`
→ DB backend B → `GATEWAY_BACKEND_B_COST` → DB fallback → `GATEWAY_COST_FALLBACK`
(log a warn once per model). Env parsed once at boot; DB rows read live.

## 2. Dynamic business config (DB, edited live from the Ops Console)

### Gateway DB — new models (ADD to ai-gateway/prisma/schema.prisma; console mirrors verbatim)

```prisma
/// Singleton business knobs, editable from the VPN-only Ops Console (id = "current").
/// The gateway reads it per-request with a short in-process cache (~30s).
model BillingConfig {
  id           String   @id @default("current")
  minMarginBps Int      @default(5000) // profit margin in basis points; 5000 = 50%
  updatedAt    DateTime @updatedAt
}

/// Per-tier monthly dollar budgets (what a subscription buys). Seeded on gateway boot
/// if missing. Edited from the Ops Console.
model TierBudget {
  tier         String   @id // free | pro | studio | label
  budgetMicros Int
  updatedAt    DateTime @updatedAt
}

/// Resolved pricing catalog, upserted by the gateway ON BOOT from env (id="current").
/// Read-only for the console (shows the admin what the env config resolves to).
model PricingSnapshot {
  id        String   @id @default("current")
  json      String   // JSON: see §5
  updatedAt DateTime @updatedAt
}

/// Admin-editable per-token COST rates — the DB layer over the §1 env config.
/// Rates in DOLLARS per Mtok. scope/key: model -> "BACKEND/upstreamModel" (exact,
/// no globs; "" backend = plan-level provider); backend -> "BACKEND"; fallback ->
/// "fallback". Resolution order: see §1. Read live by the gateway (~30s cache).
model CostOverride {
  key        String   @id
  scope      String   // model | backend | fallback
  inPerMtok  Float
  outPerMtok Float
  updatedAt  DateTime @updatedAt
}
```

Boot seeds (create-if-missing only): BillingConfig{minMarginBps:5000}; TierBudget
free=500_000 ($0.50), pro=20_000_000 ($20), studio=50_000_000 ($50), label=100_000_000 ($100).

### Gateway DB — extend existing models (additive; `db push` safe)

```prisma
model UsageCounter {  // ADD:
  costMicros    Int @default(0)  // what the backend cost us this period
  chargedMicros Int @default(0)  // what we debited the user's budget
}
model UsageEvent {    // ADD:
  backend       String? // named backend (e.g. OLLAMA) that served it
  costMicros    Int @default(0)
  chargedMicros Int @default(0)
}
model LimitOverride { // ADD:
  budgetMicros  Int?   // per-account monthly DOLLAR budget override (new world)
  // monthlyTokens stays but is LEGACY — no longer enforced.
}
```

### Marketing DB — new model (ADD to marketing/api/prisma/schema.prisma; console mirrors)

```prisma
/// Subscription display prices, editable from the VPN-only Ops Console. The pricing
/// page reads these via GET /api/plans. NOTE: Stripe charges existing subscribers per
/// its own price amounts — keep the Stripe price catalog in sync when changing these.
model PlanPrice {
  tier          String   @id // free | pro | studio | label
  monthlyMicros Int
  yearlyMicros  Int      // per-month price when billed yearly
  updatedAt     DateTime @updatedAt
}
```

Seeded on marketing boot (create-if-missing) from the current static values in
`marketing/web/src/data/content.ts` (pro monthly $20, studio monthly $50, yearly = the
values in that file; free = 0).

## 3. Formulas (single source of truth)

```
margin      = minMarginBps / 10000                      (0.5 default)
priceIn/Mtok  = costIn  / (1 - margin)                  (margin defined on PRICE)
priceOut/Mtok = costOut / (1 - margin)
costMicros    = round(promptTok * costIn  + completionTok * costOut)   // per-Mtok rates
chargedMicros = round(promptTok * priceIn + completionTok * priceOut)  // scaled /1e6
effectiveBudget(user) = LimitOverride.budgetMicros ?? TierBudget[tier].budgetMicros
overBudget    = periodChargedMicros >= effectiveBudget   (pre-flight, same as today)
```

Margin is on price ((price−cost)/price), so 50% margin ⇒ price = 2× cost. Achieved
margin in analytics = (charged − cost) / charged.

## 4. Gateway behavior changes

- **PricingService** (new): parses §1 env at boot; exposes `rateFor(backend, upstreamModel)
  -> {costInPerMtok, costOutPerMtok, priceInPerMtok, priceOutPerMtok, source}` reading
  margin live from BillingConfig (30s cache). Upserts PricingSnapshot on boot (§5 JSON).
- **Metering**: `recordUsage(...)` gains backend+upstream model; computes cost/charged
  micros via PricingService; increments the new UsageCounter fields atomically in the
  existing transaction; stamps UsageEvent.
- **QuotaGuard**: enforcement switches from tokens to `chargedMicros >= effectiveBudget`.
  The 402 payload keeps its existing shape/fields but the numbers become budget-based:
  `{ error:'quota_exceeded', message, budgetMicros, usedMicros, remainingMicros,
  periodKey }` (drop limit/used token fields, they were admin-only consumers). The FL
  plugin only surfaces the message string — verify no other client parses the old fields.
- **GET /v1/usage** (self-service): KEEP all existing token fields (plugin compat), ADD
  `budgetMicros, chargedMicros, remainingMicros, costUnit:'usd-micros'`.
- GATEWAY_LIMIT_* env + tierLimit() become legacy: no longer enforced (keep parsing so
  old envs don't crash; log once that token limits are superseded).

## 5. PricingSnapshot JSON (contract for the console)

```ts
{
  generatedAt: string,          // ISO
  minMarginBpsAtBoot: number,   // informational; live value is in BillingConfig
  costFallbackPerMtok: number,  // dollars
  models: Array<{
    plan: string,               // free|beta|pro|studio
    pseudonym: string, label: string,
    backend: string | null, upstreamModel: string,
    costInPerMtok: number, costOutPerMtok: number,   // DOLLARS per Mtok
    costSource: 'model-override' | 'model-glob' | 'backend' | 'fallback',
  }>
}
```
(Console computes display prices live: price = cost/(1−margin) with the CURRENT DB margin.)

## 6. Ops Console — API contract additions (all SessionGuard'd, audit-logged mutations)

```ts
GET /api/pricing -> {
  snapshot: <parsed §5 JSON> | null, snapshotUpdatedAt: string | null,
  minMarginBps: number,
  tiers: Array<{ tier: 'free'|'pro'|'studio'|'label', budgetMicros: number,
                 monthlyMicros: number | null, yearlyMicros: number | null }>, // PlanPrice may be unseeded -> null
}
PUT /api/pricing/margin  { minMarginBps: int 0..9000 }        -> updated GET payload   // audit 'pricing.margin.set'
PUT /api/pricing/budget  { tier, budgetMicros: int >= 0 }     -> updated GET payload   // audit 'pricing.budget.set'
PUT /api/pricing/plan-price { tier, monthlyMicros >= 0, yearlyMicros >= 0 } -> updated  // audit 'pricing.plan-price.set' (writes marketing PlanPrice upsert)

// Per-token cost rates (gateway CostOverride upsert; live on the gateway ~30s).
// GET /api/pricing additionally returns costOverrides: Array<{ key, scope, inPerMtok,
// outPerMtok, updatedAt }>, and snapshot.models comes back with the live rows already
// overlaid (costSource then one of 'db-model'|'db-backend'|'db-fallback').
PUT /api/pricing/cost { scope: 'model'|'backend'|'fallback', backend?, model?,
                        inPerMtok >= 0, outPerMtok >= 0 }    -> updated GET payload   // audit 'pricing.cost.set'
PUT /api/pricing/cost/clear { scope, backend?, model? }      -> updated GET payload   // audit 'pricing.cost.clear' (env config applies again)

// users: budget override replaces the token override in the UI
PUT /api/users/:id/limit  body becomes { budgetMicros: number | null, note?: string }
  // null -> delete override row entirely (as before). Writes LimitOverride.budgetMicros
  // (monthlyTokens left null/legacy). Audit 'user.limit.set' with dollars in detail.

// analytics: dollar fields added (names exact)
overview   += { chargedMicros, costMicros, profitMicros, marginPct }        // current period, from UsageCounter sums (marginPct = profit/charged*100, 0 if no spend)
timeseries += per-day { chargedMicros, costMicros }                         // from UsageEvent sums
top        += per-row { chargedMicros, costMicros }
models     += per-row { chargedMicros, costMicros }
recent     += per-row { chargedMicros, costMicros, backend }
UserRow.usage += { chargedMicros }; UserRow.limit becomes budget DOLLARS-based:
  { budgetMicros, remainingMicros, overBudget } replace {limit, remaining, overLimit}
  (tokens stay in usage for display).
UserDetail.limitOverride becomes { budgetMicros, note, updatedAt } | null.
```

## 7. Ops Console — UI

- New nav page **Pricing** (route /pricing): (a) editable "Minimum profit margin" (%
  input, stored bps) with a formula hint "price = cost ÷ (1 − margin)"; (b) per-tier
  table: subscription price monthly/yearly (editable, dollars) + monthly budget
  (editable, dollars) + a "match budget to monthly price" convenience button; the
  Stripe caveat note under the price editor; (c) model catalog table from the snapshot:
  plan, model label, backend, upstream, cost in/out per Mtok, LIVE computed price in/out
  per Mtok, margin source badge ('backend'/'override'/'fallback' — fallback in amber
  with "unpriced model" warning); snapshot age shown ("as of <relative time>" — it
  refreshes on gateway restart).
- Dashboard: stat cards gain a "Spend this period" card ($ charged) with cost+profit
  sub-line; keep token cards.
- Analytics: spend/cost overlay on the timeseries (or a second chart), dollar columns in
  top consumers/models/recent tables; formatMoney helper (micros -> "$12.34", compact
  "$1.2k" where formatNumber is used today).
- Users list: tokens-vs-limit progress bar becomes budget progress ($ used of $ budget);
  UserDetail usage card shows dollars + tokens; Limit override card becomes "Budget
  override" in dollars (input in dollars, stored micros).

## 8. Marketing site

- `GET /api/plans` (public, no auth): `{ plans: [{ tier, monthlyMicros, yearlyMicros }] }`.
- Pricing section (web) loads prices from /api/plans on mount; falls back silently to
  the current static content.ts values on error (SSR-less SPA — brief static flash OK).
- Boot seed of PlanPrice as in §2.

## 9. Non-goals / cautions

- Do NOT touch the Stripe integration; price changes here are display+budget only.
- Do NOT enforce budgets in marketing; enforcement is gateway-only.
- Mirrors: admin-console/api/prisma/{marketing,gateway}.prisma MUST byte-match the new
  models/fields above. Console never `db push`es the mirrors.
- Keep every existing token metric flowing (analytics continuity) — dollars are added,
  tokens are not removed.
- Windows dev; typecheck+build must pass per project. Gateway test suite must pass.
