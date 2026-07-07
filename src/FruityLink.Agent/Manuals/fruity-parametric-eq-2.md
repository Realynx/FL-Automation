---
name: Fruity Parametric EQ 2
aliases: eq, parametric eq, peq2, fruity eq, fruity parametric eq 2, parametric eq 2, eq2, fruity eq 2, 7 band eq, 7-band eq, fruityparametriceq2, fruity eq2, param eq, param eq 2
type: effect
category: Equalizer (EQ / filter)
summary: FL's main 7-band parametric EQ — corrective and tonal shaping. Place EARLY in a mixer chain, before dynamics/reverb.
---
# Fruity Parametric EQ 2 (effect)

FL Studio's flagship equalizer: a 7-band parametric EQ with a full-featured spectrum analyzer, per-band
filter shapes, and both Standard (IIR, near-zero latency) and Linear Phase (FFT) processing modes. Users
say "EQ", "the EQ", "parametric EQ" — this manual answers to all of those. It is a MIXER (effect) plugin —
it processes whatever is routed through its mixer track slot. Its job is corrective (removing problems:
mud, boxiness, harshness, resonances, rumble) and tonal (shaping character: warmth, air, presence) shaping
of a signal's frequency balance.

## How the AI drives it
Parameter NAMES + INTENT are below; exact numeric INDICES are discovered at runtime. Fruity Parametric
EQ 2 is an effect on a MIXER track, so:
1. `native_list_mixer_plugin_params(mixerTrack, slot)` → read the live name→index map for this instance.
2. `native_set_mixer_plugin_params(mixerTrack, slot, index, value)` → set each param by that index.

Map the names below to indices via the list call first; do not assume a fixed order or that all 7 bands
expose identically-named params — read the live list and match by substring (e.g. "Band 3", "Freq",
"Gain"). Values are normalized (typically 0..1 unless the list call reports otherwise); read a param back
if unsure, especially for frequency (log-scaled) and gain (bipolar, centered at 0 dB) params.

## When to use
- Corrective cleanup: cut mud (~200–500 Hz), boxiness, harshness (~2–5 kHz), or rumble/sub garbage
  (< 40–60 Hz) on any source.
- Carve frequency pockets so instruments stop fighting for the same space (e.g. cut kick around the
  bass's fundamental, cut bass around the kick's click).
- Tonal shaping: add air/sheen up top, add warmth low-mid, tilt the tonal balance of a bus or the master.
- High-pass non-bass sources (vocals, guitars, synths, hats) to remove unnecessary low-end energy and
  free up headroom for the kick/bass.
- Find and notch out resonances, ringing, or feedback-prone frequencies.
- Prefer CUTTING problem frequencies over boosting everything else — subtractive EQ is cleaner and uses
  less headroom than additive EQ.

## Chain placement
- Put EQ **EARLY** in a mixer chain — typically the FIRST or second slot. Corrective EQ shapes the raw
  signal before anything else reacts to it.
- EQ generally goes **BEFORE compression** (dynamics): removing problem frequencies first stops a
  compressor from "pumping" in reaction to a boomy low end or a harsh spike, and stops it from
  over-compressing because of energy you're about to remove anyway.
- EQ goes **BEFORE reverb/delay** (see Fruity Reeverb 2): reverbing a muddy or harsh signal smears those
  problems into the tail. Clean with EQ first, then add space/time-based effects near the end of the chain.
- A second, gentler "tonal polish" EQ instance can sit LATER in the chain (after compression/saturation)
  for final tone shaping — but the first, corrective pass belongs early.
- On a bus/group or the master track, an early high-pass and gentle corrective EQ is common before glue
  compression.

## Parameters (names + intent)

### Global
- **Gain / Main Level / Output Gain** — overall output trim of the whole EQ (post all bands). Use to
  compensate level changes caused by band boosts/cuts, not as a tonal tool.
- **Processing mode (Standard IIR / Linear Phase FFT / HQ variants)** — Standard (IIR) is near-zero
  latency and cheap; Linear Phase avoids phase smearing (useful on parallel/bus processing or mastering)
  at the cost of latency and CPU. Leave on default (Standard) unless phase-coherence is specifically
  needed; not something to change to solve a tonal problem.

### Per band (there are 7 bands, each with the same control set)
- **Band N Enable / On** — turns that band's filter on/off. Disable unused bands rather than leaving
  them at 0 gain/neutral — cleaner and cheaper.
- **Band N Frequency (Freq)** — the center/corner frequency the band acts on (log-scaled across the
  audible range, roughly 20 Hz–20 kHz). This is WHERE the band works.
- **Band N Gain** — how much that band boosts (+dB) or cuts (−dB) around its frequency, centered at
  0 dB = no change. Only meaningful for shelf and peak/peaking (bell) shapes — Low Pass, High Pass, Band
  Pass and Notch remove/isolate frequencies structurally and typically have no gain control.
- **Band N Bandwidth / Q** — how WIDE the band's effect is around its center frequency. Narrow
  bandwidth (high Q) = surgical, affects a thin slice (good for notching a resonance); wide bandwidth
  (low Q) = broad, musical, affects a large swath (good for gentle tonal shelving/bell moves).
- **Band N Type / Shape** — the filter shape for that band. One of: **Low Shelf**, **High Shelf**,
  **Peaking** (bell — boost or cut centered on Frequency), **Notch** (narrow, deep cut — for resonances),
  **Band Pass** (isolates a narrow range, cuts everything outside it), **Low Pass** (removes everything
  ABOVE Frequency), **High Pass** (removes everything BELOW Frequency).
- **Band N Slope / Order** — steepness of the roll-off, applicable to Low Pass / High Pass / Band Pass /
  Notch shapes (e.g. 2, Gentle 4/6/8, Steep 4/6/8 = −12 dB/Oct up to −48 dB/Oct). Steeper = more
  aggressive, more surgical removal; gentler = more natural, less phase disturbance.

There is no fixed mapping of "Band 1" to a fixed frequency range — any of the 7 bands can be set to any
type/frequency. Read the live param list and pick whichever band index is free/appropriate; conventionally
people order bands low-to-high by frequency for readability, but the plugin does not require it.

## Recipes (concrete steps)
Each step is "set <param> = <intent>"; map names→indices at runtime as above. Use an unused/disabled band
for each move and Enable it.

- **High-pass a bus (remove unneeded low end)** —
  - Pick a band → Type = High Pass
  - Frequency = ~80–120 Hz for full mixes/busses, ~100–150 Hz for thin sources (vocals, hats, most
    synths), lower (~30–40 Hz) only to remove true sub-sonic rumble on a full mix/master.
  - Slope = moderate–steep (e.g. Gentle 4 to Steep 4) depending on how aggressively you need to clear
    the low end without affecting the fundamental of the source.
  - Enable the band.

- **Tame boxy/muddy mids (~300–500 Hz)** —
  - Pick a band → Type = Peaking
  - Frequency = 300–500 Hz (sweep within this range while listening to find the exact boxy spot)
  - Gain = negative, roughly −2 to −6 dB
  - Bandwidth = medium (not too narrow — boxiness is usually a broad-ish buildup, not a single spike)
  - Enable the band.

- **Add air (~10–12 kHz)** —
  - Pick a band → Type = High Shelf
  - Frequency = 10–12 kHz
  - Gain = positive, roughly +2 to +4 dB (small moves — air is easy to overdo/turn harsh)
  - Bandwidth = wide/gentle for a smooth lift rather than a peaky boost
  - Enable the band.

- **Notch a resonance / ringing frequency** —
  - Sweep first to find it: temporarily set a band to Peaking, Gain = strong boost (+10–15 dB), narrow
    Bandwidth, and sweep Frequency until the offending resonance jumps out.
  - Once located, switch Type = Notch (or keep Peaking with a strong negative Gain), set Frequency to
    the exact spot found, and set Bandwidth = very narrow (high Q) for a surgical, minimally-invasive cut.
  - Gain = strongly negative (Notch shape may not expose Gain — depth is structural; for Peaking use
    roughly −6 to −12 dB or more as needed).
  - Enable the band; disable/remove the temporary sweep band if a separate one was used.

- **Low shelf for warmth** —
  - Pick a band → Type = Low Shelf
  - Frequency = ~100–250 Hz
  - Gain = small positive boost, roughly +1.5 to +3 dB (watch for low-end buildup/mud when stacked with
    other warm sources — cut elsewhere if the mix gets muddy rather than pushing this further)
  - Bandwidth = wide/gentle for a smooth, musical lift
  - Enable the band.

## Tips
- "Sweep and destroy": to find a problem frequency by ear, temporarily boost a band hard with a narrow
  bandwidth and sweep its Frequency until the problem is loudest/most obvious, then either cut at that
  spot (correction) or notch it out (resonance removal), then narrow/widen bandwidth to taste.
- Prefer cutting over boosting: subtractive moves (cut the problem) usually sound cleaner and preserve
  headroom better than boosting everything around a hole.
- Low Pass / High Pass / Band Pass / Notch shapes structurally remove frequencies and typically have no
  Gain param — don't try to set Gain on them; use Frequency + Slope/Bandwidth instead.
- Keep boosts modest (a few dB) and stack multiple gentle moves rather than one extreme one — extreme
  single-band boosts are more prone to sounding unnatural or introducing artifacts.
- Disable bands you're not using instead of leaving them at neutral gain — keeps the chain readable and
  slightly cheaper on CPU.
- If a bus/master still sounds muddy or harsh after other fixes, revisit this EQ before reaching for more
  compression or saturation — dynamics and distortion react to (and can amplify) frequency-balance
  problems that EQ should fix first.
