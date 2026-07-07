---
name: Serum
aliases: serum, xfer serum, wavetable
type: generator
category: Wavetable synth (dual-osc + sub + noise, 2 filters, mod matrix, FX rack)
summary: Xfer Serum — a third-party wavetable VST3 (dual wavetable OSC A/B + Sub + Noise, two filters, LFO/env mod matrix, macros, built-in FX rack). Great for supersaw leads, growl basses, plucks and huge pads.
---
# Serum (generator)

Xfer **Serum** is a wavetable synthesizer we host as a third-party VST3 generator. Voices are built
from **two wavetable oscillators (OSC A + OSC B)**, a **Sub** oscillator and a **Noise** generator,
run through **two multimode filters**, shaped by a **modulation matrix** (multiple LFOs + envelopes),
steered by four **Macros**, and finished by a **built-in FX rack** (Hyper/Dimension, Distortion, EQ,
Compressor, Delay, Reverb, Chorus, Flanger, Phaser). Unlike FL's 3xOSC, Serum has its own filters,
envelopes and effects, so most tone-shaping happens **inside** Serum via the params below.

## How the AI drives it
Serum is a third-party VST — but because it exposes a flat host-parameter list, we control it through
the **same channel-plugin path as native FL synths**. Parameters are addressed **BY NAME** (the names
below are the real ones Serum publishes; our probe confirmed strings like `"Filter 1 Freq"`,
`"Filter 1 Res"`, `"A Level"`, `"Main Vol"`, `"Macro 1"`, `"Osc A Warp"`):
1. `native_list_channel_plugin_params(channel)` → read the live name→index map for this Serum instance.
2. `native_set_channel_plugin_params(channel, index, value)` → set each param by the index you found.

Never hard-code indices from this manual; always map the NAMES below to indices via the list call
first (order can shift by Serum version). **Values are normalized 0..1** — e.g. Filter Freq 0=lowest
cutoff, 1=fully open; a "Level" at ~0.7 is a comfortable loud. Read a param back if unsure of its scale.

### IMPORTANT: wavetable SELECTION is not a normalized param
Serum's wavetables live **internally** (they are named tables/files loaded in Serum's own browser,
not host automation values). There is **no normalized host param to pick a wavetable by file**. So do
**not** try to "load a wavetable" through params. Instead steer the sound with what *is* exposed:
**WT Position (Warp/table scan), Unison + detune/blend, Warp mode amount, Level/Pan, filter, macros
and FX**. Assume whatever table is currently loaded and sculpt from there.

## Oscillator parameters — OSC A and OSC B
Both wavetable oscillators expose the same controls (prefixed `Osc A …` / `Osc B …`, with a few short
forms like `A Level`):
- **WT Position / Osc A Pos** — the scan position through the wavetable (which single-cycle frame is
  played). This is the primary "wavetable" timbre control; sweep it (often via an LFO/env in the mod
  matrix) for the classic evolving wavetable motion.
- **Osc A Warp** *(warp mode amount)* — applies the currently-selected **warp mode** to the table.
  Warp modes include **Sync, Bend, PWM, Asym, Flip, Mirror, Quantize, Remap** etc.; this param is the
  *amount/depth* of that mode. Drive it for sync-sweeps, PWM motion, hard edges. (The warp *mode* name
  itself is a menu selection, similar to a wavetable choice — treat amount as the automatable control.)
- **Unison** — number of stacked voices (1..16). More voices = thicker/supersaw. The width engine.
- **Unison Detune** — pitch spread between unison voices. Small = subtle chorus; large = screaming
  supersaw. The main "fatness" knob once Unison > 1.
- **Unison Blend** — balance between the center voice and the detuned outer voices (how present the
  spread is in the mix).
- **A Level / Osc A Level** — this oscillator's contribution to the voice mix. Set B Level to 0 to run
  OSC A alone; blend A+B for hybrid timbres.
- **Osc A Pan** — stereo position of this oscillator.
- **Octave / Semi / Fine** — tuning: **Octave** (whole octaves), **Semi** (semitones, e.g. +7 = fifth,
  +12 = octave), **Fine** (cents, for subtle detune/thickening between A and B).
- **Phase** — start phase of the table read.
- **Rand** — per-note phase randomization (livelier, less static/identical stacking).

Use OSC B as a detuned/octave partner to OSC A, or give it a different warp/table for a layered hybrid.

## Sub oscillator
A simple, clean low-end oscillator independent of the wavetables:
- **Sub Osc Level / Sub Level** — how much sub is added. The go-to for solid bass weight under a bright
  wavetable.
- **Sub Octave** — octave of the sub (typically −1/−2 under the mains for foundation).
- **Sub Shape / waveform** — sine/triangle/square/etc. (Sine = purest sub.)
- **Sub Direct** — routes the sub to bypass the filters (keeps low end clean/undivided by filter cutoff).

## Noise generator
- **Noise Level** — level of the noise sample layer. Adds air/texture, attack transients or grit.
- **Noise Pitch** — pitch/scan of the noise sample (Serum's noise is a selectable sample, but again the
  *sample choice* is internal; level + pitch are the automatable handles).
- Great as a short percussive transient (via a fast envelope on Noise Level) for plucks/keys.

## The two FILTERS
Serum has a primary filter (**Filter 1**) plus a second filter (**Filter 2**) and a routing balance
that decides how much of each oscillator hits the filter(s):
- **Filter 1 Freq** *(cutoff)* — the main brightness control. 0 = dark/closed, 1 = fully open. The
  single most expressive param; modulate it from an envelope or LFO for movement.
- **Filter 1 Res** *(resonance)* — emphasis at the cutoff. Higher = more whistle/acidic; extreme values
  can self-oscillate depending on type.
- **Filter Drive / Fil Drive** — pre/in-filter saturation. Adds harmonics, grit and loudness; the key
  ingredient for aggressive reese/growl basses.
- **Filter Type** — the filter mode (menu): **LP/BP/HP (12/24 dB), Notch, Comb, Phaser-style, Flanger,
  and Serum's special formant/vowel and multiband types**. LP24 for classic bass/lead; comb/formant for
  vocal/metallic character. (Type is a menu selection like a table choice — pick it in Serum; automate
  Freq/Res/Drive.)
- **Filter Pan / spread, Fat, Mix** — extra shaping depending on type (stereo, thickness, dry/wet).
- **A > Filter / B > Filter (filter routing balance)** — per-oscillator switch/amount for whether OSC A
  and OSC B (and Noise/Sub) are sent through the filter. Use this to filter the bright wavetable while
  leaving the sub clean, or to blend filtered/unfiltered signal.
- **Filter 2** — a second filter (series/parallel with Filter 1 via the filter routing) with its own
  Freq/Res/type for more complex, dual-band tone shaping.

## Global ENV / LFO / Macros (modulation)
Serum's motion comes from its mod sources routed in the **mod matrix**. You mostly automate the
*destinations' amounts* and the sources' *rates/times*:
- **Env 1 (Amp env)** — the always-on amplitude envelope: **Env 1 Atk / Dec / Sus / Rel**. Fast Atk +
  short Dec/low Sus = pluck; slow Atk + high Sus = pad.
- **Env 2 / Env 3** — spare envelopes to route (in the matrix) to **Filter Freq**, WT Position, Warp,
  etc. A fast Env→Filter Freq is the classic "pluck/zap" filter transient.
- **LFO 1..4** — low-freq oscillators with **Rate** (free Hz or tempo-synced) and shape; route to
  Filter Freq (wobble), WT Position (evolving table), Warp (PWM), Pan (auto-pan), etc.
- **Macro 1..4** *(`Macro 1` … `Macro 4`)* — four master knobs, each 0..1, that can be mapped (in
  Serum) to *many* destinations at once. **Macros are the AI's best friend**: assign a macro to, say,
  Filter Freq + Drive + Unison Detune, then automate the single `Macro 1` param for a whole-patch
  morph. If a patch already has macros mapped, sweeping them is the easiest expressive control.
- **Main Vol** — Serum's master output volume (the overall patch loudness). **Master Tune / Porta
  (glide)** are also global.

## The FX rack
Serum's built-in effects, each with an on/off + a handful of params (all normalized). Order in the rack
matters. Key params per unit:
- **Hyper / Dimension** — a unison/dimension expander that adds supersaw-style width and chorusing
  *inside* Serum. **Hyper Amount / Detune** thickens leads massively.
- **Distortion** — multi-mode drive (tube, diode, waveshaper, downsample, etc.): **Dist Drive / Amount**
  for grit and loudness; the core of aggressive basses/leads.
- **EQ** — a simple built-in equalizer (a few bands: low/mid/high **Freq / Gain**) to carve or boost.
- **Compressor** — **Comp Threshold / Ratio / Attack / Release** to glue, pump or tame dynamics.
- **Delay** — **Delay Time (often tempo-synced) / Feedback / Mix**, with ping-pong/stereo options for
  space and rhythmic echoes.
- **Reverb** — **Reverb Size / Decay / Mix / (Lo/Hi cut)** for space; the main "put it in a room/hall"
  control for pads and leads.
- **Chorus** — **Chorus Rate / Depth / Mix** for lush stereo widening (a subtler alternative to Hyper).
- **Flanger** — **Flanger Rate / Depth / Feedback** for jet/sweep motion.
- **Phaser** — **Phaser Rate / Depth / Feedback** for swirly notch movement.

Enable only what you need; e.g. Reverb+Chorus for pads, Distortion+Compressor for bass, Hyper+Delay for
leads.

## Sound-design recipes (concrete steps)
Each step is "set `<Serum param name>` = <normalized 0..1 value>"; map names→indices at runtime first.
Values are approximate starting points — read back and taste.

- **Supersaw lead** — big, wide, bright (via OSC A unison + detune).
  - `Unison` (OSC A) ≈ 0.5..0.7 (≈ 7..12 voices)
  - `Unison Detune` ≈ 0.25..0.4 (spread; more = wider/screamier)
  - `Unison Blend` ≈ 0.7 (present outer voices)
  - `A Level` ≈ 0.8; `Sub Osc Level` ≈ 0.2 for body
  - `Filter 1 Freq` ≈ 0.8 (open/bright), `Filter 1 Res` ≈ 0.15
  - Add width/space: `Hyper Amount` ≈ 0.4 and/or `Reverb Mix` ≈ 0.2, `Delay Mix` ≈ 0.15.

- **Growl / reese bass** — dirty, moving, aggressive (filter drive + macro).
  - `A Level` ≈ 0.8, second oscillator `Osc B` detuned a few cents (`Osc B Fine` small) for the reese beat
  - `Sub Osc Level` ≈ 0.4 with `Sub Direct` on (keep lows clean under the filter)
  - `Filter Type` = LP24; `Filter 1 Freq` ≈ 0.35, `Filter 1 Res` ≈ 0.35
  - `Filter Drive` ≈ 0.5..0.7 (the growl/grit)
  - `Dist Drive` ≈ 0.3 for extra harmonics
  - Map `Macro 1` to Filter Freq (+ a little Warp/Detune) and automate `Macro 1` 0→1 for the classic
    talking/wobble sweep (or route an LFO to `Filter 1 Freq`).

- **Bright pluck** — short, snappy (fast env to cutoff).
  - `Env 1 Atk` ≈ 0 (instant), `Env 1 Dec` ≈ 0.2, `Env 1 Sus` ≈ 0.0, `Env 1 Rel` ≈ 0.15 (short)
  - In the matrix route **Env 2 → `Filter 1 Freq`** with a positive amount; `Env 2 Atk` ≈ 0,
    `Env 2 Dec` ≈ 0.15 so the filter snaps open then closes = the "pluck zap".
  - `Filter 1 Freq` base ≈ 0.3 (env opens it), `Filter 1 Res` ≈ 0.25
  - A touch of `Noise Level` ≈ 0.1 on a fast env for attack transient; small `Delay Mix` for depth.

- **Wide lush pad** — slow, huge, evolving (unison + reverb).
  - `Env 1 Atk` ≈ 0.35 (slow fade-in), `Env 1 Sus` ≈ 0.9, `Env 1 Rel` ≈ 0.5 (long tail)
  - `Unison` ≈ 0.5, `Unison Detune` ≈ 0.2, `Unison Blend` ≈ 0.6
  - `Osc B Level` ≈ 0.6 at `Osc B Octave` +1 (shimmer), balance with `A Level`
  - Route an **LFO → WT Position (`Osc A Pos`)** slowly for evolving motion; slow `LFO Rate`
  - `Filter 1 Freq` ≈ 0.6 (soft, not fully open), `Filter 1 Res` ≈ 0.1
  - `Reverb Size`/`Reverb Mix` high (≈ 0.6 / 0.4), `Chorus Depth` ≈ 0.3 for width.

- **Evolving wavetable texture** — motion from the table itself.
  - Keep filter mostly open (`Filter 1 Freq` ≈ 0.7)
  - Route **LFO 1 → `Osc A Pos` (WT Position)** and **LFO 2 → `Osc A Warp`** at slow rates
  - Modest `Unison`/`Unison Detune` so the table motion (not detune) is the star; add `Reverb Mix`.

## Tips
- Filter Freq is your most musical control — modulate it (env for plucks, LFO for wobble) rather than
  leaving it static; that's where Serum's life comes from.
- Prefer **Macros** for expressive automation: sweep one `Macro N` param instead of many params at once,
  since a patch's macros are usually pre-mapped to the sound's "big move".
- Wavetable/warp-mode/filter-type *choices* are internal Serum menu selections, **not** normalized host
  params — set them in Serum, then automate the amounts (`Osc A Pos`, `Osc A Warp`, `Filter 1 Freq/Res`).
- Keep the Sub clean: raise `Sub Osc Level` and enable `Sub Direct` so bass weight isn't lost when you
  close the filter.
- Overall loudness = `Main Vol`; don't confuse it with per-oscillator `A Level` / `Osc B Level`.
