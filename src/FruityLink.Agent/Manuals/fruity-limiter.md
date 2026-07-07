---
name: Fruity Limiter
aliases: limiter, fruity limiter, maximizer
type: effect
category: Dynamics (compressor / limiter / gate)
summary: FL's all-in-one mastering-grade limiter + compressor + noise gate, with sidechain ducking. Place LAST on the master bus for final loudness/ceiling control; use compressor mode mid-chain for glue/leveling.
---
# Fruity Limiter (effect)

FL Studio's do-everything dynamics processor: a single-band **Compressor** (with sidechain input), a
brickwall **Limiter**, and a **Noise Gate**, combined in one plugin. It is FL's classic master-bus
"maximizer" — most producers meet it as the final loudness/ceiling stage on the master track — but the
compressor and gate sections make it equally useful as a general-purpose bus compressor or a sidechain
ducker anywhere in the mixer. It is a MIXER (effect) plugin — it processes whatever is routed through its
mixer track slot.

The UI is organized as two tab-switched views — **COMP** (compressor) and **LIMIT** (limiter) — plus an
always-visible **Noise Gate** section. Signal flows through the Compressor stage first, then into the
Limiter stage (which contains the integrated Noise Gate and a Saturation control), before output. Each
section (Comp / Limit / Gate) can be engaged independently, so you can use just the gate, just the
compressor, just the limiter, or stack all three.

## How the AI drives it
Parameter NAMES + INTENT are below; exact numeric INDICES are discovered at runtime. Fruity Limiter is an
effect on a MIXER track, so:
1. `native_list_mixer_plugin_params(mixerTrack, slot)` → read the live name→index map for this instance.
2. `native_set_mixer_plugin_params(mixerTrack, slot, index, value)` → set each param by that index.

Map the names below to indices via the list call first; do not assume a fixed order — Compressor, Limiter,
and Gate parameters (and their per-section enable toggles) all live in the same flat param list. Values
are normalized (typically 0..1 unless the list call reports otherwise); read a param back if unsure. Where
a knob is dB-scaled (Gain, Threshold, Ceiling) a small normalized delta can be a large audible change —
nudge and re-check rather than jumping straight to an extreme value.

## When to use
- **Limiter section** — the final stop before output: catch peaks, raise perceived loudness, and enforce
  a hard ceiling so nothing clips downstream (export, streaming platform, or the next plugin in line).
- **Compressor section** — general-purpose leveling/glue: tame dynamic range on a vocal, bus, or the
  master; smooth out performance inconsistency before the signal ever reaches the limiter.
- **Gate section** — remove bleed, hiss, or low-level noise between notes/hits (drum bleed, noisy DI
  guitar, breath noise) by muting the signal when it falls below a threshold.
- **Sidechain (on the Compressor)** — duck one track based on another track's level, the classic
  "pumping" sidechain-compression effect (bass ducking under the kick, or a pad ducking under a lead).

## Chain placement
- **Limiter mode goes LAST.** On the master track, Fruity Limiter (limiter section engaged) should be the
  final plugin in the chain — after EQ, compression, and any other master-bus processing — since its job
  is to set the final ceiling and loudness. Anything placed after it can push levels back over 0 dB.
- **Compressor mode goes MID-CHAIN.** Use the compressor section earlier in a bus or group chain (after
  EQ, before final limiting) to control dynamics and glue elements together before the loudness/ceiling
  stage. A common pattern is: EQ → Compressor (glue) → Limiter (ceiling), which can be two instances of
  this plugin (one per mode) or one instance with both sections engaged.
- **Gate typically goes EARLY**, near the start of a channel's chain, to clean up noise/bleed before any
  compression or EQ amplifies it.
- For sidechain ducking, the "control" track (e.g. the kick) does not need this plugin at all — it just
  needs to exist as a mixer track so it can be selected as the sidechain source on the "ducked" track's
  Fruity Limiter (compressor section).

## Parameters (names + intent)

### Limiter section
- **Gain** — limiter input (pre-limiting) gain. Push the signal into the ceiling harder for more limiting
  and a louder result; pull back for a more transparent pass.
- **Ceiling** — the hard maximum output level; nothing above this level is allowed through. This is the
  brickwall — set it just under 0 dB (e.g. -0.1 to -0.3 dB equivalent) on a master to leave inter-sample
  headroom for lossy export/streaming.
- **Attack** — how quickly the limiter's gain-reduction envelope responds to a peak (uses look-ahead so it
  can catch transients before they clip). Faster attack = tighter peak control but more audible
  gain-reduction artifacts; slower attack = more transparent but risks letting fast transients through.
- **Release** — how quickly gain reduction recovers after a peak passes. Fast release = louder/punchier
  but can pump/distort on sustained material; slow release = smoother/more transparent but can dull
  transients if reduction is still recovering when the next hit lands.
- **Sustain** — the RMS averaging window (0–1000 ms) over which input peaks are averaged before driving
  the limiting envelope. Longer sustain smooths the limiter's response across a phrase (more transparent,
  less peak-reactive); shorter sustain reacts to individual peaks more directly.
- **Saturation** — adds analog-style harmonic saturation/soft clipping as the signal approaches and
  crosses the threshold into the limiting region, adding perceived loudness and warmth/character instead
  of a purely clean clamp. Use sparingly for "glue" and extra loudness; keep low for a transparent master.

### Compressor section
- **Threshold** — the level above which compression begins. Lower threshold = compression engages on more
  of the signal.
- **Ratio** — how strongly the signal is compressed once above threshold (e.g. higher ratio = more gain
  reduction per dB over threshold).
- **Knee** — the sharpness of the transition from uncompressed to fully-compressed. Soft knee = gradual,
  more transparent onset; hard knee = compression kicks in abruptly at the threshold, more aggressive/
  obvious.
- **Gain** — post-compression output (makeup) gain, applied after the compressor stage to restore level
  lost to gain reduction, before the signal reaches the limiter stage.
- **Attack** — how quickly the compression envelope responds to the signal exceeding threshold. Fast
  attack clamps transients immediately (can reduce punch); slow attack lets transients through before
  compression engages (preserves punch/snap).
- **Release** — how quickly the compressor lets go after the signal drops back below threshold. Fast
  release = more responsive/pumping character; slow release = smoother, more sustained gain reduction.
- **Sustain** — RMS averaging window (0–1000 ms) over which the input is averaged to drive the compression
  envelope, similar role to the Limiter section's Sustain but applied to the compressor's detection.
- **Sidechain** — selects an external mixer track as the detection source instead of the plugin's own
  input. When set, the compression envelope is driven by the level of the selected sidechain track instead
  of the track Fruity Limiter is inserted on — this is what enables ducking (see recipe below).

### Noise Gate section
- **Gain** (noise/closed-gate gain) — the signal level allowed through while the gate is closed. At its
  minimum this fully mutes the signal below threshold; raised above minimum it attenuates rather than
  fully silences (useful for gentle noise reduction instead of hard gating).
- **Threshold** — the level below which the gate closes (attenuates/mutes). When the input rises back
  above this level, the gate opens and lets the signal through.
- **Release** — how quickly the gate closes once the signal falls back below threshold. Fast release =
  tight, can chop off decaying tails abruptly; slow release = more natural fade-out as the gate closes.
- Note: the Gate section has no dedicated Attack control — opening is effectively immediate once the
  threshold is crossed; only closing behavior (Release) is shaped.

## Recipes (concrete steps)
Each step is "set <param> = <intent>"; map names→indices at runtime as above.

- **Transparent master limiting (final loudness, minimal artifacts)** —
  - Limiter section engaged, everything else off (or compressor gentle/off if already handled upstream).
  - Ceiling = just under 0 dB (leave a little headroom for downstream export).
  - Gain = raise gradually to taste (drives more of the signal into limiting = louder).
  - Attack = fast-ish (catch transients), Release = medium — avoid extremes that cause audible pumping.
  - Sustain = low-to-medium for a peak-reactive but not choppy response.
  - Saturation = low/off for a clean, transparent result (raise only if you want extra perceived loudness
    and don't mind added color).
  - Place as the LAST plugin on the master mixer track.

- **Glue compression on a bus (group of instruments/drums)** —
  - Compressor section engaged (Limiter section can stay off, or on lightly as a safety ceiling).
  - Threshold = set so the bus is compressed a modest amount on peaks (a few dB of gain reduction, not
    constant heavy squashing).
  - Ratio = low-to-moderate (gentle, musical glue rather than aggressive limiting).
  - Knee = soft, for a smooth, transparent onset.
  - Attack = slow-ish (let transients/punch through), Release = medium-fast (stay responsive between hits).
  - Gain = raise to compensate for the gain reduction (makeup gain) so the bus level matches un-compressed.
  - Place mid-chain: after EQ, before any final limiter on that bus or on the master.

- **Sidechain-duck a bass to the kick (pumping/ducking effect)** —
  - On the BASS mixer track's Fruity Limiter instance, use the Compressor section.
  - Sidechain = select the KICK's mixer track as the sidechain source.
  - Threshold = set low enough that kick hits (via the sidechain signal) reliably trigger gain reduction.
  - Ratio = higher, for an audible/obvious duck (classic EDM pumping) or lower for a subtle, mix-cleaning
    duck (just enough to make room for the kick without an audible pump).
  - Attack = fast (duck immediately on the kick hit), Release = tuned to the tempo/groove — fast enough to
    recover before the next kick if you want rhythmic pumping, slower for a smoother sustained duck.
  - Gain = makeup gain to bring the bass back up between ducks if the overall level drops too much.
  - Note: the kick track itself needs no processing from this plugin — it only needs to be a valid mixer
    track so it can be picked as the Sidechain source on the bass track's instance.

## Tips
- If the master sounds squashed/lifeless, back off Gain (less limiting) and/or lengthen Release before
  reaching for more Ceiling headroom — over-limiting kills dynamics faster than it adds loudness.
- Compress before you limit: let the Compressor section (or an earlier compressor in the chain) do the
  leveling work, and let the Limiter section only catch the remaining peaks — asking the limiter alone to
  do both jobs tends to sound pumpy and distorted.
- On the Gate section, if transients get chopped off, raise Release rather than fighting it with Threshold
  — Threshold controls WHEN it closes, Release controls HOW the closing sounds.
- Saturation is a "to-taste" coloration control, not a required setting — start at zero/low on masters and
  only add it deliberately for extra loudness/character.
- For ducking, prefer a moderate Ratio + tuned Release over an extreme Ratio + Threshold — the goal is
  usually "make room for the kick," not audibly gate the bass out entirely (unless that pumping effect is
  the intended sound).
