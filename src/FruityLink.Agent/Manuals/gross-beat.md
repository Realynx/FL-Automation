---
name: Gross Beat
aliases: gross beat, grossbeat, stutter, tape stop
type: effect
category: Time & volume manipulation (stutter / gate / tape-stop / glitch)
summary: Real-time TIME + VOLUME automation over a 1-bar timeline — 36 time slots + 36 volume slots of stored curves. Heavily preset/slot-driven. Place EARLY on the mixer track so it manipulates the source.
---
# Gross Beat (effect)

Image-Line's real-time **time- and volume-manipulation** effect. It runs a pair of tempo-synced
automation curves over a **1-bar timeline** and rewrites the audio as it plays: the **Time** curve
shifts, freezes, reverses or resamples playback position (stutters, scratches, repeats, half-time,
pitch/tape-stop), and the **Volume** curve reshapes amplitude (gating, tremolo, sidechain-style
ducking, buildups). It is a MIXER (effect) plugin — it processes whatever audio is routed through
its mixer-track slot, and it must run with the transport **playing and bar-synced** because time
effects read from an internal look-back audio buffer.

The engine is built around **two independent banks of stored curves**: **36 Time mapping slots** and
**36 Volume mapping slots**. At any moment exactly one Time slot and one Volume slot are *active*, and
the audible effect is whatever those two currently-active curves describe. Almost every classic Gross
Beat sound is a **factory slot preset** — you don't hand-draw a curve so much as **pick the slot** that
already holds the stutter / gate / tape-stop you want.

## How the AI drives it
Parameter NAMES + INTENT are below; exact numeric INDICES are discovered at runtime. Gross Beat is an
effect on a MIXER track, so:
1. `native_list_mixer_plugin_params(mixerTrack, slot)` → read the live name→index map for this instance.
2. `native_set_mixer_plugin_params(mixerTrack, slot, index, value)` → set each param by that index.

Map the names below to indices via the list call first; do not assume a fixed order. Values are
normalized (typically 0..1 unless the list call reports otherwise); read a param back if unsure.

**Honest scope note.** What the AI can reliably do is **select which slot is active** (Time and Volume)
and set the transition/mix params — i.e. choose among the curves that already exist in the plugin and
switch between them over time. The AI **cannot draw a new envelope** (adding/moving control points is a
GUI-only action, not an exposed host parameter). So the workflow is: **rely on the factory slots**,
automate slot selection, and use smoothing to glide between them. If a needed effect isn't in a slot,
say so — it needs a preset loaded in the GUI, not a parameter tweak.

## What the curves mean
- **Time mapping (playback position).** The X axis is position within the bar; the Y axis is *where in
  the buffered audio to read from*. A straight diagonal = normal 1:1 playback. A **flat horizontal**
  segment = hold/repeat one slice (stutter/freeze). A **steeper** slope = faster playback = pitched up;
  a slope flattening toward the end = **tape-stop** (speed → 0). Segments that reach back into the
  buffer = repeats/scratches. This is why Gross Beat needs a running, synced transport and adds
  latency: it stores recent audio (roughly up to ~2 bars) so it can play it back time-shifted.
- **Volume mapping (amplitude).** The X axis is position within the bar; the Y axis is level (0–100%).
  Dips = gates/ducks (draw a dip on each beat for sidechain-style pumping); rhythmic notches = tremolo;
  a rise = a fade-in/buildup. Volume mappings are the safe, click-free way to gate because they never
  touch pitch or timing.

## How the slots / presets work
- Two rows of slots in the GUI — the top bank is **Volume mappings**, the bottom bank is **Time
  mappings**, 36 each. Most ship pre-filled with **factory content** (half-time, 1/4 & 1/8 stutters,
  scratches, tape-stops, gates, sidechain ducks, risers, etc.).
- Clicking a slot makes it the active curve; **automating the active-slot parameter** is how you change
  the effect over the song (e.g. clean on the verse, stutter on the fill, tape-stop into the drop).
- Slots hold curves; they are edited/loaded in the GUI (right-click to load, save, copy). The AI should
  treat the slot bank as a **library to choose from**, not something to author.

## When to use
- **Stutters / retriggers / glitch fills** on drums, vocals or full loops (Time slot).
- **Rhythmic gating / trance-gate / tremolo** and **sidechain-style ducking** without a compressor
  (Volume slot with per-beat dips).
- **Half-time / double-time** feel on a loop for a breakdown or fill (Time slot).
- **Tape-stop, slow-down, or pitch riser** as a transition into a drop or new section (Time slot,
  usually triggered once via slot automation).
- **Scratch / repeat** ear-candy on a one-shot or vocal phrase.
- Avoid it as an "always on" glue tool — it is a rhythmic *effect/transition* device, and its time
  mode adds latency, so keep it off tracks that must stay perfectly dry and low-latency.

## Chain placement
- Load it on a **mixer track** — either the individual channel's mixer track or a **bus** carrying a
  group (e.g. all drums, or a full loop). Bus placement lets one Gross Beat stutter/gate an entire
  submix in sync.
- Put it **EARLY in the effect chain (near the top / first slot)** so it manipulates the *source*
  before EQ, reverb and other processing act on the already-stuttered/gated result. Reverb and delay
  especially should sit **after** Gross Beat so their tails react to the chops.
- Because the Time mode buffers audio and reports latency, keep it off the master's critical path; a
  dedicated mixer track (channel or bus) is the right home.
- The transport must be **playing and bar-aligned** for time effects to sync — this is a
  play-along/arrangement effect, not a static insert.

## Parameters (names + intent)
Names vary slightly per FL version; resolve them with the list call. The reliably automatable, useful
ones are the slot selectors and the transition/mix controls.

- **Active Time slot** (a.k.a. *Time*, *Time position*, *Time mapping*) — selects which of the 36 Time
  curves is live. This is the main lever for stutters, half-time, scratches and tape-stops. Automate it
  to switch effects across the arrangement; a "flat"/normal slot = bypassed timing.
- **Active Volume slot** (a.k.a. *Volume*, *Volume position*, *Volume mapping*) — selects which of the
  36 Volume curves is live. Main lever for gating, tremolo and sidechain-style ducking. A full/flat
  slot = no volume shaping.
- **Time smoothing** (a.k.a. *Smoothing*, *Time smoothing/interpolation*) — crossfade time applied when
  the active Time slot changes, so switching mappings glides instead of clicking. Short = snappy/glitchy
  switches; longer = smeared morph between effects.
- **Volume smoothing** (a.k.a. *Smoothing*, *Volume smoothing/interpolation*) — same, for Volume-slot
  changes; keeps gate/duck transitions click-free.
- **Mix / dry-wet** — if the instance exposes it, blends processed vs unprocessed signal; verify via
  the list call. (Gross Beat's core design applies the active mappings fully, so a Mix control may be
  absent or limited — read it back before relying on it; see report note.)

## Recipes (concrete steps)
Each step is "set <param> = <intent>"; map names→indices at runtime as above. Every recipe assumes the
transport is playing/bar-synced and that a matching **factory slot** exists — pick the closest one.

- **Sidechain-style volume duck (kick-synced pump)** —
  - Set **Active Volume slot** = a factory *sidechain/duck* mapping (a dip at the start of each beat).
  - Set **Active Time slot** = normal/flat (no timing change).
  - Set **Volume smoothing** = medium so the pump is smooth, not clicky. Result: rhythmic ducking that
    tracks the bar without a compressor — great on pads/bass under a kick.

- **Trance gate / tremolo** —
  - **Active Volume slot** = a factory *gate* mapping (evenly-spaced 1/8 or 1/16 notches).
  - **Active Time slot** = normal; **Volume smoothing** = low for a hard chop, higher for a softer
    tremolo. Best on pads, chords or a sustained vocal.

- **Half-time / stutter feel (time)** —
  - **Active Time slot** = a factory *1/2-time* (slow) or *stutter/repeat* mapping.
  - **Time smoothing** = low for tight glitches. Apply on a drum bus or full loop for a breakdown; leave
    the Volume slot flat so only timing changes.

- **Retrigger / roll fill** —
  - **Active Time slot** = a factory *repeat/retrigger* mapping (holds and re-fires a short slice).
  - Automate switching **into** this slot only for the last beat/bar of a phrase, then back to normal —
    use short **Time smoothing** so the retrigger snaps in cleanly.

- **Tape-stop / slow-down transition** —
  - **Active Time slot** = a factory *tape-stop* (slope flattening to a halt) mapping.
  - Trigger it **once** by automating the slot switch right before the drop, then return to a normal
    slot on the downbeat. Keep **Time smoothing** low so the stop's ramp comes from the curve, not the
    crossfade. Pair with a Volume fade for a cleaner cut.

- **Pitch riser / buildup** —
  - **Active Time slot** = a factory *speed-up/riser* mapping (progressively steeper slope = rising
    pitch) OR combine a rising **Volume** mapping for a fade-in swell into the drop.

## Tips
- Gross Beat is **preset/slot-first**: choose the slot that already holds the stutter/gate/tape-stop; do
  not expect to sculpt the curve via parameters (that's GUI-only). If nothing fits, flag that a slot
  preset must be loaded in the GUI.
- Automate the **active-slot** params to sequence effects across the song (clean verse → gated build →
  tape-stop → normal drop); that switching is where the AI adds the most value.
- Use **smoothing** to trade snappiness for smoothness: low for hard glitch/retrigger, higher to morph
  between mappings without clicks.
- Time effects need the transport **running and bar-locked** and introduce latency — keep it on a
  channel/bus track, early in the chain, not on a track that must stay dry and zero-latency.
- Put reverb/delay **after** Gross Beat so tails follow the chops; put it before EQ/space so it shapes
  the raw source.
