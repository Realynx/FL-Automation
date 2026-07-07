---
name: FLEX
aliases: flex, image-line flex
type: generator
category: Preset-driven synth (macro-controlled ROMpler-style engine)
summary: Image-Line's free preset synth — a Subtractive/Wavetable/Multisample/FM-AM engine played through a preset plus a small set of shared shaping controls (Macros, filter, envelopes, delay/reverb/limiter).
---
# FLEX (generator)

FLEX is Image-Line's preset-based synthesizer. Under the hood it can run Subtractive, Wavetable,
Multisample, and FM/AM synthesis, but almost none of that engine is exposed on the surface — FLEX's
whole design point is "load a preset, tweak a few controls, done." The SOUND comes from the PRESET
(browsed from the Presets / Packs / Library / Store tabs, tagged by Type and Style, thousands
available). What the plugin exposes for shaping ANY loaded preset is a shared, preset-agnostic set of
controls: up to 8 **Macros** (whose targets are defined per-preset by the sound designer), a **Pitch**
control, a voice **Filter** (Cutoff/Resonance/Env Amt) with its own **Filter envelope**, a **Volume
(Amp) envelope**, and a **master FX rack** (Master Filter, Delay, Reverb, Limiter) plus master Output
Level.

## Preset-first workflow (read this before touching any parameter)
FLEX's identity is the preset, not synthesis math. There is no documented host parameter for "load
preset N by name" — preset selection is a browser/library action (Presets/Packs/Library/Store tabs,
search, Type/Style tags), not one of FLEX's automatable knobs. Practically that means:
1. The USER (or a prior step) puts FLEX on the channel with a preset already loaded (e.g. "Warm Pad",
   "808 Sub", a pluck, a vocal chop, etc.). The AI does not invent or select that preset via params.
2. The AI's job is to SHAPE that already-loaded preset: brighten/darken it (filter), soften/harden its
   attack and release (envelopes), add space/movement (delay/reverb), tighten it (limiter), or nudge
   its Macros (whatever the designer wired those 8 knobs to for this specific preset).
3. If `native_list_channel_plugin_params` happens to surface a preset-index/browser param at runtime,
   it is undocumented behavior — treat it as a bonus, don't rely on it, and prefer asking the user to
   pick the preset in-app if a specific instrument sound is required.

## How the AI drives it
This manual gives PARAMETER NAMES + INTENT. The exact numeric parameter INDICES are discovered at
runtime — FLEX is a channel (generator) plugin, so:
1. `native_list_channel_plugin_params(channel)` → read the live name→index map for this instance.
2. `native_set_channel_plugin_params(channel, index, value)` → set each param by the index you found.

Never hard-code indices from this manual; always map the names below to indices via the list call
first. Some names below (especially Macro labels) may vary or be generic ("Macro 1".."Macro 8")
depending on FL version and preset — read the live list rather than assuming a name matches exactly.
Param values are normalized (typically 0..1 unless the list call reports otherwise) — read a param
back if unsure of its scale or polarity.

## Macros (the primary per-preset shaping controls)
FLEX has up to **8 Macros per preset**. Only the ones the sound designer actually wired light up
(colored slider handles); an unused Macro does nothing. Designers link Macros to whatever matters most
for that specific sound — common targets include things like Unison, Drive, filter Cutoff, Decay,
stereo Width, Brightness, Movement, a Reverb send amount, or Attack — but the mapping is NOT documented
per-preset and differs from sound to sound. There is no fixed "Macro 3 = resonance" contract.
- **Practical approach**: check the live param list for Macro names/labels reported for the current
  preset if available; otherwise nudge Macro 1..8 in small steps and listen/inspect for effect, or fall
  back to the fixed, always-present controls below (Filter, Envelopes, FX) for deterministic control.
- FLEX does **not** use the generic "Mod X / Mod Y" knobs found on other FL instruments like Sytrus,
  Harmor, or Slicex — its equivalent mechanism is this Macro system, not a literal MOD X/MOD Y pair.

## Pitch
- **Pitch** — main tuning control for the voice. +/- steps in semitones (max range ±24 semitones / ±2
  octaves); fine-tunes in cents via the slider. Use for octave transposition or micro-tuning a preset
  to fit a track, without touching the preset itself.

## Filter (voice filter — always present, independent of preset)
- **Cutoff** — filter cutoff frequency. Raise to brighten/open the sound, lower to darken/mute it.
- **Resonance (Res)** — boosts the frequencies just before the cutoff point; higher = more pronounced,
  "peaky"/resonant edge at the cutoff.
- **Env Amt (Envelope Amount)** — how much the Filter envelope (below) modulates the cutoff frequency.
  At 0 the Filter envelope has no audible effect on the filter; increase it to let the envelope sweep
  the cutoff (e.g. classic filter-pluck "wow" or evolving pad movement).

## Filter envelope (AHDSR — modulates Cutoff via Env Amt above)
- **Attack (A)** — time to reach the Hold level.
- **Hold (H)** — initial level the envelope holds at when a key is first held.
- **Decay (D)** — time for the level to decay down to the Sustain level.
- **Sustain (S)** — level held for as long as the key/note is held.
- **Release (R)** — how quickly the envelope (and its effect on the filter) falls to silence/zero after
  note-off.
This envelope only matters if Env Amt (above) is non-zero — with Env Amt at 0 you can change these
freely with no audible result.

## Volume / Amp envelope (AHDSR — shapes the overall loudness contour)
- **Attack (A)** — time to reach the Hold level (fade-in speed of the note).
- **Hold (H)** — initial level the sound is played at when a key is held.
- **Decay (D)** — time for the level to decay to the Sustain level.
- **Sustain (S)** — level held while the note/key is held.
- **Release (R)** — how quickly the sound decays to silence after note-off (0 = hard cutoff, high =
  long tail/pad-like fade).
This is the primary control for a preset's "shape" — soft pads (slow Attack, long Release) vs. tight
plucks/stabs (fast Attack, short Decay/Release, low Sustain).

## Master FX rack (post-synthesis, applies to the whole preset)
These sit after the voice and shape the overall character/space. They are global per-instance, so
changes affect everything played on this channel — adjust with a light hand since presets usually
arrive already mixed.

### Master Filter
- **Type** — filter mode selector (~20+ options: Low pass 6/12/12 Alt/24/24 Alt dB, High pass
  6/12/12 Alt/24 dB, Band pass 12/12 Alt dB, Low shelf, High shelf, Notch, Peak, Phaser (3 variants),
  Vowel, Comb+, Comb-, All pass).
- **Cutoff** — this filter's cutoff frequency.
- **Resonance** — frequency boost at the cutoff, same idea as the voice filter's Resonance.

### Delay (built on the Fruity Delay 3 engine)
- **Time** — delay time between echoes.
- **Feedback** — how much of each echo is fed back in (more feedback = more repeats).
- **Mix** — wet/dry balance of the delay send.
- **Color** — low-pass filtering applied to the echoes (darkens repeats as they continue).
- **Mod** — sine-wave modulation depth of the delay time; short Time + Mod gives chorus/flanger-like
  movement (FLEX has no dedicated Chorus block — this is the closest built-in equivalent).
- **Type** — Fake stereo / True stereo / Ping pong.

### Reverb
- **Decay** — time for the reverb tail to decay to -60 dB (room/space size in time, not the "Size"
  control below).
- **Size** — size of the simulated room/space.
- **Mix** — wet/dry balance — this is the "reverb send" amount to reach for when adding space.
- **Color** — damping of high frequencies in the reverb tail (lower Color = darker, more natural tail).
- **Mod** — modulates the reverb time for a subtler, less static tail.
- **Speed** — modulation speed for the Mod sine-wave modulation above.

### Limiter (built on the Maximus engine)
- **Pre** — pre-compression gain (drive into the limiter).
- **Mix** — dry/compressed mix (parallel compression amount).
- **Type** — Custom / Limiter / Warming / Heating / Distortion.

## Master Level
- **Master Level / Output Volume** — final output gain from FLEX, after all FX. Use to gain-stage the
  channel, not as a mixing tool for individual sound elements (those are already baked into the
  preset).

## Sound-design recipes (concrete steps)
Each step is "set FLEX <param> = <value>"; map names→indices at runtime as above. All of these ASSUME
a preset is already loaded — they reshape it, they don't create a sound from nothing.

- **Soften an attack (pad-ify a preset)** —
  - Volume envelope Attack ↑ (e.g. toward 0.3–0.6 of range for a noticeable fade-in)
  - Volume envelope Hold slightly up, Release ↑ for a smoother tail
  - Optionally raise Filter Env Amt slightly and slow the Filter envelope Attack too, so the tone
    opens up gradually along with the volume.

- **Brighten via cutoff** —
  - Filter Cutoff ↑ (the direct lever for "brighter/more present")
  - Small Resonance ↑ for a bit of edge/bite at the cutoff, if it still sounds clean
  - Check the live param list for a Macro labeled Brightness/Tone/Cutoff on this preset — if present,
    nudging it may be the designer-intended (and cleaner) way to brighten instead.

- **Add space via reverb send** —
  - Reverb Mix ↑ (this is the "send amount")
  - Size ↑ and Decay ↑ together for a bigger, longer space; keep Color moderate-to-low so the tail
    doesn't get harsh/bright
  - Small Mod/Speed helps avoid a static, phasey tail on sustained notes.

- **Shorten release for a pluck** —
  - Volume envelope Release ↓ (short = plucky, percussive cutoff)
  - Volume envelope Decay ↓ and Sustain ↓ (little to no sustained body, all transient)
  - Hold short too, so the note snaps rather than blooms
  - For an extra pluck character, add a fast Filter envelope Decay with Env Amt raised, so the filter
    also snaps shut right after the transient (classic "filter pluck").

- **Wide / chorus-like movement (no dedicated Chorus block)** —
  - Delay Time short (near-unison-delay territory), Mod raised for pitch/time wobble, Feedback low,
    Mix moderate — approximates chorus/ensemble width using the Delay block.
  - If the loaded preset exposes a Unison-linked Macro (designer-dependent, not guaranteed), raising
    it is the more "native" way to widen the sound — check the live param list first.

## Tips
- Preset choice dominates the sound far more than any parameter here — if the current preset is
  fundamentally the wrong instrument (e.g. a pluck when a pad is needed), the fix is picking a
  different preset (a user/UI action), not chasing it with envelopes and filters.
- Macros are the fastest lever but their meaning is preset-specific and undocumented in general —
  always sanity-check the live param list/labels for the loaded preset rather than assuming, e.g.,
  Macro 1 always means the same thing across presets.
- The Filter envelope only has an audible effect if Env Amt is non-zero — don't debug a "stuck" filter
  envelope without checking Env Amt first.
- Master Filter/Delay/Reverb/Limiter are global per FLEX instance and affect the whole preset at once;
  they're the right place for space/glue/brightness, not per-macro or per-layer tweaks (FLEX has no
  concept of separately-addressable layers here).
- No dedicated Chorus block exists — use the Delay's short-Time + Mod combination for chorus/flanger-
  style width instead.

## Unsure / needs live verification
- Exact reported parameter name strings from `native_list_channel_plugin_params` may not match the UI
  labels above verbatim (e.g. "Res" vs "Resonance", or how the Volume vs. Filter envelope sets are
  distinguished/prefixed) — confirm against the live list before mapping.
- Whether Macro labels/targets for the currently loaded preset are exposed through the host param list
  at all, or only as generic "Macro 1".."Macro 8" — unconfirmed.
- Polarity/range of Env Amt (whether it can go negative for inverted filter envelopes, or is unipolar
  0..max) is not explicitly documented by Image-Line.
- Whether any preset-selection/browser param is exposed to host automation at all — the official manual
  documents no such parameter, so this manual assumes there isn't one.
