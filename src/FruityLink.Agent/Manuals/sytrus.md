---
name: Sytrus
aliases: sytrus, fm synth
type: generator
category: FM / RM / subtractive synth (6 operators + modulation matrix)
summary: Image-Line's flagship hybrid synth — 6 operators with an FM/RM modulation matrix, 3 filters and deep envelopes; the go-to for FM bells, electric pianos, metallic basses, brass and evolving pads.
---
# Sytrus (generator)

A hybrid semi-modular synthesizer built around **6 operators** (each a tunable oscillator with its
own editable waveform/harmonics, volume, panning and a full set of envelopes) wired together through
a **modulation matrix**. The matrix lets any operator modulate any other operator (or itself) using
**FM (frequency modulation)** or **RM (ring modulation)**, and routes operator output into **3
multimode filters** and the master **Out**. That combination means Sytrus covers three synthesis
worlds at once: FM (bells, electric pianos, metallic/glassy tones), RM (clangorous/inharmonic
textures) and subtractive (filtered saws/pads via its rich oscillator shapes). It is a very
"preset-first" instrument — the fastest path to a good sound is usually to start from a factory
preset and then adjust operator levels, matrix amounts and filter cutoff/resonance.

## How the AI drives it
This manual gives PARAMETER NAMES + INTENT. The exact numeric parameter INDICES are discovered at
runtime — Sytrus is a channel (generator) plugin, so:
1. `native_list_channel_plugin_params(channel)` → read the live name→index map for this instance.
2. `native_set_channel_plugin_params(channel, index, value)` → set each param by the index you found.

Never hard-code indices from this manual; always map the names below to indices via the list call
first (order can differ by FL version). Param values are normalized (typically 0..1 unless the list
call reports otherwise) — read a param back if unsure of its scale.

**Reality check — Sytrus is preset-rich and deep.** Sytrus exposes a large automatable parameter set
(operator volumes/pans, the matrix connection amounts, filter cutoff/resonance/type, master and FX
knobs), but a lot of its power lives in things that are edited graphically in the plugin UI and are
NOT clean host params: the per-operator **oscillator shape / harmonics** drawing, and the exact
**point shapes** of each envelope. The robust strategy is therefore:
- **Start from a preset** that is already in the right ballpark (bell, EP, bass, lead, pad…).
- Then **tweak the exposed params** below: operator **volumes**, operator→operator/operator→Out
  **matrix amounts**, **filter cutoff/resonance/type**, envelope/LFO **amount** knobs, and the
  **master + FX** controls.
- Do not rely on drawing new oscillator waveforms or reshaping envelope curves via host params —
  steer changes toward the knobs that ARE exposed.

## The operators (OP 1 … OP 6)
Sytrus has six identical operators. Each is an oscillator that can be a carrier (heard at the output),
a modulator (shapes another operator and may itself be silent), or both. Per-operator controls:

- **Waveform / oscillator shape** — the operator's single-cycle wave. Defaults to **sine** (the
  classic FM building block); templates for **sine, triangle, square, saw** exist plus fully editable
  **harmonics** and **phase** for custom shapes. NOTE: the shape/harmonic editor is a UI/preset
  feature, not a simple host knob — pick the shape from a preset rather than trying to draw it via
  params. Sine carriers give clean FM; brighter shapes add subtractive-style harmonics up front.
- **Volume (Op level)** — this operator's output amplitude, shaped by its Volume envelope. For a pure
  modulator, its own audible volume can be low/zero (it still modulates via the matrix); for a
  carrier, this plus the operator's **Out** matrix amount set how loud it is.
- **Panning (Op pan)** — stereo position of this operator's output.
- **Pitch / ratio** — the operator's tuning (coarse/fine + center). **This is the heart of FM
  timbre:** the *ratio* between a modulator's pitch and its carrier's pitch decides the harmonic
  content. **Integer ratios** (1:1, 2:1, 3:1, 14:1 …) give harmonic, musical tones (EPs, basses,
  organs); **non-integer ratios** (e.g. 1:1.41, 3.5:1) give inharmonic, metallic/bell/clang tones.
- **Per-operator envelopes** — each operator carries a full envelope for **Volume, Panning, Pitch,
  Mod X, Mod Y, Phase**, plus **Filter cutoff** and **Filter resonance**. Each envelope is a
  multi-point graph with tension/curve and a built-in **LFO** (speed + amount). The *shape* of an
  envelope is drawn in the UI, but envelope/LFO **amount** and **LFO speed** are the knobs to reach
  for from the host. A fast-decaying **modulator Volume envelope** is what makes FM tones bright on
  attack then mellow (the classic DX electric-piano "bite").

## The modulation matrix (the core of FM/RM sound design)
The matrix is a grid that wires the operators together and out to the filters/output:
- **Rows = sources** (the 6 operators, i.e. what does the modulating / where audio comes from).
- **Columns = destinations** — **OP 1…OP 6**, then **Filter 1, Filter 2, Filter 3**, then **Out**.
- A value in the cell where **source operator R (row)** meets **carrier operator C (column)** makes
  **R modulate C**. For operator→operator cells this is **FM** (Sytrus also supports **RM** for
  ring-mod / inharmonic character). Larger amount = deeper modulation = brighter / more harmonics /
  more clang.
- **Diagonal cells (an operator into itself) = feedback.** Self-feedback progressively adds harmonics
  and, pushed hard, edge/noise — the classic route to reedy **brass** and gritty basses.
- **Operator → Filter 1/2/3** cells route that operator's audio *through* a filter (subtractive
  shaping of the FM result).
- **Operator → Out** cells route that operator straight to the master output. A carrier needs a
  non-zero **Out** (or filter→out) amount to be heard; a pure modulator has **Out = 0**.

Matrix connection amounts are automatable, so the AI can build/adjust FM by setting these cell
amounts by name/position from the param list. Think in terms of: which op is the carrier (→ Out),
which op is the modulator (→ carrier), how much FM (the cell amount), and the modulator's pitch ratio.

## The filter section (3 filters)
Three independent multimode filters sit between the operators and the output (operators are routed in
via the matrix Filter columns). Each filter exposes:
- **Type / mode** — filter response: **Low pass, High pass, Band pass, Notch, Low shelf, Peaking,
  High shelf, All pass**, plus the **SVF** (state-variable) variants. Low pass is the workhorse for
  taming FM brightness and for classic subtractive sweeps.
- **Cutoff** — the corner frequency. Lower = darker/muffled, higher = brighter/open. The single most
  useful "make it move" target (sweep it with an envelope or LFO).
- **Resonance (Q / emphasis)** — boost around the cutoff. Moderate = character/vowel; high = whistle
  / near self-oscillation (careful — can get loud and squeally).
- Each filter also has its own **cutoff** and **resonance envelopes** (with LFO), so cutoff can
  attack/decay or slowly sweep on its own. Reach for the filter **cutoff/resonance** knobs and their
  **envelope/LFO amount + speed** for movement.

## Master / global + effects
- **Main volume (Vol)** — overall output level of the instrument.
- **Unison** — stacks detuned voices for width/thickness: **Order** (number of unison voices),
  **Panning** (stereo spread), **Volume**, **Pitch/detune**, **Sub** and **Phase** spread, plus a
  unison **LFO**. Great for fat leads/pads; keep it modest/centered for tight mono bass.
- **Quality / rendering** — HQ / oversampling / interpolation and band-limiting options that trade CPU
  for cleaner high end (raise for final render, lower for live editing).
- **FX tab** — a built-in effects chain applied to the whole instrument:
  - **Chorus** — thickening/widening (depth + rate).
  - **Delay** — echo (time, feedback, stereo).
  - **Reverb** — space/tail (size, decay, diffusion, damping).
  - **EQ / cut** — low-cut and high-cut to clean the low end and tame harsh highs.
  These add the "produced" character on top of the raw synth — useful for pads/leads, usually dialed
  back or off for tight bass.

## Sound-design recipes (concrete steps)
Each step is "set OP<n> <param>" or "set matrix cell <source→dest> amount"; map names→indices at
runtime as above. Where a step needs harmonic drawing or exact envelope curves, prefer starting from
a matching preset and only tweak the exposed knobs.

- **FM bell / electric piano (2-operator FM)** — the canonical FM patch.
  - OP1 = **carrier**: shape=sine, route **OP1 → Out** high; this is what you hear.
  - OP2 = **modulator**: route **OP2 → OP1** with a moderate FM amount; keep **OP2 → Out = 0**
    (silent, modulate only).
  - **Ratio decides the flavor:** integer (e.g. OP2 at 1:1, 2:1 or 14:1 vs OP1) = tonal **electric
    piano**; non-integer (e.g. OP2 pitch ~1.4× OP1) = inharmonic **bell/glass**.
  - **Bite/decay:** a fast-decaying modulator Volume envelope = bright attack that mellows (DX-style
    EP). More modulation amount = brighter/clangier; less = warmer/rounder.
  - Optional gentle low-pass (Filter 1) to soften the top.

- **Fat FM bass** — punchy, harmonically rich low end.
  - OP1 carrier sine → Out; drop pitch an octave (coarse −12).
  - OP2 modulator at a **1:1 or 2:1** integer ratio → OP1, moderate FM amount for body/growl.
  - Add grit with a touch of **feedback** on OP2 (its diagonal cell) or a 3rd operator.
  - Route through **Filter 1 (Low pass)**; use the filter **cutoff envelope** to open briefly on the
    attack for punch, then settle. Keep **unison** low and centered so the bass stays tight/mono.

- **Bright pluck** — short, percussive, glassy attack.
  - 2-op FM as above (OP2 → OP1), integer ratio.
  - Short **carrier Volume envelope** (fast decay, little/no sustain) for the pluck.
  - Modulator envelope also decays fast so the tone is bright at the transient then dulls.
  - Moderate FM amount for brightness; light low-pass to taste. Add a hint of Delay/Reverb from the
    FX tab for space.

- **Evolving pad with LFO on cutoff** — slow, wide, moving.
  - Use richer operator shapes and/or 2–3 carriers → Out for a fuller spectrum.
  - Slow **attack** on the carrier Volume envelope; long **release**.
  - Route through a **Low pass** filter and drive its **cutoff with an LFO** (filter cutoff envelope
    LFO amount + slow LFO speed) for a gentle sweeping motion.
  - Add **unison** (order up, moderate detune/spread) and **Chorus + Reverb** from the FX tab for
    lushness.

- **Brass via feedback** — reedy, harmonically dense, expressive swell.
  - Carrier sine → Out with **self-feedback** (its diagonal matrix cell) to progressively add
    harmonics and that brassy edge; push feedback for more bite.
  - Route through a **Low pass** filter whose **cutoff envelope opens on the attack** (and/or scales
    with velocity) for the classic brass swell, then relaxes.
  - A small **pitch envelope** blip on the attack adds realism. Keep unison modest.

## Tips
- **Think carrier vs modulator.** A carrier is routed to **Out** (heard); a modulator is routed into a
  carrier's matrix cell (shapes it) and usually has **Out = 0**. Confusing the two is the #1 cause of
  "silent" or "wrong" FM patches.
- **Ratio > amount for character; amount = brightness.** Set the harmonic flavor with operator pitch
  *ratios* (integer = musical, non-integer = bell/metallic), then use the matrix cell **amount** and
  the modulator's envelope to dial brightness and how it evolves over the note.
- **Feedback is your friend for edge** — small amounts add harmonics; large amounts approach noise.
- **Start from a preset.** Sytrus's factory library (Bells, EPs, Basses, Leads, Pads, Plucks, Organs,
  Sequences…) is deep; pick the nearest patch, then adjust operator volumes, matrix amounts and
  filter cutoff/resonance rather than building from a bare sine every time.
- **Raise Quality/oversampling only for the final render** — FM can alias; higher quality is cleaner
  but heavier on CPU.
