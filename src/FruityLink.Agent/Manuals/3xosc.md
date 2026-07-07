---
name: 3xOSC
aliases: 3x osc, 3xosc, three osc, threeosc, triple osc, 3 osc, 3-osc, tripleoscillator
type: generator
category: Subtractive synth (oscillators)
summary: FL's default 3-oscillator subtractive synth — the go-to for saw leads, sub bass, plucks and supersaws.
---
# 3xOSC (generator)

Three independent, band-limited oscillators summed into one voice. FL Studio's default channel
synth and the fastest way to build classic subtractive tones (saw/square leads, sine subs, PWM-ish
pads, detuned supersaws). It has NO built-in filter or envelope beyond the channel's — shape the
timbre with the oscillator mix + detune here, then a mixer EQ/filter downstream.

## How the AI drives it
This manual gives PARAMETER NAMES + INTENT. The exact numeric parameter INDICES are discovered at
runtime — 3xOSC is a channel (generator) plugin, so:
1. `native_list_channel_plugin_params(channel)` → read the live name→index map for this instance.
2. `native_set_channel_plugin_params(channel, index, value)` → set each param by the index you found.

Never hard-code indices from this manual; always map the names below to indices via the list call
first (order can differ by FL version). Param values are normalized (typically 0..1 unless the list
call reports otherwise) — read a param back if unsure of its scale.

## Per-oscillator parameters (OSC 1, OSC 2, OSC 3)
Each of the three oscillators exposes the same controls:
- **Shape / Waveform** — the oscillator's wave. Options: **sine, triangle, square, saw, rounded saw
  (softer saw), noise, custom** (a user-drawn/imported single-cycle shape). Sine = pure/sub; saw =
  bright & buzzy (leads/bass); square = hollow/woody; triangle = soft & flute-like; noise = texture.
- **Coarse** — pitch in semitones (± a couple octaves). ±12 = one octave. Use to stack octaves/fifths.
- **Fine** — fine detune in cents (±100). Small offsets (±5..±15) between oscillators = width & thickness.
- **Panning (Pan)** — stereo position of this oscillator. Spread osc 2/3 left/right for a wide image.
- **Volume (Level)** — this oscillator's contribution to the mix. Turn an oscillator's volume to 0 to
  disable it (e.g. a pure single-oscillator sub).
- **Phase** — start phase of the wave. Randomize/offset per-osc to avoid identical phase stacking; a
  fixed phase gives a consistent transient (useful for tight plucks/kicks).

## Global parameters
- **Phase randomness (Phase rand)** — randomizes phase each note; higher = livelier, less "static".
- **Stereo detune / Detune** — spreads the three oscillators apart in pitch for instant width without
  manual per-osc fine tuning (the quickest supersaw thickener).

## Sound-design recipes (concrete steps)
Each step is "set OSC<n> <param> = <value>"; map names→indices at runtime as above.

- **Fat saw lead** — 3 detuned saws.
  - OSC1 shape=saw, fine=0
  - OSC2 shape=saw, fine=+8 cents, pan slightly left
  - OSC3 shape=saw, fine=-8 cents, pan slightly right
  - All three volumes roughly equal; add a touch of global stereo detune for extra width.

- **Sub bass** — single clean sine, down an octave.
  - OSC1 shape=sine, coarse=-12 (one octave down), fine=0, pan=center
  - OSC2 volume=0, OSC3 volume=0 (disable the other two so the sub stays mono & pure)

- **Square pluck** — hollow, short.
  - OSC1 shape=square, fine=0
  - OSC2 shape=square, coarse=+12 (octave up), volume ~40%
  - OSC3 volume=0; fix OSC phases for a consistent transient. Shorten with the channel/mixer envelope.

- **Hollow / PWM-ish** — fake pulse-width by detuning two squares.
  - OSC1 shape=square, fine=0
  - OSC2 shape=square, fine=+12..+20 cents (beating creates the moving, hollow PWM character)
  - OSC3 volume=0; add a slow LFO to OSC2 fine (via automation) for classic PWM motion.

- **Supersaw-ish spread** — big, wide, bright.
  - All three OSC shape=saw
  - OSC2 fine=+15, OSC3 fine=-15
  - OSC1 pan=center, OSC2 pan=left, OSC3 pan=right
  - Raise global stereo detune moderately; follow with a mixer reverb/chorus for lushness.

- **Octave stack (organ-ish body)** —
  - OSC1 shape=sine/triangle, coarse=0
  - OSC2 same shape, coarse=+12
  - OSC3 same shape, coarse=-12; balance volumes so no single octave dominates.

## Tips
- Detune is thickness, not tuning: keep fine offsets under ~±20 cents or the patch sounds out of tune.
- 3xOSC is dry and static by design — the "character" usually comes AFTER it: filter/EQ, drive,
  chorus and reverb on the channel's mixer track. Document those on the effect manuals.
- For mono bass, disable oscillators 2 and 3 (volume=0) and keep pan centered to avoid phase issues.
