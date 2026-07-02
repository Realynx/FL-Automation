# Agent tuning — MINIMALISM (fewest, only-necessary tool calls)

Problem (user-reported): on a simple request ("make a <thing>") the FL Agent does extra/miscellaneous
work it wasn't asked for — sets volume/pan/pitch, mutes, routes, reads unrelated channels/patterns/
project state, "warms up" with `native_is_available`, etc. It should do EXACTLY what's needed and
nothing more.

Backend = `glm-5.2:cloud`. Caveman output mode ON. **Caveman is orthogonal to this problem** — it
compresses PROSE/reasoning tokens only (see `CavemanPrompt` carve-outs: "Emit tool calls exactly as
the function schema demands"). It does NOT reduce the NUMBER of tool calls. The over-acting is driven
by the `Default` prompt + tool descriptions + the model's "helpful" tendency, and needs its own fix.

Scope of this doc: MINIMALISM only. A sibling agent owns PARALLELISM + raw SPEED (the
`AllowParallelCalls=true` batching in `FlAgent.cs`/`SubAgentService.cs` and `run_parallel_tasks`).
Overlap noted where relevant; I don't redesign their parts. Fewer calls helps them regardless of how
calls are batched.

RESEARCH ONLY — no source edited. All proposals below are exact before/after text for the coordinator.

---

## 1. Diagnosis — what drives the gratuitous calls

All file refs are `src/FruityLink.Agent/`.

### Cause A (primary): "Inspect real state before acting" reads as "always survey first"
`SystemPrompts.cs`, `Default`, lines 82-84:

```
- Inspect real state before acting (native_is_available, native_list_channels / native_list_patterns /
  native_get_current_pattern) — don't guess indices. Identify channels by NAME (e.g. "Kick","Color Bass")
  — cited numbers are often wrong; map name → real index.
```

- "**before acting**" attaches inspection to EVERY action, not "when you need a value." The model
  reads it as an unconditional pre-flight.
- It then **enumerates four read tools** as the recommended pre-flight: `native_is_available`,
  `native_list_channels`, `native_list_patterns`, `native_get_current_pattern`. On a fresh "make a
  <thing>" the model dutifully fires all four before doing anything — most of it unneeded (adding a
  brand-new channel needs neither the existing-channel list nor the pattern list; new notes default
  to `pattern 0 = current`, so `get_current_pattern` is pointless).
- The genuinely-needed bit ("map name → real index") is buried as the tail of the same sentence, so
  the model keeps the survey AND does the mapping.

### Cause B: `native_is_available` is advertised as a warm-up — but it's pure waste in the normal path
- Listed first in Cause A's pre-flight list, and again in lines 88-90 ("If native_is_available says
  the bridge isn't injected…"). The model calls it as a handshake on most turns.
- It's redundant: **every** `native_*` tool already returns a clear, classified bridge error on
  failure (`PluginSupport.BridgeError` — distinguishes "bridge not reachable → inject it" from a
  logic error). So the availability check buys nothing; the next real tool would report the same
  thing. Pure extra round-trip.

### Cause C: header plants a "list/read first" habit
`Default` lines 67-68:

```
... plugin params (sound design — set NORMALIZED 0.0-1.0; list params first), ...
```

"list params first" is correct *for setting a param* (you need the index), but stated as a bare
imperative it reinforces a general "list before you act" reflex. Header also frames the agent around
"**read** + change the live project" and "Inspect … don't guess," tilting overall tone toward
exploration.

### Cause D: NO minimalism rule anywhere in the MAIN agent prompt
This is the core gap. `Default` never says "do only what's asked / make the fewest calls / don't
touch what you weren't asked to." Tellingly, the `SubAgent` prompt DOES ("Do exactly the task you are
given — nothing more; don't touch patterns/channels outside it") — but the main agent has no
equivalent. With a vacuum and ~60 friendly "set_*" tools available (volume/pan/pitch/mute/route/EQ/
send, each annotated with defaults like "10000 = default", "6400 = center"), the model fills the gap
with "helpfulness": it rounds out a new instrument by setting a sensible volume/pan, etc.

### Cause E: tool descriptions that invite extra steps
- `native_get_notes` (line 474): "**Use to understand existing music before editing.**" — invites a
  read on essentially any edit, even when adding fresh notes.
- `native_select_channel` (line 261): "Select a channel (0-based) so the piano roll edits it." and
  `native_select_pattern` (line 227): "Select pattern by number." Nothing says these are NOT required
  before adding notes/clips — yet `native_add_note`/`native_add_notes`/`native_add_pattern_clip` all
  take explicit `pattern` + `channel` args, so a preparatory `select_*` is almost never needed. The
  model adds them as ceremony.
- `native_is_available` (line 16): "Check if the native bridge is loaded in FL + responding." — no
  hint that it's diagnostics-only, so it gets called routinely (Cause B).

### Cause F: creative-soul is forced on every musical request
- `MusicTheoryPlugin.cs` line 18: get_creative_soul "you **MUST** apply … **Consult BEFORE
  generating**"; `Default` lines 92-95 also says "First call get_creative_soul." This is the
  intended creative behavior and should be PRESERVED — but it's currently unscoped, so it also fires
  on purely mechanical edits (e.g. "kick on every beat") where there's no melodic/harmonic idea to
  bias. Minor, but it's one more reflexive call. Fix = scope it to actual idea generation, keep it
  otherwise intact.

### Cause G (minor, redundant-call): `get_ppq` after `build_chord_progression`
`build_chord_progression` already returns `PPQ=…` and per-note `start`/`length` in ticks
(`MusicTheoryPlugin.cs` lines 56-59). After calling it, a separate `native_get_ppq` is redundant —
the tick positions are already computed. Worth a one-line note so the model doesn't double up.

### Non-cause (note for the sibling): parallel-call batching
`FlAgent.cs` lines 95-96 set `AllowParallelCalls=true`. This doesn't CREATE unnecessary calls — it
batches intended ones into one round-trip. But it lowers the friction of emitting a "survey bundle"
(`is_available` + `list_channels` + `list_patterns`) cheaply in one shot, so it mildly amplifies
Cause A. Left to the sibling agent (speed/parallelism); minimalism fixes the root by removing the
survey reflex.

---

## 2. Proposed REWRITE — `SystemPrompts.Default` "Operating principles" block

Replaces the current "Operating principles:" block (`SystemPrompts.cs` lines 81-95) **in full**.
Preserves: name→index mapping, `native_get_ppq`, the bridge-not-injected guidance, the
run_parallel_tasks option, and creative-soul behavior. Leaves `CavemanPrompt`, `BuildDefault()`,
`SubAgent`, the header, and "Authoring notes" untouched (caveman + BuildDefault structure intact).

### BEFORE (lines 81-95)
```
Operating principles:
- Inspect real state before acting (native_is_available, native_list_channels / native_list_patterns /
  native_get_current_pattern) — don't guess indices. Identify channels by NAME (e.g. "Kick","Color Bass")
  — cited numbers are often wrong; map name → real index.
- PREFER run_parallel_tasks for multi-part jobs: inspect state for real indices, decompose into
  independent self-contained tasks (one per pattern/instrument, each naming its own pattern + channel(s)
  so they don't conflict), run in parallel. Small/final tweaks → do directly.
- If native_is_available says the bridge isn't injected (or a tool returns a bridge error), tell the user
  to inject it (banner "Inject bridge" button, or Settings ▸ FL Studio control) + that FL must be running.
  FL is controlled ONLY via the bridge — no MIDI/controllers/scripts; never suggest MIDI setup.
- Keep the user in creative control: offer concrete options + confirm what you changed. Roman-numeral +
  chord-symbol language. Concise + musical.
- For randomness/inspiration ("roll me a progression"), make a concrete choice + explain briefly rather
  than asking back. First call get_creative_soul + let the artist's profile (adventurousness, brightness,
  complexity, dissonance, rhythmic density) steer it.
```

### AFTER (drop-in replacement)
```
Operating principles:
- DO ONLY WHAT THE USER ASKED, with the FEWEST tool calls that accomplish it. Never set volume, pan,
  pitch, mute, routing, EQ, sends, color, names, or any parameter/channel/pattern/clip/project setting
  the request did not ask for. No "nice to have" extras, no tidying, no rounding-out a new instrument.
- Read state ONLY to get a value you need to act on — e.g. map a named channel ("Kick","Color Bass")
  to its index via native_list_channels, or get timing via native_get_ppq. Cited indices are often
  wrong, so map a NAMED channel → its real index; otherwise act on what you're given. Do NOT survey
  unrelated channels/patterns/clips or read project/song state you don't need.
- Don't add preparatory calls the action doesn't require: native_add_note(s) and native_add_pattern_clip
  take pattern + channel directly, so don't native_select_pattern/native_select_channel first; new notes
  default to pattern 0 = current, so don't fetch the current pattern just to pass it. Don't call
  native_is_available as a warm-up — just call the tool you need; if the bridge is down it returns a
  clear error. Don't re-read state to "confirm" — trust the tool's success result.
- If a tool reports the bridge isn't injected/reachable, tell the user to inject it (banner "Inject
  bridge" button, or Settings ▸ FL Studio control) and that FL must be running. FL is controlled ONLY
  via the bridge — no MIDI/controllers/scripts; never suggest MIDI setup.
- Generating a melody/chord/musical idea (incl. "roll me a progression")? FIRST call get_creative_soul
  and let the artist's profile (adventurousness, brightness, complexity, dissonance, rhythmic density)
  steer a concrete choice — explain briefly rather than asking back. Skip it for purely mechanical
  edits (e.g. "kick on every beat", "set tempo 140").
- Genuinely multi-part build (several independent patterns/instruments at once)? You MAY use
  run_parallel_tasks: inspect once for real indices, then give each task its own pattern + channel(s)
  so they don't conflict. A single thing or a tweak → just do it directly.
- Keep the user in creative control: briefly state what you changed (Roman-numeral + chord-symbol
  language). Concise + musical.
```

Why this fixes each cause:
- A → "Read state ONLY to get a value you need to act on" replaces "inspect before acting"; survey
  reflex removed, name→index kept.
- B → explicit "Don't call native_is_available as a warm-up"; bridge-error guidance kept as a
  reaction, not a pre-flight.
- D → new first bullet is the missing minimalism rule (mirrors the SubAgent "nothing more" clause).
- E (select_*) → "Don't add preparatory calls" bullet.
- F → creative-soul scoped to idea generation, preserved otherwise.
- Verification reads ("confirm what you changed" → re-reads) → "Don't re-read state to confirm."

### Optional small header tweak (Cause C) — `Default` line 67-68
- BEFORE: `... plugin params (sound design — set NORMALIZED 0.0-1.0; list params first), ...`
- AFTER:  `... plugin params (sound design — set NORMALIZED 0.0-1.0; to set one, list THAT plugin's params first to get its index), ...`

Low priority — scopes "list params first" to its real name→index need rather than a general habit.

---

## 3. Tool-DESCRIPTION tweaks (`Plugins/NativeControlPlugin.cs`)

Reinforce the prompt at the point of temptation. Exact before/after `[Description("…")]` text:

**`native_get_notes`** (line 474)
- BEFORE: `Read piano-roll notes in a pattern (pattern: 1-based, or 0 = current). channel = index to filter, or -1 = all. Returns each note's channel, pitch, position (ticks), length, velocity. Use to understand existing music before editing.`
- AFTER:  `Read piano-roll notes in a pattern (pattern: 1-based, or 0 = current). channel = index to filter, or -1 = all. Returns each note's channel, pitch, position (ticks), length, velocity. Call ONLY when you need the existing notes (e.g. to edit/replace/avoid them) — not as a routine step before adding new notes.`

**`native_select_channel`** (line 261)
- BEFORE: `Select a channel (0-based) so the piano roll edits it.`
- AFTER:  `Select a channel (0-based) for the FL UI. NOT required before native_add_note/native_add_notes — those take an explicit channel. Call only if the user asked to change the selection.`

**`native_select_pattern`** (line 227)
- BEFORE: `Select pattern by number (1-based).`
- AFTER:  `Select pattern by number (1-based). NOT required before adding notes/clips — those take an explicit pattern (0 = current). Call only if the user asked to change the selection.`

**`native_is_available`** (line 16)
- BEFORE: `Check if the native bridge is loaded in FL + responding.`
- AFTER:  `Diagnostics only: check if the native bridge is loaded + responding. Don't call as a warm-up — every other native_* tool already returns a clear bridge error if it's down. Use only to investigate a failure.`

**`native_list_channels`** (line 253) — keep as the legit name→index map, just scope it
- BEFORE: `List channels as 'index: name'. Use index with native_add_note/native_select_channel.`
- AFTER:  `List channels as 'index: name' — use to map an instrument NAME to its index when you need one to act. Pass that index to native_add_note(s)/native_set_channel_*.`

(`native_get_song_state`, `native_get_project_info`, `native_list_patterns` descriptions are already
neutral — the prompt rule in §2 covers over-reading them; no description change needed.)

---

## 4. Minimal-call rubric for the maintainer

For a request **R**, the minimal call set = (resolve any NAME→index/value you don't have) + (the
single action(s) R names). Nothing else. No `is_available`, no `select_*`, no `get_current_pattern`,
no setting params R didn't mention, no read-back to confirm.

**Ex. 1 — "Set the tempo to 140"**
- Minimal: `native_set_tempo(140)`. **(1 call)**
- Over-acting to avoid: `get_tempo` first, `native_is_available`, `get_song_state`.

**Ex. 2 — "Add a kick on every beat" (kick channel exists)**
- Minimal: `native_list_channels` (map "Kick"→index) → `native_add_notes(pattern 0, kickIdx,
  "60,0,120,100; 60,480,120,100; 60,960,120,100; 60,1440,120,100")`. **(2 calls)**
- If "make a kick from scratch": `native_add_sample_channel(<kick path>)` (or
  `native_list_samples`→add) → `native_add_notes(...)`. **(2-3 calls)**
- Over-acting to avoid: `is_available`, `list_patterns`, `get_current_pattern`, `select_channel`,
  `set_channel_volume`, `set_channel_pan`.

**Ex. 3 — "Write a sad 4-chord progression on the piano"**
- Minimal: `get_creative_soul` → `build_chord_progression(root,scale,[…])` (returns notes+ticks+PPQ)
  → `native_list_channels` (map "piano"→index) → `native_add_notes(0, pianoIdx, <notes>)`.
  **(4 calls; 3 if the piano index is already known)**
- Over-acting to avoid: a separate `native_get_ppq` (the progression already returned PPQ+ticks —
  Cause G), `list_patterns`/`get_current_pattern` (use pattern 0), `select_channel`, any volume/pan.

Rule of thumb the maintainer can apply: **if removing a call still produces the asked-for result,
that call was unnecessary.** Inspection earns its place only when its return value feeds a later
argument.

---

## 5. Apply-checklist (for the coordinator)
1. Replace `SystemPrompts.Default` "Operating principles" block (lines 81-95) with §2 AFTER text.
2. (Optional) header line 67-68 tweak from §2.
3. Apply the five `[Description]` edits in §3 to `NativeControlPlugin.cs`.
4. Leave `CavemanPrompt`, `BuildDefault()`, `SubAgent`, `FlAgent`/`ChatBridgeService`/`SubAgentService`
   untouched — caveman + BuildDefault structure preserved; `SubAgent` already has its own minimalism
   clause.
5. A/B check on `glm-5.2:cloud`: run the §4 examples, count tool calls before/after. Coordinate with
   the parallelism/speed sibling so the minimalism rule and their batching changes don't fight.
