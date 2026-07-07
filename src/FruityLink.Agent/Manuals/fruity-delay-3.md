---
name: Fruity Delay 3
aliases: fruity delay 3, delay, fruity delay, delay 3, echo, delay3
type: effect
category: Delay / Echo (time-based)
summary: FL's advanced "analog"-style feedback delay — tempo-synced echoes with a filtered/distorting feedback path, diffusion, and mono/stereo/ping-pong spread. Often on a send, before or parallel to reverb.
---
# Fruity Delay 3 (effect)

FL Studio's advanced feedback delay. Repeats ("echoes") a signal at a set time interval and feeds
those repeats back into itself to create a decaying series of echoes. Its delay Time can be
tempo-synced (and tracks live tempo automation) or run free in milliseconds, its feedback path has a
filter (LP/HP/BP + resonance) plus optional saturation/limiting distortion so the repeats can darken,
narrow, or grit up as they decay, and it can be driven into self-oscillation at high Feedback for
special effects. A Diffusion stage smears/softens the echoes, and a Modulation section can wobble the
delay time and feedback cutoff for tape-style wow/flutter or chorus/flange effects. It is a MIXER
(effect) plugin — it processes whatever is routed through its mixer track slot.

## How the AI drives it
Parameter NAMES + INTENT are below; exact numeric INDICES are discovered at runtime. Fruity Delay 3 is
an effect on a MIXER track, so:
1. `native_list_mixer_plugin_params(mixerTrack, slot)` → read the live name→index map for this instance.
2. `native_set_mixer_plugin_params(mixerTrack, slot, index, value)` → set each param by that index.

Map the names below to indices via the list call first; do not assume a fixed order. Values are
normalized (typically 0..1 unless the list call reports otherwise); read a param back if unsure.

## When to use
- Add rhythmic interest or a sense of space to leads, vocals, plucks, guitars, and other melodic
  elements — a tempo-synced delay "answers" the source in time with the track.
- Slapback (single short, quiet repeat) to thicken vocals/guitars without an obvious echo.
- Widen a mono or narrow source using Ping Pong or Stereo Spread instead of (or alongside) a widener.
- Dub-style effects: high Feedback + a swept/filtered feedback path for repeats that evolve and decay.
- Tape-style motion: Modulation on Time (wow/flutter) or a long Diffusion smear for lo-fi character.
- Be careful with high Feedback + high Resonance — the plugin can self-oscillate (runs away into a
  ringing tone). That is sometimes the goal, but watch levels (the Feedback indicator goes green →
  orange → red).
- Generally avoid heavy, un-filtered delay on kick/bass — the repeats build up low-end mud; if delay
  is used there, high-pass the feedback path (Filter Type = HP) or keep Feedback low.

## Chain placement
- Fruity Delay 3 is commonly used on a **send/bus mixer track** so several sources can share one delay
  and so the delay's own level/repeats can be ridden independently of the dry sources.
- Delay vs. reverb order is a creative choice, both are common:
  - **Delay → Reverb** (delay earlier in the chain, reverb after): the repeats themselves get
    reverberated, gluing the echoes into the space and smoothing the tail. Good for cohesive, ambient
    delay trails.
  - **Delay and Reverb in parallel** (two separate sends fed from the dry source): each effect stays
    distinct and controllable — the classic choice when the delay needs to stay rhythmically crisp
    while the reverb handles space separately.
  - Putting reverb before delay (delay repeats a reverberated signal) is less common — it tends to
    smear the repeats together and lose rhythmic definition; avoid unless that wash is the intent.
- Turn Tempo Sync ON so Time locks to the project BPM (and tracks live tempo automation) for musical,
  in-time echoes; turn it OFF and use free milliseconds for sound-design or non-musical effects.
- On percussive/rhythmic material, avoid stacking heavy Feedback with other time-based effects (long
  reverb, chorus) on the same source — the repeats compound and the mix gets washy/cluttered.

## Parameters (names + intent)

### Input / delay time
- **Wet (Input)** — input level fed into the delay/feedback process. Automate this to apply the delay
  to only part of an incoming signal (e.g., only the tail of a vocal phrase).
- **Time** — the interval between echoes. With **Tempo Sync** ON, set in musical divisions (from
  1/48th of a step up to 16 steps/1 bar — e.g., 1/4, dotted 1/8, 1/16); with Tempo Sync OFF, set freely
  in milliseconds (roughly 1–1000 ms). Right-click → "Set" for pre-defined tempo/time values.
- **Tempo Sync** — locks echoes to the project tempo, including through tempo automation, instead of a
  fixed millisecond time.
- **Keep Pitch** — keeps the pitch of the echoes constant when Time changes. Turn this OFF to get
  pitch-shifting/"tape-speed-change" style effects when Time is automated.
- **Smoothing** — slews (slows down) changes to the underlying Time value, and therefore how fast any
  resulting pitch change happens. Higher = smoother "tape-style" glides between delay times.
- **Offset / Pan** — stereo offset between channels, expressed as a percentage of the main Time value.
  In **Mono** mode it acts as a time-multiplier for bouncing/rhythmic subdivisions; in **Stereo** /
  **Ping Pong** modes it shifts left vs. right timing/pan for width.

### Delay model (mono / stereo / ping-pong)
- **Delay Model — Mono** — echoes are mono (L+R summed); Offset creates timing-based bounce effects.
- **Delay Model — Stereo** — left and right channels are delayed and fed back independently, preserving
  stereo content across the repeats.
- **Delay Model — Ping Pong** — echoes alternate/flip between left and right on each repeat, producing a
  bouncing stereo effect — the classic "widener" delay mode.
- **Delay Model — Off** — no delay/echoes at all; Filter, Saturation/Limiting, Sample Rate, Bit
  reduction and Tone stay active on the dry-processed signal (useful as a static filter/distortion box).
- **Stereo Spread** — widens or narrows the stereo image of the echoes, from fully merged/mono to full
  stereo.

### Feedback (repeats + filter in the feedback path)
- **Feedback Level** — how much of each echo is fed back in to create the next one; higher = more
  repeats before decaying to silence. Can exceed 100% (self-oscillation) — indicator shows
  green → orange → red as level rises; use with care.
- **Feedback Cutoff** — cutoff frequency of the feedback-path filter. Because this filter sits inside
  the feedback loop, EACH successive echo is filtered again, so repeats progressively darken (LP),
  thin out (HP), or narrow toward a band (BP) as they decay.
- **Feedback Resonance** — emphasizes/boosts a peak in amplitude around the Cutoff frequency; indicator
  changes color above ~75%. Higher resonance makes the filtered repeats more pronounced/whistly and
  pushes the feedback path toward self-oscillation.
- **Filter Type** — shape of the feedback filter: **LP** (low-pass, only frequencies below Cutoff
  survive into later repeats), **HP** (high-pass, only frequencies above Cutoff survive), **BP**
  (band-pass, only frequencies around Cutoff survive), or **Off** (no filtering in the feedback path).
- **Sample Rate (feedback)** — lower values reduce fidelity and add aliasing/"crunch" to the echoes as
  they recirculate.
- **Bits (bit reduction, feedback)** — lower bit depth adds a grainy/crunchy lo-fi quality to the
  echoes; effects become audible around 10 bits and below.

### Modulation
- **Rate** — speed of the modulation cycle (0–20 Hz) that drives the Time/Cutoff modulation below.
- **Time (mod amount)** — amount of modulation applied to the delay Time — creates wow/flutter
  tape-style pitch wobble, or flanging/chorusing character at faster rates.
- **Cutoff (mod amount)** — amount of modulation applied to the Feedback Cutoff, sweeping the filtered
  feedback path over time for evolving/animated repeats.
- All modulation destinations can also be driven by FL's internal modulation sources (LFO/envelope via
  right-click) for more elaborate motion.

### Diffusion (echo smearing)
- **Diffusion Level** — amount of smearing applied to the echoes — turns crisp discrete repeats into a
  softer, denser wash. Low = distinct echoes; high = blurred, reverb-like trail.
- **Diffusion Spread** — the time spread over which the smearing is applied.

### Feedback distortion
- **Distortion type — Limit** — hard-limits the feedback signal's peak level to the set Level, capping
  runaway feedback cleanly.
- **Distortion type — Saturation** — waveshapes the feedback signal once it exceeds Level, adding
  harmonic grit instead of hard-limiting.
  - **Knee** — shape of the transition from clean to distorted (soft vs. hard onset).
  - **Symmetry** — shifts the saturation curve from symmetrical to asymmetrical, changing the harmonic
    character (more even vs. odd harmonics).
- **Distortion Level** — the level at which Limiting or Saturation begins to act on the feedback signal.

### Output
- **Wet (Output)** — level of the fully effected (echoed/filtered/diffused) signal in the final output.
- **Tone** — filters the wet output only: low-pass toward one end, OFF in the center, high-pass toward
  the other end — a quick way to tame or brighten the overall delay character without touching the
  feedback filter.
- **Dry** — level of the unprocessed input signal passed straight to the output. Set to zero (typical
  on a dedicated delay send) to hear only the processed/wet signal.

## Recipes (concrete steps)
Each step is "set <param> = <intent>"; map names→indices at runtime as above.

- **1/4 slap-back** —
  - Tempo Sync = ON, Time = 1/4 (or right-click → Set for an exact quarter-note division)
  - Feedback Level = very low (1 repeat, minimal decay), Filter Type = LP with Cutoff fairly open
    (keep the single repeat natural, not dull)
  - Output Dry = high, Output Wet = low–moderate so the repeat sits just behind the source
  - Diffusion = low (repeat should stay distinct, not smeared). Great for thickening a vocal or
    guitar without an obvious "echo" effect.

- **Dotted-1/8 rhythmic delay (leads)** —
  - Tempo Sync = ON, Time = dotted 1/8
  - Delay Model = Stereo or Ping Pong for movement across the repeats
  - Feedback Level = medium (a handful of audible repeats), Filter Type = HP with a gentle Cutoff so
    repeats don't build up low-mid clutter under the lead
  - Modulation Time (mod amount) = low, Rate = slow, for subtle motion on later repeats
  - Output Dry = high, Output Wet = moderate, blended under the dry lead so the syncopation is heard
    without swamping the source.

- **Filtered dub delay (high feedback)** —
  - Tempo Sync = ON (or free Time for a looser dub feel), Feedback Level = high (pushing toward but
    not fully into self-oscillation — watch the indicator)
  - Filter Type = BP (or LP), Feedback Cutoff = mid, automate/modulate Cutoff (via the Modulation
    Cutoff amount, or by hand) for the classic dub "filter sweep" as repeats decay
  - Feedback Resonance = raised for a more pronounced, whistly filtered character
  - Distortion type = Saturation, Distortion Level = moderate for grit on the repeats as they build
  - Diffusion Level = moderate for extra smear/wash on the tail
  - Output Wet = high on a dedicated dub-delay send; automate Feedback Level or Cutoff live for
    breakdown/drop moments.

- **Ping-pong widener** —
  - Delay Model = Ping Pong, Time = a short subdivision (1/16 or 1/8, Tempo Sync = ON) so the bounce
    reads as width/movement rather than a distinct rhythmic echo
  - Feedback Level = low–medium (1–2 audible bounces), Stereo Spread = wide
  - Offset/Pan = adjusted to taste for the left/right bounce timing
  - Output Dry = high, Output Wet = low–moderate — enough to widen the source without an obvious echo.
    Useful on mono synths, pads, or plucks that need stereo interest.

## Tips
- Because the Feedback filter sits INSIDE the feedback loop, it shapes every successive repeat, not
  just the first — a small Cutoff change can dramatically change how "dark" or "thin" a long decay
  feels by the last audible repeat.
- If a delay send gets muddy, high-pass the feedback path (Filter Type = HP) or reduce Feedback Level
  before touching the overall Output Wet.
- Set Output Dry = 0 on a delay SEND track (the dry signal already lives on the source track); keep
  some Dry on a per-track INSERT use.
- High Feedback + high Resonance can self-oscillate into a sustained tone — useful for effects/risers,
  but keep an eye on the Feedback level indicator (green/orange/red) to avoid runaway levels.
- Keep Pitch = OFF plus an automated Time is a quick way to get classic tape-delay pitch-bend effects.
