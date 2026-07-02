# Agent tuning — THINK-BEFORE-TOOL-CALL (reasoning behavior)

Goal: make the FL Agent **reason briefly before each tool call** so it picks the RIGHT and FEWEST
calls — and reconcile that with the just-merged **minimalism** + **parallelism** Operating principles
and the **caveman** output style. Thesis: **reason MORE so you call LESS.** Reasoning lives in the
THINKING channel; it must never reintroduce extra tool calls.

Backend: `glm-5.2:cloud` over the OpenAI-compatible API (Ollama `/v1`, see `LlmSettings` default
`Endpoint = http://localhost:11434/v1`).

**Scope of THIS doc: BEHAVIOR — prompting + the model param that enables/sizes reasoning.** A sibling
owns the thoughts PLUMBING/display (capturing the reasoning channel out of the response and rendering
the `AgentDeltaKind.Thought` delta). I reference that boundary but don't design it.

RESEARCH ONLY — no source edited. Everything below is exact before/after text for the coordinator.

---

## 1. Diagnosis — why it "jumps straight to tool calls"

Files studied: `src/FruityLink.Agent/SystemPrompts.cs`, `src/FruityLink.Agent/FlAgent.cs`,
`src/FruityLink.Llm/ChatKernelFactory.cs`, `src/FruityLink.Core/Configuration/LlmSettings.cs`,
`src/FruityLink.Agent/AgentDelta.cs`.

### Cause 1 (primary): the prompt never tells it to think first
The live `Default` "Operating principles" block (`SystemPrompts.cs` lines 81-116) is already the
merged minimalism+parallelism rewrite. Every bullet is an **action/avoidance rule** —
"DO ONLY WHAT THE USER ASKED…", "Read state ONLY to get a value…", "Don't add preparatory calls…",
"BATCH…", "FAN OUT…". There is **no planning/reasoning step** anywhere: nothing says *think about the
minimal correct set first, THEN act*. The model is told **what not to call** but never told to
**deliberate before calling**. Result: it pattern-matches request → tool and fires, with no visible
plan — exactly the reported symptom.

The `SubAgent` prompt (lines 119-130) has the same gap — all "do the task / batch / report," no
"think first."

### Cause 2: caveman is being read (by a human) as "no reasoning" — but it isn't
`CavemanPrompt` line 33: **"Write reasoning + natural-language replies terse, like smart caveman."**
That sentence *presupposes reasoning exists* — caveman only **compresses** it (drop articles/filler,
fragments, arrow notation). It never says "skip reasoning." The carve-outs (lines 48-57) protect tool
calls/values, so terse reasoning can never corrupt a tool call. **So caveman is already compatible
with think-before-tools** — no contradiction to resolve, only a sentence to lean on. (If reasoning
isn't *visible* today, that's the model-param/plumbing issue in §2-§3, not caveman.)

### Cause 3 (model/transport): is the reasoning channel even on / surfaced?
Two independent things must both be true for visible pre-tool reasoning:

1. **The model must emit reasoning before tool calls.** GLM-5.2 does this **by default** — thinking
   is ON, and its default mode is *interleaved thinking*: it thinks before a tool call and again after
   each tool result before deciding the next step. So at the model level it should already reason
   before tools; you do NOT need a param to *turn it on*.
2. **The reasoning must reach the UI.** `FlAgent.ExtractReasoning` (lines 144-154) only scans
   `reply.Metadata` for a key containing `"reason"`. Whether SK's OpenAI connector populates that from
   Ollama's `reasoning_content` field is the **sibling's plumbing concern**. If the channel isn't
   captured, reasoning *happens* but is invisible — which can read as "jumps straight to tools."

Behavioral takeaway: the lever I own is the **prompt** (make the reasoning deliberate + minimal-focused
and explicitly allowed to be short) plus the **param** that keeps thinking ON and sizes it for *brief*
(not maximal) planning. Surfacing it is the sibling's lane.

---

## 2. The model param — does glm-5.2 need one to reason before tools?

**Short answer: no param is required to ENABLE reasoning — GLM-5.2 reasons before tool calls by
default. The only useful param is to SIZE that reasoning (keep it brief, not maximal) and to make sure
nothing has turned it off.**

GLM-5.2 OpenAI-compatible thinking controls:

| Param | Values | Default | Effect |
|---|---|---|---|
| `thinking` (Z.ai shape) | `{"type":"enabled"}` / `{"type":"disabled"}` | **enabled** | master on/off for the reasoning channel |
| `enable_thinking` (vLLM/SGLang/Ollama shape) | `true` / `false` | **true** | same on/off, different proxies expose this name |
| `reasoning_effort` | `"high"` / `"max"` | **`max`** (deepest) | depth/length of reasoning **when thinking is on** |

Notes that decide the recommendation:
- **Thinking is ON by default** (Z.ai, vLLM, SGLang, and Ollama all default thinking ON for
  thinking-capable models). So the *enable* path needs nothing.
- **`max` is the default and is the DEEPEST/slowest.** For "think *briefly*," `reasoning_effort:"high"`
  is the lighter of the two levels → a short plan-before-tools instead of maximal deliberation. This is
  the one param worth setting, and it directly serves both goals: reasoning still happens (so calls get
  smarter) but stays short (so latency/output tokens stay low — aligns with the speed + caveman work).
- **Do NOT set `thinking:disabled` / `enable_thinking:false`.** That would kill the very reasoning we
  want. (The speed sibling floated "suppress reasoning on tool-only turns" — that conflicts with THIS
  goal. Reconciliation: keep thinking ON but **dial effort down to `high`**, don't switch it off. Brief
  reasoning > no reasoning for correct minimal tool selection.)
- **Transport caveat (Ollama `/v1`):** Ollama's OpenAI-compatible endpoint accepts `reasoning_effort`
  for thinking-capable models (with Ollama's own value constraints) but has documented gaps passing the
  `thinking`/`think` object through `/v1`. The **portable** knob over this endpoint is therefore
  `reasoning_effort`; treat `thinking`/`enable_thinking` as provider-specific fallbacks. Exact wiring
  (adding it to `OpenAIPromptExecutionSettings.ExtensionData` so SK serializes it into the request
  body) is the sibling's PLUMBING task — behaviorally, the ask is just: **leave thinking ON, set
  `reasoning_effort:"high"`.**

**Bottom line on params:** no enabling param needed; optionally set `reasoning_effort:"high"` to get
*brief* pre-tool reasoning rather than the default `max`. The think-before-tools behavior is then
driven by the **prompt** in §3, which works whether or not the depth param is wired.

---

## 3. The prompt addition (the actual fix)

A single new **first bullet** at the top of `Default`'s "Operating principles", so the plan-first
framing governs the action/avoidance bullets that follow. It makes the reasoning (a) exist and be
deliberate, (b) aimed at the MINIMAL correct set (reinforces, never fights, minimalism), (c) aware of
BATCH/FAN-OUT (reinforces parallelism), (d) explicitly caveman-OK and short, and (e) explicitly walled
off from producing extra tool calls or read-backs.

### Where it slots — `SystemPrompts.cs`, `Default`, the "Operating principles:" block

Insert as the **FIRST bullet**, immediately after the `Operating principles:` header (current line 81)
and **before** the existing `- DO ONLY WHAT THE USER ASKED…` bullet (current line 82). Nothing else in
the block changes — minimalism, "Read state ONLY…", "Don't add preparatory calls…", BATCH, FAN OUT,
DON'T OVER-ORCHESTRATE, bridge guidance, creative-soul, and the closing "creative control" bullet all
stay exactly as-is.

### EXACT text to insert (drop-in new first bullet)

```
- THINK FIRST, THEN ACT. Before any tool call, think briefly (caveman ok) about the MINIMAL correct
  set of calls: which NAMED value(s) you must resolve first (e.g. channel name → index), which single
  action(s) the request actually names, and which of those calls are independent so you can BATCH them
  in one turn (or FAN OUT). Then emit exactly those calls — no warm-up, no survey, no read-back. Your
  reasoning lives in your thinking, NEVER as extra tool calls: reason MORE so you call LESS.
```

### Why this reconciles with each sibling block (no contradiction)

- **Minimalism** (`agent-tuning-focus.md`, now live as bullets 1-4): the new bullet is the *method*
  for the existing *rule*. "DO ONLY WHAT THE USER ASKED, with the FEWEST tool calls" tells the model
  the target; "think about the MINIMAL correct set, then emit exactly those calls" tells it *how to
  hit it*. The clause "NEVER as extra tool calls … reason MORE so you call LESS" + "no warm-up, no
  survey, no read-back" explicitly forbids reasoning from regrowing the very calls (`is_available`,
  surveys, confirm-reads) that the minimalism work removed. It cannot reintroduce gratuitous calls —
  it forbids them by name.
- **Parallelism** (BATCH / FAN OUT bullets): the new bullet names "which calls are independent so you
  can BATCH them in one turn (or FAN OUT)," steering the plan toward the existing batching rules
  instead of one-call-per-turn — so thinking *helps* the round-trip count, not hurts it.
- **Caveman** (`CavemanPrompt`): "(caveman ok)" and "briefly" make the thought obey the terse output
  style; CavemanPrompt line 33 ("Write reasoning … terse") already expects reasoning to exist, so this
  is consistent, not additive scope. The thought is in the reasoning channel, which caveman compresses
  but the carve-outs keep tool-call JSON exact — unchanged.
- **Speed** (`agent-tuning-speed.md`): "briefly" + `reasoning_effort:"high"` (§2) keep the thinking
  short, so the plan-step doesn't bloat per-turn latency; brief reasoning that prevents one wrong/extra
  round-trip is a net latency *win*.

### Optional, lower priority: same one-liner for `SubAgent`
Sub-agents also choose tool calls and have no think-first step. To keep them in lock-step, prepend one
terse line to `SystemPrompts.SubAgent` (lines 119-130), e.g. after the first sentence:

```
Think briefly first about the minimal correct calls for THIS task (resolve names → indices, batch
independent calls), then act — reasoning stays in your thinking, never as extra tool calls.
```

Keep it short; the SubAgent prompt is already minimal-by-design.

### Explicitly NOT changed
- `CavemanPrompt`, `BuildDefault()`, the caveman toggle — untouched (already compatible; §1 Cause 2).
- The header and "Authoring notes" — untouched.
- `FlAgent` execution settings beyond the optional `reasoning_effort` knob (§2), and the whole
  thoughts capture/display path — that's the sibling's PLUMBING lane.

---

## 4. Apply-checklist (for the coordinator)
1. **Prompt (the fix):** insert the §3 "THINK FIRST, THEN ACT" bullet as the new FIRST bullet of
   `Default`'s "Operating principles" block (before the existing `- DO ONLY WHAT THE USER ASKED…`).
   No other bullet changes.
2. **Param (optional, brief-reasoning knob):** leave thinking ON; set `reasoning_effort:"high"`
   (not the default `max`) on the request. Wiring it onto `OpenAIPromptExecutionSettings` is the
   sibling's plumbing task; over Ollama `/v1` the portable param is `reasoning_effort`. Do NOT send
   `thinking:disabled` / `enable_thinking:false`.
3. **(Optional) SubAgent:** add the §3 one-liner to `SystemPrompts.SubAgent`.
4. **Leave caveman + BuildDefault + display path untouched** (§3 "NOT changed").
5. **A/B on `glm-5.2:cloud`:** for the minimalism doc's §4 examples, confirm (a) a short plan now
   appears in the Thought channel and (b) tool-call COUNT does **not** rise vs the minimalism baseline
   (it should stay equal or drop). If reasoning still isn't *visible*, that's the sibling's plumbing
   capture (`FlAgent.ExtractReasoning` / `reasoning_content`), not this behavior change.

---

## Summary for the caller

**Param:** none is required to *enable* reasoning — GLM-5.2 thinks before tool calls **by default**
(interleaved thinking, ON). Don't disable it (`thinking:disabled` / `enable_thinking:false` = wrong
direction). The one optional knob is `reasoning_effort:"high"` (instead of the default `max`) to keep
the pre-tool thinking **brief**; over the Ollama `/v1` endpoint `reasoning_effort` is the portable name
(wiring it is the sibling's plumbing lane). The behavior fix is the prompt, which works regardless.

**Exact prompt block** — insert as the new FIRST bullet of `SystemPrompts.Default`'s "Operating
principles" (before `- DO ONLY WHAT THE USER ASKED…`):

```
- THINK FIRST, THEN ACT. Before any tool call, think briefly (caveman ok) about the MINIMAL correct
  set of calls: which NAMED value(s) you must resolve first (e.g. channel name → index), which single
  action(s) the request actually names, and which of those calls are independent so you can BATCH them
  in one turn (or FAN OUT). Then emit exactly those calls — no warm-up, no survey, no read-back. Your
  reasoning lives in your thinking, NEVER as extra tool calls: reason MORE so you call LESS.
```

**Reconciled with caveman/minimalism:** caveman already says "Write reasoning … terse" (reasoning
EXISTS, just short) — "(caveman ok)/briefly" honors it. Minimalism's "FEWEST tool calls" gets its
missing *how* ("think about the minimal set, emit exactly those"), and the "NEVER as extra tool calls
… no warm-up, no survey, no read-back" clause hard-blocks reasoning from regrowing the calls the
minimalism work removed. Parallelism's BATCH/FAN-OUT is named in the plan so thinking lowers round-trips
too. Net: reason MORE (in the thinking channel) so you call LESS.
```
