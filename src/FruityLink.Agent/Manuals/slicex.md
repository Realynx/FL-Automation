---
name: Slicex
aliases: slicex, slicer, beat slicer, chop
type: generator
category: Beat slicer / loop chopper
summary: FL's beat-slicing sampler — auto-slices a loaded loop by transients, maps each slice to a key, and plays slices back as notes; the tool for chopping drum loops, re-sequencing beats and vocal chops.
---
# Slicex (generator)

A sample-based "beat slicer": you load a loop (usually a drum loop, but any audio works) into one of
Slicex's two sample decks (Deck A / Deck B), it auto-slices the loop by transients (or reads
slice/region markers already embedded in the file, e.g. REX files or pre-sliced Browser content), and
each resulting slice becomes an independently playable "region" bound to a MIDI trigger note. From
there Slicex is played like a drum kit: each Piano roll note pitch fires a specific slice. Because
every slice is now a free-standing one-shot, you can re-order them (re-sequence the beat), skip/repeat
them, layer several slices on one key, and shape each slice (or the whole instrument) with a filter,
an ADSR-style envelope, per-slice playback speed, and global pitch — independent of the original
loop's timing. Two decks + layering/crossfade let you build variations (e.g. alternate takes, velocity
layers) from one or two source loops.

## How the AI drives it
This manual gives PARAMETER NAMES + INTENT. The exact numeric parameter INDICES are discovered at
runtime — Slicex is a channel (generator) plugin, so:
1. `native_list_channel_plugin_params(channel)` → read the live name→index map for this instance.
2. `native_set_channel_plugin_params(channel, index, value)` → set each param by the index you found.

Never hard-code indices from this manual; always map the names below to indices via the list call
first (order and exact surfaced set can differ by FL version / plugin state). Param values are
normalized (typically 0..1 unless the list call reports otherwise) — read a param back if unsure of
its scale. Discrete choices (filter type, layer mode, crossfade curve) are usually exposed as stepped
values across the same 0..1 range — read the current value back before/after a set to confirm you
landed on the intended step.

## Sample & slice loading is NOT a normalized parameter
Be honest with yourself here: loading an audio file into Slicex and generating slices is a
file/UI-level operation (drag-and-drop from the Browser, or "Auto-slice" with a Dull/Medium/Sharp
preset, grid-based slicing, or beat/downbeat detection, done in Slicex's Wave Editor) — it is **not**
one of the plugin's automatable/settable parameters, so it cannot be driven through
`native_set_channel_plugin_params`. Practically this means:
- Slicex needs a loop already loaded and sliced (by the user, or via a pre-sliced Browser/REX loop)
  before the AI's note-authoring and param-shaping does anything audible.
- The AI's real levers are (a) **which notes it writes** — each note pitch selects a slice, so
  composing/re-ordering slices is done entirely in the Piano roll, and (b) the **shaping parameters**
  below (envelope, filter, speed, pitch, volume/pan), which apply to slice playback, not to the
  slicing process itself.
- If no sample is loaded / no slices exist, setting these shaping params has nothing to play back —
  confirm a loop is loaded (or ask the user to drop one in) before relying on Slicex for sound.

## Slice → note mapping (how the AI "plays" a chopped loop)
- Each slice/region has a **trigger note** (a MIDI note number) it's bound to — that's the key that
  fires it. Re-sequencing a beat is just writing Piano roll notes at the trigger-note pitches of the
  slices you want, in the order/timing you want — Slicex does not need the notes to be in the loop's
  original order.
- **Layering**: multiple regions can share the same trigger note ("layers"); Slicex's Master panel
  layer mode (All / All gain-compensated / velocity-mapped / mod X-Y-mapped / random / cycle / song
  time-synced) decides how layered slices on one key combine or alternate — useful for velocity-based
  variation or randomized hits without changing note data.
- **Note color selects the deck**: in the Piano roll, a note's color (1-16) maps to MIDI channel
  1-16 — odd colors (1,3,5...15) trigger Deck A, even colors (2,4,6...16) trigger Deck B; colors 15
  and 16 additionally play the slice in reverse. This lets the AI address two loaded loops (or a
  reversed pass) from the same channel purely via note color, no plugin param needed.
- **Pitched/melodic use** (e.g. a vocal chop played as an instrument): if the loaded region has a
  root ("middle") note and a low/high key range set, notes outside the region's own trigger note will
  play the same slice pitch-shifted/resampled to that note, like a normal sampled instrument — this is
  how a single vocal chop becomes playable across a melodic range instead of a single hit.

## Parameters the AI can set
### Global (Master panel) — applies to the whole instrument
- **Master level** — overall output volume of Slicex (all slices, both decks).
- **Master pitch** — transposes every slice uniformly (semitone-ish global pitch shift), independent
  of which trigger note fired. Use this for "the whole chop is a bit sharp/flat" or a global key change
  without re-mapping notes.
- **Master randomness level** — scales the depth of any RAND-sourced modulation (see envelope sources
  below) instrument-wide; raise for more per-hit variation, 0 for fully deterministic playback.
- **Master LFO** — scales the depth of any LFO-sourced modulation instrument-wide.

### Amp envelope (the "global ADSR") — shapes each slice hit's volume over time
Each slice's playback runs through an envelope/articulator whose default modulation target is
volume (VOL) — functionally the instrument's amp envelope:
- **Attack (ATT)** — fade-in time from note-on. 0 = instant hit; raise for a softer, swelling onset.
- **Decay (DEC)** — time to fall from the attack peak toward the sustain level.
- **Sustain (SUS)** — the held level a sustained note sits at after decay (irrelevant for very short
  one-shot slices that finish before decay completes).
- **Release (REL)** — fade-out time after note-off. Raise to avoid clicks when a slice is cut short by
  note-off; keep near 0 for tight, chopped, percussive hits.
- The same envelope shape can instead (or additionally) target filter cutoff (CUT), resonance (RES),
  pan (PAN), playback speed (SPEED) or start position (START) — if you find an ATT/DEC/SUS/REL set in
  the param list that doesn't visibly affect volume, it may be routed to one of these other targets;
  check behavior after setting rather than assuming VOL.

### Filter — tone-shaping on the slices
- **Filter cutoff (CUT)** — the filter's cutoff frequency. Low value = darker/muffled, high value =
  brighter/more open (behavior depends on filter type below).
- **Filter resonance (RES)** — emphasis/peak at the cutoff frequency. Higher = more pronounced,
  squelchy/whistling peak; use sparingly on drum material to avoid harshness.
- **Filter type** — a discrete choice of **LP** (low-pass, cuts highs — the default "darken" filter),
  **BP** (band-pass, keeps only a band around cutoff — telephone/lo-fi effect), **BS** (band-stop /
  notch, removes a band around cutoff), or **HP** (high-pass, cuts lows — thins out low end). Each type
  also has a slope (1x/2x/3x, i.e. gentle to steep) — treat slope as a separate stepped param if it
  appears in the list.

### Per-slice / per-region playback (may appear as per-region entries in the param list)
These live on each individual slice/region rather than globally, and are worth knowing so you don't
confuse them with the global/envelope params above:
- **Speed (per region)** — this slice's playback rate. It is a plain rate change (like tape/turntable
  speed), so it moves pitch and duration together — it is NOT an independent time-stretch. Use it for
  quick pitch+time effects on a slice (half-speed = down an octave and twice as long; negative values
  reverse/scratch the slice).
- **Start (per region)** — offset into the slice where playback begins (plus an optional trigger
  delay) — nudge forward to skip a slice's transient/click, e.g. for a smoother pad-like re-trigger.
- **Amp: Pan & Level (per region)** — this slice's own pan position and volume, layered under the
  global Master level above. Use to balance individual hits (e.g. quiet down an overly loud snare
  slice) without touching the whole instrument.
- **Cut group (per region, also labeled CUT in the region row)** — a choke/mute group: triggering one
  region silences other currently-playing regions in the same group (classic hi-hat open/closed choke
  behavior). Do not confuse this "Cut" (choke group) with filter "Cutoff" (also sometimes shown as
  CUT) above — they are unrelated controls that happen to share an abbreviation in the UI.
- **Output (OUT, per region)** — routes this region to a specific mixer track instead of the channel's
  default, letting individual slices (e.g. kick vs. snare vs. hats) get separate mixer processing.

### "Time-stretch mode" — be honest about what's live-settable
Slicex has genuine time-stretch tooling ("Drum Loop Stretch", a "Time Stretch/Pitch Shift Tool", and
tempo detection/sync), but these are **offline Wave Editor operations performed on the sample once**,
not real-time automatable parameters — they cannot be driven via `native_set_channel_plugin_params`.
The only live, per-note "time" lever exposed as a plugin parameter is the per-slice **Speed** above,
which changes pitch and time together rather than stretching independently. For a pitch-preserving
tempo change, the source loop needs to be time-stretched before/while slicing (a manual/Wave-Editor
step), not something the AI can do purely through parameter automation.

## Sound-design recipes (concrete steps)
Each step assumes a loop is already loaded into Slicex and sliced (auto-slice or pre-sliced content).
Map param names → indices at runtime as above.

- **Chop a drum loop and re-sequence** — the core Slicex move.
  - Confirm slices exist (loop loaded, auto-sliced). Note each slice's trigger note.
  - In the Piano roll, write notes at those trigger-note pitches in a NEW order/rhythm (not the
    original loop order) — e.g. swap the 2nd and 4th 16th-notes, or repeat the snare slice twice.
  - Keep envelope Release low so re-triggered slices stay tight and don't smear into each other.

- **Half-time a loop via Speed** — quick, deliberately pitched-down "screwed" half-time feel.
  - Set the active regions' **Speed** to ~0.5. This drops pitch about an octave and doubles each
    slice's duration (Speed is a rate change, not an independent stretch — see honesty note above).
  - If a pitch-preserving half-time is required instead, that has to be done by time-stretching the
    source loop in the Wave Editor before slicing — not achievable purely via live parameter sets.

- **Filter-sweep a loop** — evolving, filtered chop.
  - Set **Filter type = LP**, start **Filter cutoff** low (dark).
  - Automate/step **Filter cutoff** upward over the pattern/arrangement (or drive it from the
    envelope's ATT/DEC re-routed to CUT) so the loop opens up brightness over a bar or section.
  - Add a little **Filter resonance** for a more pronounced, synth-like sweep.

- **Pitch a vocal chop** — turn one chopped phrase into a playable melodic instrument.
  - Ensure the chop's region has a root/middle note and a sensible low/high key range set (so it
    behaves as a pitched sample, not a fixed one-shot).
  - Play the chop at different Piano roll note pitches to retune the phrase to a melody; use
    **Master pitch** for a small uniform tuning correction across all uses instead of re-mapping notes.
  - Keep Attack low and Release moderate so the chop's own vocal transient stays intact while its tail
    doesn't cut off abruptly.

## Tips
- Slicex is silent without a loaded, sliced sample — always be sure sound is actually mapped to notes
  (via the Piano roll trigger-note pitches) before assuming a param change will be audible.
- "Cut" appears twice in Slicex's own UI with two different meanings (choke/mute Cut Group vs. filter
  Cutoff) — don't assume a param named similarly is the filter; verify by ear or by its effect.
- Speed changes pitch and time together; don't expect it to behave like a pitch-independent stretch.
- Note color in the Piano roll is a real control surface here (deck A/B select + reverse on 15/16) —
  remember this when re-sequencing across two loaded decks.
- Global params (Master level/pitch/randomness/LFO) affect everything at once; per-region params
  (Speed/Start/Amp/Cut group/Output) affect just one slice — pick the right scope for the effect.
