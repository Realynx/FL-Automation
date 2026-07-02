# Caveman-speak output mode — integration notes

Goal: make the **model's own reasoning + prose output** terse "caveman speak" to cut
output tokens (SaaS margin) WITHOUT breaking tool-calling. This is distinct from the earlier
"caveman TRIM" that shortened *tool descriptions* (input side, -26%). This task = OUTPUT side.

Source skill: `https://www.skills.sh/mattpocock/skills/caveman` (mattpocock). Fetched 2026-06-30.

## Distilled caveman rules (verbatim-ish from the skill)

Core directive: "Respond terse like smart caveman. All technical substance stay. Only fluff die."

Drop:
- Articles (a / an / the)
- Filler (just / really / basically / actually / simply)
- Pleasantries (sure / certainly / of course / happy to)
- Hedging language + most conjunctions

Keep:
- Technical terms exact
- Code blocks unchanged
- Errors quoted exact

Style:
- Fragments instead of complete sentences
- Short synonyms (big not extensive; fix not implement)
- Abbreviate common terms (DB, auth, config, req, res, fn, impl)
- Arrow notation for causality: `X → Y`
- One word suffices when one word works

Pattern example:
- AVOID: "Sure! I'd be happy to help you with that. The issue you're experiencing is likely caused by..."
- USE: "Bug in auth middleware. Token expiry check use `<` not `<=`. Fix:"

Carve-outs (skill's own): security warnings + irreversible actions "temporarily revert to clarity";
code blocks unchanged.

Activation in the original skill is trigger-phrase based ("caveman mode", "less tokens", `/caveman`)
and persists until "stop caveman" / "normal mode". We instead bake it in as an always-on
(default) composable prompt fragment with a code toggle (see below) — better fit for a SaaS where
we want the savings by default and A/B control, not per-user phrases.

## What I changed (all under `src/FruityLink.Agent/`)

1. `SystemPrompts.cs`
   - Added `public static bool CavemanModeEnabled = true;` — the A/B toggle, default ON.
   - Added `public const string CavemanPrompt` — the delimited caveman OUTPUT-STYLE block. Applies
     to prose/reasoning only; has explicit CARVE-OUTS keeping tool names / argument keys / JSON
     values / numeric literals (indices, MIDI keys, PPQ, normalized params) / user-given names exact,
     and reverting to plain language for safety/irreversible actions.
   - Added `public static string BuildDefault()` = `Default` (+ `"\n\n" + CavemanPrompt` when enabled).
     `Default` and `SubAgent` consts left untouched (back-compat).

2. `FlAgent.cs` — both `AddSystemMessage(SystemPrompts.Default)` sites (configure + ResetConversation)
   now call `SystemPrompts.BuildDefault()`.

3. `ChatBridgeService.cs` — its `AddSystemMessage(SystemPrompts.Default)` now calls `BuildDefault()`.

Both agents share `BuildDefault()`, so the toggle drives them in lock-step.

## How to toggle

- Code/A-B: set `FruityLink.Agent.SystemPrompts.CavemanModeEnabled = false;` before an agent first
  seeds its history (i.e. before first `Configure`/turn). Default is `true` (caveman ON).
- Existing conversations keep whatever system prompt they were seeded with; toggle affects new
  histories (and `FlAgent.ResetConversation`).

## Why tool-calling stays intact

- Caveman block governs PROSE only; carve-out explicitly forbids cavemanizing tool names, argument
  keys, JSON values, and numeric literals — "Emit tool calls exactly as the function schema demands."
- Tool calls are structured function-call objects produced by Semantic Kernel's
  `FunctionChoiceBehavior.Auto`, separate from assistant prose. Prompt style text cannot reshape the
  JSON the connector serializes.
- We added NO new format/triggers that could confuse the function-call channel (prior backends had
  flaky tool-calls; kept it minimal). `FlAgent` is deliberately non-streaming so tool-call JSON
  arrives in one body — unchanged.

## Expected token savings

Caveman compression targets assistant prose + visible reasoning, typically ~30-50% fewer output
tokens on explanatory turns (filler/articles/pleasantries are a large fraction of chat prose). Pure
tool-execution turns (little/no prose) save little — already terse. Net output-token reduction is
workload-dependent; chat-heavy/explanatory use benefits most. Output tokens are the pricier side on
most backends, so margin impact > the raw % suggests. A/B via `CavemanModeEnabled` to measure real
deltas against `glm-5.2:cloud`.

## Follow-ups / out of scope

- Config home: a proper user/Core setting (e.g. `AppSettings.Llm.CavemanMode`) would be the "right"
  home for the toggle, but Core is owned by another agent + out of scope this task. For now the
  switch lives as a static in `SystemPrompts`. FOLLOW-UP: surface it in Core config + a WPF settings
  checkbox, and read it in `FlAgent.ConfigureCoreAsync` / `ChatBridgeService.ConfigureAsync`.
- `SystemPrompts.SubAgent` (used by `SubAgentService`) was left as-is. Sub-agents already reply with a
  single concise sentence (minimal prose), so caveman gain is small. FOLLOW-UP (optional): compose a
  caveman variant for sub-agents too if their reports grow.
- Verified by build only (`dotnet build src/FruityLink.Agent/FruityLink.Agent.csproj`). Did NOT launch
  FL Studio / WPF (sibling agent owns runtime testing). Recommend a live A/B: same prompt with
  `CavemanModeEnabled` true vs false, compare output-token counts + confirm tool calls still fire.
