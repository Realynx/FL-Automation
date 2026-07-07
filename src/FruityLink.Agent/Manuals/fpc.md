---
name: FPC
aliases: fpc, fruity pad controller, drum pads, pad controller, drum kit
type: generator
category: Drum/pad sampler (16-pad MPC-style kit, 2 banks)
summary: FL's built-in MPC-style drum-pad sampler — 16 velocity-sensitive pads per bank (2 banks, 32 pads total), each pad a sample (or multiple velocity-switched layers) with its own tune/pan/volume, triggered by note; the standard tool for building/playing a drum kit.
---
# FPC (generator)

FPC (Fruity Pad Controller) is FL Studio's built-in drum-pad sampler, modeled on the classic Akai
MPC workflow: instead of one voice that plays melodically across the keyboard, FPC is a **KIT** — a
grid of pads, and each pad is its own mini sample-player with its own tune/pan/volume, triggered by
its own single MIDI note. Building a beat with FPC means two very different activities: (1) loading
a sample onto each pad (a browser/drag-and-drop action, done once per kit) and (2) placing NOTES in
the piano roll that hit each pad's assigned note (done per pattern, this is where the groove lives).
There is no single "filter cutoff for the whole kit" — every pad is shaped independently.

## How the AI drives it
This manual gives PARAMETER NAMES + INTENT. The exact numeric parameter INDICES are discovered at
runtime — FPC is a channel (generator) plugin, so:
1. `native_list_channel_plugin_params(channel)` → read the live name→index map for this instance.
2. `native_set_channel_plugin_params(channel, index, value)` → set each param by the index you found.

Never hard-code indices from this manual; always map the names below to indices via the list call
first (order can differ by FL version and by how many pads/layers the current kit uses). Param
values are normalized (typically 0..1 unless the list call reports otherwise) — read a param back if
unsure of its scale or polarity. Because FPC's parameters are **per-pad** (up to 32 pads, each with
its own Volume/Pan/Pitch), expect the live list to contain many similarly-named entries — filter the
list by pad number/name when you can.

### IMPORTANT — FPC is a kit of independently-triggered pads, not one voice
Unlike a synth (3xOSC, Sytrus…) where every parameter shapes one instrument, almost everything useful
in FPC is scoped to a SINGLE PAD. There is no documented plugin-wide "master volume/pan/pitch" knob
separate from the per-pad ones — for overall kit loudness, use the channel/mixer volume rather than
assuming FPC exposes a master level param. Also, loading/replacing the actual audio sample on a pad
is a browser drag-and-drop / file action in FL's UI, **not** a documented host-automatable parameter
— treat sample selection as something the user (or a prior setup step) has already done, and focus
the AI's work on shaping the pads that are already loaded plus authoring the notes that play them.

## Pads, banks and layers (the kit concept)
- **Pads** — up to **16 pads per bank**, laid out in a 4×4 grid numbered bottom-left → top-right.
- **Banks (A / B)** — FPC has **2 banks**, each with its own 16 pads, giving **32 pads total** (the
  pad selector shows a global index like "Pad 1/32"). Switch banks with the Bank A/B control.
- **Layers (per pad)** — a pad isn't limited to one sample: it can hold multiple **layers**, each
  responding to a specific **velocity range**, so a hard hit and a soft hit can trigger genuinely
  different recordings (realistic acoustic kits) instead of just a quieter copy of the same sample.
  Each layer has its own Volume, Pan and **Tune** (-1..+1 octave). Overlapping layers can be set to
  choose via **Cycle** (round-robin), **Random**, or **Random (avoid previous)**; **Spread Even** and
  **Lock Layers** manage how velocity ranges divide up. Layer authoring (which samples, their
  velocity splits) is a kit-building/UI action, not something this manual assumes is host-automatable
  — see "Unsure" below.

## Per-pad parameters (the AI-reachable shaping knobs)
These are the "Main Pad Properties" controls FL documents as knobs on the selected pad, and are the
most likely to appear as automatable parameters per pad:
- **Volume** — this pad's overall output level. Controls the loudness of every layer/sample loaded
  on the pad together (e.g. if a pad has 4 layered kick samples, Volume scales all 4 at once).
- **Pan** — this pad's stereo position. Use to spread hi-hats/percussion left-right, or to keep
  kick/snare centered/mono.
- **Pitch** — this pad's overall pitch offset. Use to tune a one-shot sample up or down without
  reloading it (e.g. deepen a kick, brighten a snare, detune a clap layer against the main snare).
- **Mute** — silences the pad (shown active/green in the UI). Useful for A/B'ing a pad out of a kit.
- **Solo** — isolates the pad (shown active/red in the UI), muting all other non-solo'd pads. A
  monitoring/audition control more than a mix-authoring one.
- **Scale volume** — when enabled, the pad's output volume follows note velocity (harder MIDI hits =
  louder); when disabled, velocity only selects which velocity-layer plays (if any) and volume stays
  fixed. Leave enabled unless velocity dynamics are being handled entirely through layered samples.

## Pad-building / kit-structure fields (verify these are host-exposed before relying on them)
FL's manual documents these as pad properties, but they read more like kit-editor/routing settings
than continuous mix parameters — confirm they actually appear in `native_list_channel_plugin_params`
for this instance before depending on them; if they don't, they're a "set it up in the UI" job:
- **Play Key / Octave** — the MIDI root note that triggers this pad (this IS the pad→note mapping).
  Can be set directly, captured via **Learn** (hit a MIDI key to assign it), or bulk-assigned with
  **Map notes for entire bank** (steps through every pad in the bank assigning consecutive notes).
  **Show GM note names** displays General MIDI drum-map names (Kick, Snare, Closed Hat, …) next to
  the note, useful when matching a pad layout to the General MIDI drum convention.
- **CUT / BY** — mute-group fields: "Use CUT and BY to use one Pad to cut (stop) notes playing on
  another Pad." Give two pads matching CUT/BY values so triggering one stops the other's ringing
  note — the classic closed-hat-cuts-open-hat trick. A pad can also cut itself.
- **Output** — adds a mixer-track offset relative to FPC's own track, letting a pad (or group of
  pads) route to its own mixer insert independently of the rest of the kit (e.g. send the kick and
  snare to separate tracks for individual processing).

## Triggering pads: notes are the interface
FPC has no "trigger pad N" bridge command — pads are played exactly like any other instrument note.
To make FPC produce sound, place notes on its channel at the pitch each target pad is assigned to
(its **Play Key**):
- `native_add_note` / `native_add_notes` — `channel` = FPC's channel index (from
  `native_list_channels`); `key` = the MIDI note number matching the pad's Play Key (the bridge uses
  0-131 with 60 = middle C, i.e. FL's own "C5" naming); `velocity` (0-127) drives dynamics — and, if
  **Scale volume** is enabled on that pad, its loudness — plus which velocity-layer plays, if the pad
  has velocity-split layers.
- **Which note maps to which pad is preset/kit-dependent.** FPC's stock "Empty" preset pre-assigns
  General MIDI drum-map keys, but the official manual does not enumerate exact numbers, and any kit
  the user has built may reassign them via Play Key/Learn. Before authoring a beat, either ask the
  user which notes map to which drums, or read the pad's Play Key (with **Show GM note names** on)
  rather than assuming a fixed layout. As general MIDI-drum-map background (not FPC-specific, but the
  convention many kits follow): kick ≈ note 36, snare ≈ 38, closed hat ≈ 42, open hat ≈ 46, crash ≈
  49, ride ≈ 51 — treat these as a reasonable *starting guess* only, not a guarantee for this kit.

## Sound-design / kit recipes (concrete steps)
Each step is "set FPC <pad> <param> = <value>" or "place notes at <pad's note>"; map param names to
indices at runtime, and confirm each pad's note via Play Key before writing notes.

- **Build a trap-style kit layout** —
  - Confirm (from the user, or the pad Play Key/GM-name display) which pads hold kick, snare/clap,
    closed hat, open hat, and any 808/perc one-shots — FPC doesn't expose a "load sample" param, so
    the samples themselves must already be on the pads.
  - Author the pattern with `native_add_notes` on FPC's channel: kick notes on a syncopated pattern,
    snare/clap on beats 2 and 4, closed-hat notes at a dense subdivision (e.g. 1/16 or 1/32) with
    varied velocity for hi-hat "rolls", occasional open-hat hits to break the roll.
  - Set the closed-hat pad's CUT value equal to the open-hat pad's BY value (or vice versa) so an
    open hat cuts a ringing closed hat and they don't overlap unnaturally.

- **Tune a kick pad down** — deepen a thin one-shot kick without re-sampling.
  - Find the kick pad's **Pitch** parameter via `native_list_channel_plugin_params` (filter by the
    kick pad's number/name) and lower it a modest amount; re-audition and adjust in small steps since
    the pitch range/scale isn't documented numerically — read the value back if unsure.
  - If layer-level **Tune** is reachable for this pad (see "Unsure" below), it offers a wider, more
    musical range (documented as -1..+1 octave) — prefer it over pad Pitch if both are exposed.

- **Pan hats for width** — reduce mono buildup and add stereo interest.
  - Set the closed-hat pad's **Pan** slightly to one side (e.g. a bit left) and the open-hat/ride pad
    slightly to the other side; keep kick and snare pads centered (Pan = default/center) so the low
    end and backbeat stay mono/punchy.

- **Velocity for ghost notes** — snare/hat ghost notes are a NOTE property, not a plugin param.
  - Place extra low-velocity notes (e.g. velocity ~20-45 out of 127) between the main snare hits via
    `native_add_note`/`native_add_notes` on the snare pad's note — classic ghost-note groove.
  - Make sure the snare pad's **Scale volume** is enabled so those low-velocity hits actually come out
    quieter (with it disabled, only the *layer selection* changes with velocity, not the loudness).
  - The same technique works for hi-hat dynamics: alternate full-velocity and low-velocity hat notes
    to fake an accented 8th/16th-note groove instead of a flat, robotic hi-hat pattern.

## Tips
- Sample content (what's actually loaded on each pad) is a UI/browser action, not a host parameter —
  don't try to "set" a sample by index; work with what's already loaded and ask the user if a specific
  drum sound needs to change.
- FPC has no plugin-wide master Volume/Pan/Pitch distinct from the per-pad ones — gain-stage the whole
  kit via the channel/mixer, not by hunting for a "master" FPC param.
- Mute groups (CUT/BY) matter for realism on hats and other choke-able instruments — an unnaturally
  overlapping open+closed hat is one of the most common "this doesn't sound like a real kit" tells.
- Because every meaningful control is per-pad, always confirm which pad index/number you're editing
  (via the live param list or pad naming) before setting Volume/Pan/Pitch — there is no single
  "the kick" parameter, only "Pad N's" parameters, and N depends on how the user built the kit.
- Image-Line's manual stresses giving every sample used across an FPC kit a **unique filename** to
  avoid accidental duplicate loading — relevant if the AI is ever asked to help reorganize a kit's
  source samples, even though sample management itself isn't a plugin parameter.

## Unsure / needs live verification
- Exact reported parameter name strings from `native_list_channel_plugin_params` for per-pad
  Volume/Pan/Pitch (e.g. "Pad 1 Volume" vs "1 Volume" vs some other prefix/format) are not confirmed
  — read the live list rather than assuming a name matches exactly.
- Whether Mute, Solo, and Scale-volume are exposed as host-automatable toggle params at all, or are
  editor-only UI state — unconfirmed.
- Whether Play Key/Octave, CUT/BY, and Output offset are exposed as host-automatable params (so the
  AI could set them directly) or are kit-structure fields only editable in FPC's own UI — unconfirmed;
  this manual treats them as "verify before relying on."
- Whether per-LAYER Volume/Pan/Tune (as opposed to per-PAD Volume/Pan/Pitch) are separately
  addressable through the host param list — unconfirmed; assume only pad-level controls are reachable
  unless the live list proves otherwise.
- The exact default note-to-pad layout of FPC's stock "Empty" preset is not enumerated in Image-Line's
  official manual text (only that it "pre-assigns the appropriate General MIDI keys") — this manual
  gives the standard GM drum-map note numbers as background/a starting guess, not a guaranteed mapping
  for any given kit; always confirm the live Play Key assignment (or ask the user) before authoring
  notes.
