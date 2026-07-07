---
name: Harmor
aliases: harmor, additive synth
type: generator
category: Additive synth (resynthesis)
summary: Image-Line's additive-synthesis powerhouse — two subtractive-style "parts" (A/B) resynthesized additively, plus image/audio resynthesis, unison, prism, blur, phaser and additive filters.
---
# Harmor (generator)

Harmor is an ADDITIVE synthesizer disguised as a subtractive one. You dial in two familiar
"parts" (A and B) using waveforms, sub oscillators, unison, filters and effects — but internally
everything is decomposed into hundreds of individual sine partials and rebuilt additively. That
additive core is why Harmor can do things a normal subtractive synth can't: resynthesize audio or
images into a playable instrument, apply true zero-phase additive filters, retune every harmonic
independently (prism), and smear the spectrum over time (blur). It is one of FL's deepest
instruments — huge supersaws, evolving pads, plucked strings, bells, vocal-ish formant tones and
sound-mangling all live here.

## How the AI drives it
This manual gives PARAMETER NAMES + INTENT. The exact numeric parameter INDICES are discovered at
runtime — Harmor is a channel (generator) plugin, so:
1. `native_list_channel_plugin_params(channel)` → read the live name→index map for this instance.
2. `native_set_channel_plugin_params(channel, index, value)` → set each param by the index you found.

Never hard-code indices from this manual; always map the names below to indices via the list call
first (order can differ by FL version). Param values are normalized (typically 0..1 unless the list
call reports otherwise) — read a param back if unsure of its scale.

### IMPORTANT — Harmor is deep and graph-driven; start from a preset
Most of Harmor's *character* comes from things that are NOT simple host-exposed knobs: the timbre
waveform windows, the image/audio you resynthesize, and dozens of envelope/mapping GRAPH editors
(the ENV panel, per-target articulators, image gain/frequency planes). Those graphs generally do
**not** appear in `native_list_channel_plugin_params`, so you cannot draw or edit them by index.

The reliable workflow:
1. **Load a preset first** (a supersaw, pad, pluck, bell, etc.) so the graphs/timbres are already
   sensible. Pick one whose name matches the target sound.
2. Then **tweak the exposed macro-ish knobs** below to steer it — the ones you can actually reach
   are the part/master volumes, A/B mix, unison, prism, filter cutoff/resonance, pluck, tremolo,
   master volume and the FX levels. Treat everything else as "set by the preset."
Confirm what's reachable for this instance by reading the live param list before designing.

## Parts A & B (the two timbre banks)
Harmor has two independent synthesis parts, **A** and **B**, with identical controls; you edit one
at a time via the A/B switch and blend them with Part mix. Each part's core controls:

- **Part mix (A/B)** — crossfade/balance between Part A and Part B. Center = both; hard left = only
  A; hard right = only B. The main way to layer two timbres.
- **Timbre 1 / Timbre 2 + Mix** — each part has two single-cycle waveform windows (default saw and
  square) and a **Mix** knob that crossfades between them. NOTE: the waveform *shapes* are drawn/
  imported in a window (not host-exposed); the **Mix** crossfade knob is the reachable macro.
- **Blend mode** — how Timbre 1 and 2 combine (Fade, Subtract, Multiply, Maximum, Minimum, Pluck).
  Menu, usually preset-set.
- **Sub** — sub-harmonic level(s) for added bass weight, configured "Around" the fundamental or
  "Below" it (octaves down).
- **Prot (Protection)** — stops the filters from removing the first few harmonics (keeps body/bass).
- **Volume / Vol / Env / FX (per part)** — the part's contribution and its envelope/effect send.

### Pitch (per part)
- **Freq** — coarse frequency multiplier/divider of the partials (octave or Hz distribution).
- **Detune** — offsets the partials from the fundamental for thickness/inharmonicity.
- **Pitch Env** — bipolar amount of the pitch envelope (preset-driven graph supplies the shape).
- **Vibrato Depth / Vibrato Speed** — pitch LFO depth and rate.

## Unison (per part — instant thickness / supersaw)
Harmor's unison is per-note detuned voice stacking, the key to supersaws and lush widths:
- **Order** — number of unison subvoices (more voices = thicker + more CPU).
- **Pitch** — detune spread across the voices (the "supersaw spread" amount).
- **Pan** — stereo spread of the voices (width).
- **Phase** — starting-phase variation between voices (100% recommended for high notes).
- **Type** — spread flavor (Classic, Uniform, Blurred, Random, etc.). Menu.
- **Alt** — alternates gain polarity across voices for a different beating character.

## Filter section (two additive filters)
Two additive filters (**Filter 1** and **Filter 2**) with a mix/routing knob between them. Because
these are additive, they are extremely clean and can take shapes no analog filter can. Per filter:
- **Type** — Low pass, Band pass, Band stop, High pass, Phaser, or custom shapes. Menu.
- **Freq** — cutoff frequency (the primary "sweep" control).
- **Width** — filter slope/bandwidth (how steep, roughly dB/oct).
- **Res** — resonance / peak boost at the cutoff.
- **Res type** — resonance shape (Classic, Pedestal, Wide bump, Well, Noise, etc.). Menu.
- **Res width** — narrowness→width of the resonant peak.
- **Ofs** — resonance offset from cutoff (±cents), for detuned/formant-like peaks.
- **Osc** — self-oscillation sine level at the cutoff.
- **Filter Env** — bipolar amount of the filter envelope (the classic filter-sweep motion; the
  envelope *shape* is a preset-driven graph).
- **KB track** — keyboard tracking of the cutoff.
- **Filter 1&2 Mix** — routes the two filters **parallel** (knob left of center) or **series**
  (right of center), and balances them. This is how you mix filter TYPES (e.g. LP + BP).

## Prism (additive harmonic re-tuning / inharmonicity)
Prism is unique to additive synthesis: it retunes the harmonics away from their integer ratios,
turning a harmonic tone metallic/bell-like/inharmonic. Per part:
- **Amt** — how far the harmonics are shifted from perfect harmonicity (0 = normal/harmonic; up =
  increasingly inharmonic, glassy, bell/metallic).
- **Mode** — additive vs multiplicative shifting.
- **From vol** — makes the prism amount depend on each harmonic's volume (normal or inverted).

## Harmonizer, blur, phaser (spectral shapers, per part)
- **Harmonizer** — clones the spectrum upward into stacked harmonic copies (chord/organ/choir-ish
  thickening): **Amt** (mix), **Width** (how far up it clones), **Str** (emphasis on upper clones),
  **Shift** (transposition of the clones), **Gap** (spacing between cloned harmonics).
- **Blur** — smears the spectrum over time for softness, pads, "reverb-in-the-synth" movement:
  **Mix** (wet/dry), **Time** (attack/decay smear amount), **Harm** (vertical harmonic smear).
  Great for turning a static tone into an evolving pad.
- **Phaser** — additive frequency-cancellation phaser: **Mix** (amount), **Type** (menu),
  **Width** (aggression), **Speed** (sweep rate/direction), **Ofs** (mask position), **LFO**,
  **KB.t** (key tracking).

## Pluck & Tremolo
- **Pluck** — imposes a decaying, string/plucked attack via a decay filter: **Time** (decay-length
  multiplier), **Blur** (uses a dedicated pluck blur instead of the main Blur when engaged). The
  fastest route to plucked-string, harp, mallet and pizzicato characters.
- **Tremolo** — stereo amplitude modulation: **Depth** (wet/dry), **Speed** (rate), **Gap** (L/R
  pan extent; 0 = mono volume tremolo, up = auto-pan).

## Image / audio resynthesis (advanced, mostly non-param)
Harmor can analyze a **dropped audio file or image** and play it back additively (Resynthesis mode)
or convert it to editable gain/frequency image planes (Image synthesis mode). Playback macros like
**C / F** (coarse/fine speed), **Time** (start offset/scrub), **Sharp** (transient sharpen),
**Form** (formant shift, avoids the "chipmunk effect"), and the looping menu exist — but the source
data and image planes themselves are loaded/drawn, not host-exposed. Set these up from a preset or
by loading a sample; steer only the exposed playback knobs by name.

## Master & FX (global, post-synthesis)
- **Pre FX volume** — output level before the FX chain.
- **Post FX volume** — output level after the FX chain (the true master out; use this as "volume").
- **Master Pitch** — global playback pitch offset.
- **LFO** — global scale for all active LFO envelopes (bipolar).
- **Vel** — links note velocity to volume.

Global effects chain (each is bypassable/preset-driven; steer the mix/amount knobs):
- **Distortion** — **Amt** (drive), **Asym** (asymmetry), **Wet** (mix), **Filter** (post LP),
  **Type** (menu). Adds grit/warmth/edge.
- **Chorus** — **Order** (voices), **Depth**, **Speed**, **Del**, **Spread** (width), **Mix**.
- **Delay** — **Feedback**, **Time** (tempo-synced), **Vol**, **S.ofs** (ping-pong), mode switches.
- **Reverb** — **Size**, **Diff**, **Dec** (decay), **Damp**, **Vol**, plus preset/color menus.
- **Compression** — Maximus-based: **Type** (menu), **Amt** (dry/wet), **L / M / H** (per-band).

## Sound-design recipes (concrete steps)
Load a matching preset first, then set the named macros below (map names→indices at runtime).

- **Huge supersaw** — thick, wide, moving.
  - Start from a saw/supersaw preset (Timbre = saw).
  - Unison **Order** high (e.g. 7–9), Unison **Pitch** moderate-high (the detune spread), Unison
    **Pan** high for width, Unison **Phase** ~100%.
  - Blur **Mix** slightly up + Blur **Time** small to smear the attack and glue the voices.
  - Open Filter 1 **Freq** most of the way; add a little **Res**. Finish with **Chorus** and
    **Reverb** Vol for lushness. Post FX volume to taste.

- **Evolving pad** — slow, breathing, alive.
  - Start from a pad/string preset.
  - Prism **Amt** small-to-moderate for shimmer/inharmonic motion; Blur **Mix** up and Blur **Time**
    large for a soft, smeared, reverberant body.
  - Filter 1 **Freq** lowered with **Filter Env** positive so the cutoff opens slowly (envelope
    shape comes from the preset); add **Reverb** (large **Size**, long **Dec**). Vibrato **Depth**
    tiny for life.

- **Plucked string** — percussive attack, natural decay.
  - Start from a pluck/guitar/harp preset.
  - Engage **Pluck** and set **Time** short-to-medium for the decay length.
  - Filter 1 **Res** up a bit and **Freq** mid for a resonant, string-like body; keep **Prot** on so
    the fundamental survives. A touch of **Prism Amt** adds string inharmonicity/realism. Short
    **Reverb**/**Delay** for space.

- **Vocal-ish / formant tone** — choir/vowel character.
  - Start from a vocal/formant/choir preset (or resynthesize a vocal sample).
  - Use **Harmonizer** (Amt + Width) to stack harmonics into a choir-like body; nudge the resynth
    **Form** (formant) to move the vowel without changing pitch.
  - Filter **Res type** to a formant-ish shape with two resonant peaks (Filter 1 + Filter 2 via the
    1&2 **Mix** in parallel), **Res** up, **Freq** in the vocal range. Add slow **Tremolo**/vibrato.

## Tips
- Harmor is preset-and-graph driven: get 90% of the way with a preset, then automate only the
  reachable macros (part/master volume, A/B mix, unison, prism, filter Freq/Res, pluck, tremolo,
  FX). Don't try to "build from scratch" by index — the timbres, images and envelopes aren't
  host-exposed.
- The single most useful live controls are **Filter Freq** (sweeps), **Unison Pitch/Order** (width),
  **Prism Amt** (inharmonic shimmer), **Blur** (motion/softness) and **Post FX volume**.
- Two filters + the 1&2 Mix let you combine filter TYPES (e.g. LP in series after a BP) — use it for
  formants and complex shapes, not just a single cutoff.
- Unison and high partial/HQ settings are CPU-heavy; keep Order sane and lean on the preset's
  quality settings unless you specifically need more.
- Harmor is dynamic on its own; still, mixer EQ/compression downstream helps it sit in a mix.
