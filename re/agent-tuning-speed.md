# Agent Tuning: Speed / Latency (FL Agent)

Research-only. Focus: **per-turn latency + total round-trips** for the FL Agent
(LLM driving FL Studio via `native_*` tools, backend `glm-5.2:cloud`). Sibling
docs own **minimalism** (trimming prompt/tools) and **parallelism** (fewer
round-trips); this doc owns **latency + backend/model** and only *references*
their levers.

Source files studied:
- `src/FruityLink.Llm/ChatKernelFactory.cs` — connector + `HttpClient` chain
- `src/FruityLink.Agent/FlAgent.cs` — execution settings, non-streaming reply path
- `src/FruityLink.Agent/SubAgentService.cs` — same settings on each sub-agent
- `src/FruityLink.Agent/SystemPrompts.cs` — system prompt size (Default + caveman)
- `src/FruityLink.Agent/Plugins/NativeControlPlugin.cs` (+ siblings) — tool-schema size
- `src/FruityLink.Core/Configuration/LlmSettings.cs` — model/endpoint/backend model
- `src/FruityLink.Llm/Diagnostics/LlmRetryHandler.cs` — retry/backoff

---

## TL;DR

The agent is slow for four compounding reasons, in priority order:

1. **It's non-streaming.** `FlAgent.StreamAsync` calls
   `GetChatMessageContentAsync` (one blocking JSON body per turn) — the user
   sees *nothing* until the whole turn finishes. This is the #1 perceived-latency
   problem and the cheapest to fix.
2. **~8K tokens of fixed prefix are re-sent and re-prefilled every turn.**
   ~86 tool schemas (~7K tokens) + system prompt (~1.2K tokens) ride on *every*
   round-trip. On a cloud reasoning model that's real prefill latency each step.
   (Minimalism sibling shrinks this; caching mostly eliminates the re-prefill.)
3. **Many sequential round-trips per task.** Each is a full network RTT +
   queue + prefill + decode + a sequential FL bridge call. (Parallelism sibling
   reduces the count.)
4. **`glm-5.2:cloud` is a reasoning model on shared cloud GPUs** — it emits
   thinking tokens before every tool call and has variable time-to-first-token.
   Reasoning + non-streaming + 8K `MaxTokens` = long, opaque turns.

Biggest wins, in order: **turn on streaming** (perceived speed, free) →
**stabilize the prefix for cache reuse + lower `MaxTokens`** → **suppress/limit
reasoning on tool-only turns** → **trim tools/prompt (minimalism) + parallelize
(parallelism)** → **consider a faster strong-tool-calling backend** (the system
is OpenAI-compatible and endpoint-flexible, so this is a config change).

---

## 1. Latency breakdown — where the time goes

A single agent **turn** today costs roughly:

```
 user/tool input
      │
      ▼
 [ HTTP RTT to cloud ]            network + TLS + retry-handler chain
 [ queue / cold GPU ]             glm-5.2:cloud is shared; TTFT ~0.9–1s+ observed
 [ PREFILL ~8K+ fixed tokens ]    ~86 tool schemas + system prompt, EVERY turn
 [ + prefill growing history ]    prior turns re-sent (stateless API)
 [ DECODE reasoning tokens ]      glm-5.2 "thinks" before answering/calling
 [ DECODE output/tool-call JSON ] capped at MaxTokens=8192
      │  (nothing shown to user until ALL of the above completes — non-streaming)
      ▼
 [ tool auto-invoke, SEQUENTIAL ] SK runs tool calls one-by-one over the FL pipe
      │
      ▼  → next turn repeats the whole stack
```

A multi-step music task ("program a drum pattern + bassline + chords") is
**many** of these turns back-to-back. Total latency ≈
`Σ turns × (RTT + queue + prefill + reasoning + decode + sequential tools)`.

### Quantified per-turn cost (measured/estimated from source)

| Component | Size | Notes |
|---|---|---|
| Tool schemas | **86 tools** (81 `native_*` + `build_chord_progression`/`list_scales`/`get_creative_soul` + `search_knowledge` + `run_parallel_tasks`) | `NativeControlPlugin` alone is 13,023 chars of `[Description]` text before names/params/JSON scaffolding |
| Tool schema tokens | **~6,000–8,000 tokens/turn** | descriptions + 81 names + ~150 params + JSON structure; re-sent every turn |
| System prompt | Default ~2,957 chars + caveman ~1,945 chars ≈ **~1,200 tokens** | `SystemPrompts.BuildDefault()`; caveman is ON by default |
| **Fixed prefix / turn** | **~8,000 tokens** | system + tools, *identical every turn* → ideal cache target |
| `MaxTokens` ceiling | **8,192** (`FlAgent.cs:89`, `SubAgentService.cs:50`) | safety ceiling; also bounds reasoning + a big `native_add_notes` |
| Temperature | **unset** → backend default (often ~1.0) | not a latency lever per se, but see §2 |
| Streaming | **OFF** (`GetChatMessageContentAsync`, `FlAgent.cs:103`) | the dominant *perceived*-latency problem |
| Knowledge/RAG injection | **on-demand only** (`search_knowledge` tool) — not injected every turn | good; no per-turn RAG tax. One extra round-trip when used. |
| Retry handler | 3 attempts, backoff 250/500/1000ms + jitter (`LlmRetryHandler.cs`) | adds latency **only on transient errors**; fine |
| HTTP timeout | 10 min (`ChatKernelFactory.cs:35`) | a safety net, not a tuning knob |

The ~8K-token fixed prefix is the headline number: with ~86 tools it dominates
input on early turns, and it is **re-prefilled on every round-trip** unless the
backend can reuse a KV/prefix cache (see §2, Prompt caching).

---

## 2. Concrete speed changes (this doc's lane)

### A. Turn on streaming — the single biggest perceived-latency win

`FlAgent.StreamAsync` is named "Stream" but is non-streaming: it awaits one
`GetChatMessageContentAsync` and yields the text all at once
(`FlAgent.cs:99–115`). The code comment explains *why* — some OpenAI-compatible
proxies resend full tool-call args in each chunk, which SK concatenates into
invalid JSON (`"{}{}"`) — and the `LlmToolCallRepairHandler` only sees a single
body in non-streaming mode.

That reasoning is real, but the fix is not "stay fully non-streaming forever":

- **Stream only the final assistant text turn** (the turn that produces prose,
  no tool calls). Tool-calling turns stay non-streaming so the repair handler
  keeps working; the user still gets token-by-token output on the part they
  actually read. SK exposes `GetStreamingChatMessageContentsAsync`.
- Or **stream reasoning/"thought" deltas** for live progress while keeping the
  tool-call body buffered. The agent already surfaces a `Thought` delta
  (`AgentDeltaKind.Thought`) — streaming it turns the current dead-air "long
  pause" into visible progress.
- Either way, perceived latency drops sharply even if wall-clock is unchanged.
  This is the highest ROI change in the doc and needs no backend change.

### B. Sensible `MaxTokens` + temperature

- **`MaxTokens=8192` is high.** It's a safety ceiling, but on a *reasoning*
  backend a generous ceiling invites long thinking + verbose turns, and
  non-streaming makes the user wait for all of it. Most turns are a tool call or
  a terse caveman reply. Recommend **splitting the ceiling by role**: keep a
  large cap only where a single turn legitimately emits a big payload
  (`native_add_notes` with a long note list, or a final summary); drop the
  default to **~1,500–2,048** for ordinary tool-calling turns. Lower cap = less
  worst-case decode time and a tighter bound on runaway reasoning.
- **Temperature is unset** → backend default. Latency is roughly
  temperature-independent, so this is a *quality/determinism* knob, not a speed
  knob — but for deterministic tool-argument JSON a low temperature (~0.2–0.4)
  reduces re-tries/self-correction loops, which indirectly cuts round-trips.
  Set it explicitly rather than inheriting an unknown default.

### C. Suppress / limit reasoning on tool-only turns

`glm-5.2` emits thinking tokens before acting (the agent reads them out of
`reply.Metadata` via `ExtractReasoning`). Reasoning is decoded **every turn** and
is pure latency for mechanical steps ("call `native_list_channels`"). Levers:

- If the endpoint exposes a thinking/effort control (Ollama/Z.ai expose
  reasoning toggles for GLM), **disable or minimize reasoning for tool-execution
  turns** and reserve it for planning turns. This can be a per-call setting on
  `OpenAIPromptExecutionSettings.ExtensionData`/`extra_body` depending on what
  the proxy accepts.
- Reasoning depth is the GLM analog of Claude's `effort` — lower it for routine
  agentic steps, raise it for the initial plan.

### D. Prompt caching — stop re-prefilling the 8K fixed prefix

The ~8K-token system+tools prefix is **byte-identical every turn** (it only
changes when the caveman toggle flips or the tool set changes — neither happens
mid-conversation). That is the ideal caching scenario. What's available depends
on the endpoint (the app is OpenAI-compatible and endpoint-flexible):

- **Ollama cloud / vLLM / Z.ai**: automatic **prefix KV-cache reuse** — repeated
  leading tokens "share cached context and use less" (Ollama's own wording).
  Already partly working *as long as the prefix stays stable*. Action: **don't
  mutate the prefix per turn** (stable tool order, stable system text, no
  per-turn timestamps/IDs injected ahead of the history). Keep caveman set once.
- **OpenAI-compatible OpenAI endpoint**: automatic caching, ~50% input discount
  + 13–31% TTFT improvement on prefixes >1024 tokens — no code change.
- **Anthropic (Claude)**: explicit prefix caching gives ~90% input-cost / ~85%
  latency reduction on the cached prefix. Native caching needs Anthropic's API
  shape (`cache_control` on the last system/tool block); via an OpenAI-compatible
  gateway (e.g. OpenRouter) caching is exposed through a passthrough breakpoint.
  Note: cache the **stable system + tools** only, *not* volatile tool results —
  naive full-context caching can *increase* latency.

Caching is the lever that makes the large tool surface tolerable **without**
waiting on the minimalism work — but the two compound: a smaller prefix that is
*also* cached is best.

### E. Reduce round-trips (reference only — siblings own this)

Per-turn latency × number of turns is the real wall-clock. Two sibling levers cut
the multiplier; both are already partly wired:

- **Parallelism**: `FunctionChoiceBehavior.Auto(AllowParallelCalls = true)` is
  already set (`FlAgent.cs:95`), letting one response request several tool calls
  → one LLM round-trip instead of N. `run_parallel_tasks` (`OrchestrationPlugin`)
  fans independent jobs to sub-agents. *Design owned by the parallelism sibling.*
- **Minimalism**: fewer/tighter tool schemas and a shorter prompt shrink the
  ~8K prefix → less prefill per turn and a cleaner tool-choice (fewer wrong-tool
  retries). *Design owned by the minimalism sibling.*

This doc's only ask of them: every token they remove and every round-trip they
collapse multiplies with streaming + caching here.

---

## 3. Backend / model recommendation

The system is **OpenAI-compatible and endpoint-flexible**
(`ChatKernelFactory` routes Ollama/OpenAI/Anthropic-as-proxy through one OpenAI
connector at a configurable `Endpoint`; `LlmSettings`). Switching backends is a
**settings change, not a code change** — so model choice is a live lever.

### Keep `glm-5.2:cloud`?

**Pros:** very cheap (~$0.14/M in, ~$0.28/M out at Z.ai pricing — matters
because the product's only margin is the AI-token spread), strong agentic
tool-calling (GLM-5.x scores at/above frontier on SWE-Bench-Pro-class and
MCP/tool benchmarks), 15% more token-efficient than GLM-4.5.
**Cons:** it's a **reasoning model on shared cloud GPUs** — variable TTFT
(~0.9–1s+ to first token) and throughput that swings 5×+ by provider/load
(34–175 t/s on GLM-5.1; up to ~365–470 t/s only on the fastest dedicated
hosts). For a tight agentic feedback loop, that variance *is* the latency
complaint.

**First move: tune the GLM path before switching.** Streaming + cache-stable
prefix + lower `MaxTokens` + reduced reasoning on tool turns may close most of
the gap at near-zero cost. Also worth testing: a **faster GLM-5.2 host**
(dedicated/quantized providers report ~0.93s TTFT and 360–470 t/s) — same model,
much better latency profile, still cheap.

### Switch to a faster strong-tool-calling model?

Per current model guidance, the strongest agentic tool-callers are the Claude
family (and peers). Objective tradeoffs for an OpenAI-compatible swap:

| Option | Latency profile | Tool-calling | Cost (in/out per 1M) | Notes for this app |
|---|---|---|---|---|
| **GLM-5.2:cloud** (current) | Variable TTFT ~1s; throughput host-dependent (34–470 t/s) | Strong | **~$0.14 / ~$0.28** (Z.ai) | Cheapest; tune first. Reasoning adds per-turn latency. |
| **Claude Haiku 4.5** (`claude-haiku-4-5`) | **Fastest strong option** — ~0.75s TTFT, ~80–120 t/s; built *for* agent loops | Strong (matches older Sonnet on tool use) | **$1 / $5** | Best latency-for-quality on the Claude side; 200K ctx. Likely the right upgrade if GLM tuning isn't enough. |
| **Claude Sonnet 5** (`claude-sonnet-5`) | Mid; higher quality/turn can mean **fewer** turns | Very strong | **$3 / $15** ($2 / $10 intro thru 2026-08-31) | Use when wrong-tool/retry loops are the bottleneck — better planning cuts round-trips. |
| **Claude Opus 4.8** (`claude-opus-4-8`) | Slowest, highest quality; **Fast Mode** ~2.5× t/s at premium price (first-party API only, *not* via OpenAI-compatible proxy) | Strongest | **$5 / $25** | Overkill for mechanical FL edits; reserve for hard planning. |

Cost reality check: Claude is **~7–50× the per-token cost** of GLM-5.2. Since the
business sells only the token-margin, a blanket switch to Claude erodes margin
hard. Two pragmatic shapes:

1. **Tune GLM first; keep it as the default.** Streaming + caching + reasoning
   control likely makes `glm-5.2` (or a faster GLM host) "fast enough" at its
   price. Lowest-risk, margin-preserving.
2. **Tiered / hybrid routing** (the system already supports per-agent backends —
   `SubAgentService` builds its own kernel from settings): route the cheap,
   high-volume *mechanical* tool turns to **GLM-5.2** or **Haiku 4.5**, and the
   occasional *planning/orchestration* turn to **Sonnet 5 / Opus 4.8**. Note:
   Claude **prompt caching, Fast Mode, and adaptive thinking are native-API
   features** — full access wants Anthropic's endpoint (or a gateway that passes
   `cache_control` through), not a bare OpenAI-compatible shim.

**Recommendation:** Do not switch yet. Land **streaming + prefix-stable caching +
lower `MaxTokens` + reduced reasoning on tool turns** on the existing
`glm-5.2:cloud` path first — that addresses the actual complaint (slow, opaque
turns) at zero token-cost. If latency is still unacceptable after that, the
best-value upgrade for a *strong tool-caller built for agent loops* is **Claude
Haiku 4.5** (fast, ~0.75s TTFT, agentic-tuned) — ideally as the cheap tier in a
hybrid, with Sonnet 5/Opus 4.8 reserved for planning, to protect token margin.

---

## Summary for the caller

**Latency breakdown:** per turn = HTTP RTT + cloud queue/TTFT (~1s, variable) +
prefill of a ~8,000-token fixed prefix (~86 tool schemas ≈ 6–8K tokens + ~1.2K
system prompt, *re-sent every turn*) + growing-history prefill + glm-5.2 reasoning
decode + output decode (capped at `MaxTokens=8192`) + **sequential** FL tool
invocation — and the whole turn is **non-streaming**, so the user sees nothing
until it all completes. A music task is many such turns in series.

**Concrete speed changes (this doc):**
1. **Stream** the final text turn (and/or reasoning deltas) — biggest perceived
   win, free; keep tool-call turns buffered so the JSON-repair handler still works.
2. **Lower `MaxTokens`** to ~1.5–2K for ordinary tool turns (keep a large cap only
   for big payloads); set an explicit low-ish **temperature** for stable tool JSON.
3. **Suppress/limit reasoning** on mechanical tool-only turns (GLM's effort analog).
4. **Keep the ~8K system+tools prefix byte-stable** so the backend reuses its
   prefix/KV cache (Ollama/vLLM/OpenAI automatic; Claude explicit ~90%/85%); cache
   stable system+tools only, never volatile tool results.
5. Reference siblings: **minimalism** shrinks the prefix, **parallelism** cuts the
   round-trip count — both multiply with the above.

**Backend/model recommendation:** Keep `glm-5.2:cloud` and **tune it first**
(it's the cheapest, the product sells token margin, and it's a strong tool-caller);
optionally move to a faster GLM-5.2 host for better TTFT/throughput at the same
price. If still too slow, the best-value upgrade is **Claude Haiku 4.5** (~0.75s
TTFT, agentic-tuned, $1/$5) as the cheap tier; reserve **Sonnet 5** ($3/$15) and
**Opus 4.8** ($5/$25, Fast Mode native-API only) for planning turns via hybrid
routing. Avoid a blanket Claude switch — it's ~7–50× GLM's per-token cost and
would gut the margin. The swap is a config change either way (OpenAI-compatible,
endpoint-flexible factory).
