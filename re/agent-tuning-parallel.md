# Agent tuning: parallelism + self-orchestration

Goal: make the FL Agent (LLM driving FL Studio via `native_*`, backend `glm-5.2:cloud`) work in
parallel instead of one-tool-at-a-time:

- **(a) Batch** several *independent* tool calls into ONE assistant turn → 1 inference round-trip
  instead of N.
- **(b) Fan out** genuine multi-part jobs to its own sub-agents via `run_parallel_tasks` → many
  LLM inferences overlap.
- **WITHOUT** over-orchestrating a single trivial action.

RESEARCH ONLY. This doc proposes changes; it does not make them. Sibling docs cover MINIMALISM
(fewest calls) and SPEED/backend — see *Overlap with siblings* at the end. This doc owns
PARALLELISM + self-orchestration only.

---

## TL;DR — what blocks parallelism

1. **Axis (a) is mechanically enabled but the model isn't being pushed to use it.**
   `AllowParallelCalls = true` is set in all three execution-settings sites
   (`FlAgent.cs:95`, `SubAgentService.cs:51`, `ChatBridgeService.cs:110`). That ALLOWS glm to emit
   multiple `tool_calls` in one response, but it does not *force* it — and GLM-family models are
   conservative: by default they emit one tool call per turn unless the **prompt explicitly tells
   them to batch**. The current prompt only says "use `native_add_notes` (ONE call)" for notes; it
   never states the general rule "batch ALL independent tool calls into one turn."
   → *Blocker = prompt, not code.*

2. **Axis (b) already runs sub-agents truly concurrently** (`OrchestrationPlugin.cs:35-42`,
   `Task.WhenAll` + `SemaphoreSlim` cap). The plumbing is correct and fan-out is **safe** (see #4).
   The blocker is again the **prompt**: `run_parallel_tasks` is offered ("PREFER … for multi-part
   jobs", `SystemPrompts.cs:86`) but the *when to / when NOT to* boundary is soft, so glm either
   under-uses it (does the parts itself, sequentially) or risks over-using it on trivial requests.

3. **SK invokes a turn's tool calls SEQUENTIALLY by design, and that is fine here.** SK has two
   independent flags ([MS docs](https://learn.microsoft.com/en-us/semantic-kernel/concepts/ai-services/chat-completion/function-calling/function-choice-behaviors)):
   - `AllowParallelCalls` — model MAY *choose* several functions per round-trip (set = `true`).
   - `AllowConcurrentInvocation` — SK *executes* those chosen calls concurrently (default = `false`
     → executed one-by-one). **Not set anywhere in this codebase.**
   The batching win in (a) comes purely from `AllowParallelCalls` (N calls returned in ONE message
   → SK runs all N, then ONE more inference). Whether SK runs those N sequentially or concurrently
   does **not** change the inference count, and the FL bridge serializes writes anyway (#4), so
   leaving `AllowConcurrentInvocation` at its default is correct — see "do NOT flip" below.

4. **The FL bridge is single-client and already serializes everything**, so parallel sub-agents are
   safe. `FlInjectBridge._pipeGate = new SemaphoreSlim(1, 1)` (`FlInjectBridge.cs:48`) gates every
   command (`RawAsync`, `FlInjectBridge.cs:67-94`), and `INativeFlControl` is registered
   **singleton** (`ServiceConfiguration.cs:59`), as are `NativeControlPlugin` (`:66`) and
   `SubAgentService` (`:67`). So all sub-agents share ONE bridge + ONE gate; their FL writes
   serialize, only their *LLM inference* overlaps. On slow glm-5.2 the inference is the bottleneck,
   so overlapping it is exactly the win.

**Net:** the engine is already wired for parallelism on both axes and is safe. The thing not pulling
its weight is the **system prompt** (and `run_parallel_tasks`' tool description). The one optional
code change worth making is a higher default sub-agent cap; everything else is prompt text.

---

## How it works today (the two parallelism axes)

### Axis (a): multiple tool calls in one turn

`FlAgent.StreamAsync` builds settings and makes a single non-streaming call:

```csharp
// FlAgent.cs:84-105
var settings = new OpenAIPromptExecutionSettings
{
    MaxTokens = 8192,
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(
        options: new FunctionChoiceBehaviorOptions { AllowParallelCalls = true }),
};
ChatMessageContent reply = await _chat!
    .GetChatMessageContentAsync(_history, settings, _kernel, ct)   // SK runs the FULL auto-invoke loop here
    .ConfigureAwait(false);
```

`GetChatMessageContentAsync` with `Auto` runs SK's auto-invoking function-calling loop internally:
model → tool_calls → SK invokes them → model again, repeating until the model returns text (default
cap 128 attempts). If glm returns 3 `tool_calls` in one message, SK invokes all 3 (sequentially)
before the next inference. So batching = fewer inferences = the main latency win on glm-5.2.

Identical settings exist in `SubAgentService.cs:46-53` and `ChatBridgeService.cs:107-112` (the in-FL
chat tab). All three already set `AllowParallelCalls = true`.

The code comment at `FlAgent.cs:92-94` is accurate and worth keeping: *"SK still auto-invokes them
SEQUENTIALLY, so the single-client FL bridge is never called concurrently."* That is the intended,
safe behavior — sequential **invocation** of a batched turn, not sequential **turns**.

### Axis (b): self-orchestration via `run_parallel_tasks`

```csharp
// OrchestrationPlugin.cs:30-42 — runs sub-agents CONCURRENTLY (correct)
int max = Math.Clamp(app.MaxSubAgents, 1, 64);   // default MaxSubAgents = 10 (AppSettings.cs:24)
using var gate = new SemaphoreSlim(max);
var runs = tasks.Select(async (task, i) =>
{
    await gate.WaitAsync(ct);
    try { results[i] = await subAgents.RunTaskAsync(task, ct); }
    catch (Exception ex) { results[i] = $"FAILED: {ex.Message}"; }
    finally { gate.Release(); }
}).ToList();
await Task.WhenAll(runs);   // genuine concurrency, capped at `max`
```

Each `SubAgentService.RunTaskAsync` (`SubAgentService.cs:27-58`) builds its **own** kernel + history,
registers MusicTheory / NativeControl / Knowledge (but **not** OrchestrationPlugin — sub-agents
can't recurse), and runs its own full auto-invoke loop. So N sub-agents = N overlapping glm loops,
all writing through the one shared, gated bridge. **This is the part that actually parallelizes real
work and it is already implemented correctly.**

---

## Diagnosis (what to change, and what not to)

| Symptom | Root cause | Fix |
|---|---|---|
| Agent does multi-part jobs one tool at a time | Prompt never says "batch independent calls"; glm defaults to 1 call/turn | **Prompt** (Axis a block below) |
| Agent rarely calls `run_parallel_tasks`; does parts itself serially | "PREFER" guidance is soft on *when*; glm under-fans-out | **Prompt** (Axis b block) + sharpen tool description |
| Risk: over-orchestrating a single edit | No explicit "don't fan out a single action" rule | **Prompt** (explicit carve-out) |
| Only 10 sub-agents at once | `MaxSubAgents = 10` default | Optional: raise default to ~16 (code) |
| (Non-issue) SK runs batched calls sequentially | By design; bridge serializes anyway | **Do NOT** set `AllowConcurrentInvocation` |
| (Non-issue) bridge races under fan-out | Shared singleton `_pipeGate` already serializes | none |

### Why NOT to flip `AllowConcurrentInvocation = true`

It only changes how SK *executes* the calls a single turn returned. The FL bridge's `_pipeGate`
(`FlInjectBridge.cs:48`) serializes every command regardless, so concurrent invocation buys ~nothing
for FL writes while adding a (gated, but pointless) concurrency surface. The inference count — the
real cost on glm-5.2 — is unchanged. Leave it default. Real concurrency comes from
`run_parallel_tasks` (separate kernels, overlapping inference), which is the right layer for it.

### glm-5.2 reality check

GLM models support the OpenAI `parallel_tool_calls` request field (SK maps `AllowParallelCalls=true`
onto it), and the team is already running with it set, so the endpoint accepts it. But GLM-family
models are **more conservative than GPT-4-class models about emitting multiple `tool_calls` in one
turn** — they tend toward one call per turn unless the prompt explicitly and repeatedly pushes
batching. Practical implication: **don't rely on spontaneous Axis-(a) batching alone.** The robust,
model-controlled lever is Axis (b) `run_parallel_tasks`, where the *prompt* tells glm to decompose
and the *code* guarantees the concurrency. Lean on (b) for anything genuinely multi-part; treat (a)
batching as a bonus that the prompt should still request.

(References: [SK Function Choice Behaviors](https://learn.microsoft.com/en-us/semantic-kernel/concepts/ai-services/chat-completion/function-calling/function-choice-behaviors),
[SK Function Invocation](https://learn.microsoft.com/en-us/semantic-kernel/concepts/ai-services/chat-completion/function-calling/function-invocation),
[GLM-4.6 tool-calling analysis](https://cirra.ai/articles/glm-4-6-tool-calling-mcp-analysis),
[Z.AI tool docs](https://docs.z.ai/devpack/tool/others).)

---

## Proposed fixes

### 1. Execution settings — keep `AllowParallelCalls = true`, do NOT add concurrent invocation

No change required to the three settings sites; they are already correct. Confirm they read exactly:

```csharp
// FlAgent.cs:84-97, SubAgentService.cs:46-53, ChatBridgeService.cs:107-112 — KEEP AS-IS
var settings = new OpenAIPromptExecutionSettings
{
    MaxTokens = 8192,
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(
        options: new FunctionChoiceBehaviorOptions
        {
            AllowParallelCalls = true,        // model may batch tool calls in one turn (Axis a)
            // AllowConcurrentInvocation: leave UNSET (default false).
            // FL bridge serializes via _pipeGate; concurrent invoke buys nothing, adds risk.
        }),
};
```

The only optional code change: raise the default fan-out cap so big jobs aren't throttled to 10.

```csharp
// AppSettings.cs:24 — optional
int MaxSubAgents = 16,   // was 10; OrchestrationPlugin still clamps to [1,64]
```

(10 is already reasonable; bump only if users routinely request >10 independent parts. Higher = more
concurrent glm inferences in flight = more tokens/sec billed and more pressure on the serialized
bridge gate, so 16 is a safe ceiling, not 64.)

### 2. `OrchestrationPlugin` / `SubAgentService` — already concurrent, leave the mechanics

`Task.WhenAll` + capped `SemaphoreSlim` is correct (`OrchestrationPlugin.cs:32-42`); sub-agents use
isolated kernels (`SubAgentService.cs:33-37`); the shared singleton bridge gate makes it safe
(`ServiceConfiguration.cs:59`, `FlInjectBridge.cs:48`). **No concurrency change needed.** The only
edit here is wording — make the tool's `[Description]` sharper so glm reaches for it (below).

### 3. Prompt changes (the real fix)

#### 3a. `SystemPrompts.Default` — replace the current "PREFER run_parallel_tasks" bullet

Current (`SystemPrompts.cs:85-87`):

> - PREFER run_parallel_tasks for multi-part jobs: inspect state for real indices, decompose into
>   independent self-contained tasks (one per pattern/instrument, each naming its own pattern +
>   channel(s) so they don't conflict), run in parallel. Small/final tweaks → do directly.

Replace with this **PARALLELISM** block (drop-in; caveman-style terse to fit the existing prose):

```
Operating principles — PARALLELISM (work fast, never serial when parts independent):
- BATCH: in ONE turn emit ALL tool calls that don't depend on each other (e.g. add a channel +
  set its mixer track + author its notes; or read patterns + read channels + read ppq together).
  One inference, many calls — not one call per turn. Only chain across turns when a call's args
  truly depend on a previous call's RESULT (e.g. need the real channel index before adding notes).
- FAN OUT: for a job with multiple INDEPENDENT parts — several patterns, instruments, tracks, or
  song sections — call run_parallel_tasks with one self-contained task per part. Each task names its
  OWN pattern + channel(s) so tasks never touch the same target. They run in parallel; you get all
  results back at once. First inspect real state (list channels/patterns) so each task carries the
  right indices/names.
- DON'T OVER-ORCHESTRATE: a SINGLE simple action (one edit, one read, one tweak, a final
  adjustment) → just call the tool(s) directly. Never wrap one action in run_parallel_tasks. Rule
  of thumb: 2+ genuinely independent multi-step parts → fan out; otherwise act directly (batching
  independent calls into the turn where you can).
```

Keep the existing note-authoring guidance (`SystemPrompts.cs:74-75`: "Multiple notes →
native_add_notes (ONE call …)") — it's a concrete instance of BATCH and reinforces the rule.

#### 3b. `run_parallel_tasks` `[Description]` — tighten the fan-out boundary

Current ends with "Prefer over doing many independent edits yourself." (`OrchestrationPlugin.cs:23`).
Append an explicit lower bound so glm doesn't fire it for trivial work:

```
… Prefer over doing many independent edits yourself. Use ONLY for 2+ genuinely independent,
multi-step parts (e.g. distinct patterns/instruments/sections); for a single action or a couple of
quick edits, just call the native_* tools directly instead.
```

#### 3c. `SystemPrompts.SubAgent` — add a one-line batch reminder

Sub-agents have no orchestration tool (correct — no recursion) but should still batch within their
one task. Append to `SystemPrompts.cs:107`:

```
Batch independent tool calls into one turn where you can (e.g. native_add_notes once for all notes);
only chain across turns when an argument depends on a prior result.
```

(No fan-out instruction for sub-agents — they must not orchestrate.)

---

## Fallback if glm-5.2 won't emit parallel tool calls reliably

If Axis-(a) batching stays weak in practice (glm keeps returning one `tool_call` per turn even with
the prompt), do NOT fight the model with connector hacks. Instead:

1. **Lean entirely on Axis (b).** It's model-instructed but code-guaranteed: as long as glm calls
   `run_parallel_tasks` *once* with a list, the concurrency is ours, not the model's. Make the
   Default prompt bias toward fanning out (the 3a block already does; you can strengthen "FAN OUT"
   to "for ANY job with 2+ independent parts, your FIRST move after inspecting state is
   run_parallel_tasks"). Each sub-agent then only needs to batch *within* its own single task, which
   is a much lower bar for the model.
2. **Connector knob (only if the endpoint rejects the field):** if a future endpoint 400s on
   `parallel_tool_calls`, set `AllowParallelCalls = null` (omits the field, uses provider default)
   rather than `false`. Leaving it `true` is fine for current z.ai/glm since it's already running.
   There is no SK setting that *forces* a model to batch — batching is always the model's choice;
   `run_parallel_tasks` is the deterministic substitute.
3. Do not set `AllowConcurrentInvocation = true` as a "fix" — it doesn't make glm emit more calls;
   it only changes execution of calls glm already chose, and the bridge serializes regardless.

---

## Verification (after applying the prompt changes)

- Multi-part request ("make a kick pattern, a bassline in pattern 2, and a chord prog in pattern 3")
  → expect ONE `run_parallel_tasks` call with 3 tasks, sub-agent tool calls interleaving in the UI
  (`ToolCallFilter.Invoked` fires from all sub-agents — `SubAgentService.cs:34`).
- Single request ("nudge the snare velocity up") → expect a direct `native_*` call, NO
  `run_parallel_tasks`.
- Compound-but-dependent read+write ("add a Piano channel then write a C-major chord on it") →
  expect inspect → batched where possible; the add-channel result feeds the note call (legit
  cross-turn chain, not a regression).
- Because the bridge is gated, fan-out must never produce interleaved/corrupt writes — confirm via
  the operation audit (`OperationAuditSink`) that commands are still well-ordered under load.

---

## Overlap with siblings (don't design theirs)

- **MINIMALISM doc (fewest calls):** there's natural tension — batching (this doc) reduces *turns*
  but a minimalism pass may reduce *total calls*. They're compatible: "batch the calls you do make
  into one turn" ≠ "make more calls." If both docs touch `SystemPrompts.Default`, merge the
  PARALLELISM block with their minimalism bullet rather than appending twice. Flagging the shared
  file; not resolving their wording here.
- **SPEED/backend doc:** `MaxTokens = 8192`, non-streaming choice (`FlAgent.cs:99-105`), the retry/
  repair handler chain (`ChatKernelFactory.cs:31-35`, `LlmToolCallRepairHandler.cs`), and
  glm/endpoint/model selection are theirs. This doc only relies on those existing; the one shared
  knob is `MaxSubAgents` (more concurrent sub-agents = more tokens/sec, a cost/speed tradeoff they
  may want to weigh in on).
- Shared files to coordinate on: `SystemPrompts.cs` (all three docs likely edit `Default`),
  `AppSettings.cs` (`MaxSubAgents`), and the three execution-settings sites.
