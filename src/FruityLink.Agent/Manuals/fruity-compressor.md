---
name: Fruity Compressor
aliases: compressor, fruity compressor, comp
type: effect
category: Dynamics (compressor / gain control)
summary: FL's dedicated single-band compressor with variable knee and built-in peak limiting. Place after EQ, before reverb/delay.
---
# Fruity Compressor (effect)

FL Studio's dedicated, single-band dynamics processor — a variable-knee compressor with built-in
peak limiting. It reduces dynamic range by slowing down (and squashing) the rate of amplitude
increase once a signal crosses a threshold, then lets you make up the lost loudness. Used to tame
peaks, add punch to drums, "fatten" bass, control an unruly vocal, or glue a group of tracks
together. It is a MIXER (effect) plugin — it processes whatever is routed through its mixer track
slot.

## How the AI drives it
Parameter NAMES + INTENT are below; exact numeric INDICES are discovered at runtime. Fruity
Compressor is an effect on a MIXER track, so:
1. `native_list_mixer_plugin_params(mixerTrack, slot)` → read the live name→index map for this instance.
2. `native_set_mixer_plugin_params(mixerTrack, slot, index, value)` → set each param by that index.

Map the names below to indices via the list call first; do not assume a fixed order. Values are
normalized (typically 0..1 unless the list call reports otherwise); read a param back if unsure.

## When to use
- Even out an inconsistent performance (vocal, bass, solo instrument) so quiet parts sit up and
  loud peaks don't jump out.
- Add punch/snap to drums, or "glue" several elements together so they feel like one unit.
- Control peaks before they hit a limiter/maximizer downstream, so the limiter has less work to do.
- Skip it (or use very light settings) on already-controlled, dynamically-simple sources — over-
  compression flattens life out of a performance and can cause audible pumping/breathing.

## Chain placement
- Put compression **AFTER EQ** (Fruity Parametric EQ 2). Compressing a signal that still has
  resonant/muddy frequencies means the compressor reacts to problems it shouldn't — clean the tone
  first, then control its dynamics.
- Put compression **BEFORE reverb/delay** and other time-based effects. Compress the dry source so
  its level is controlled, then send the now-consistent signal into space; compressing after a
  reverb/delay squashes the tail unevenly and can pump the decay.
- Works both as a per-track insert (an individual vocal, bass, kick, snare) and on a group/bus or
  the master (to "glue" multiple elements into one cohesive sound at gentler settings).

## Parameters (names + intent)
- **Threshold** — the level (0 to -60 dB) above which compression starts acting. Lower threshold =
  more of the signal is above it, so compression engages earlier/more often and more of the
  performance gets squashed.
- **Ratio** — how much gain reduction is applied to the portion of the signal above threshold (from
  about 0.4:1 up to 30:1). E.g. a ratio of 4:1 means a 4 dB increase above threshold at the input
  only becomes a 1 dB increase at the output. Low ratios (2:1–4:1) are gentle/transparent; high
  ratios (8:1+) approach limiting and get aggressive/obvious.
- **Gain** — makeup gain (+30 to -30 dB) applied to the compressed output. Compression reduces peak
  level and therefore perceived loudness; use Gain to bring the squashed signal back up to a
  comparable (or louder) level than the uncompressed source.
- **Attack** — time (0 to 400 ms) it takes to reach full compression once the threshold has been
  crossed. Fast attack clamps down on transients almost immediately (tighter, can dull punch/flatten
  a drum hit). Slow attack lets the initial transient through uncompressed before gain reduction
  engages, which preserves — or exaggerates — punch and snap.
- **Release** — time (1 to 4000 ms) the compressor takes to stop reducing gain once the signal falls
  back below threshold. Fast release recovers quickly and can add energy/pump (or cause audible
  pumping/breathing if too fast relative to the material); slow release is smoother and more
  transparent but can still be reducing gain when the next transient arrives, softening it.
- **Type** (knee/curve preset) — Hard, Medium, Vintage, Soft, each with an "R" variant that enables
  TCR (Transient Controlled Release, an adaptive release). Soft means a gradual ramp from no
  compression to full compression as level rises through threshold (smooth, transparent, forgiving).
  Hard means the transition is instant right at threshold (obvious, more aggressive/pumping-prone).
  Medium sits between the two. Vintage emulates classic analog compressors (e.g. the LA-2A-style
  behavior) for extra warmth and "punch" character rather than transparency.
- **Sidechain input** — optional: route a different mixer track's signal to trigger the gain
  reduction instead of the plugin's own input (e.g. duck a bass under a kick). Not one of the core
  tuning knobs above; only reach for it when a ducking/pumping effect is specifically wanted.

### How they interact
Threshold and Ratio together set *how much* reduction happens (how far below threshold, how hard).
Attack and Release shape *how it happens over time* — together they define whether transients (the
hit's initial punch) survive and how smoothly/audibly the compressor recovers. Type (knee) shapes
the transition *around* threshold and colors the overall feel — a soft knee with a slow attack reads
as gentle/glued, a hard knee with a fast attack reads as clamped/pumping. Gain is the last step:
it doesn't change the dynamics, only restores the loudness that compression took away, so it's
easiest to judge the actual tonal effect of a setting when Gain is used to level-match against the
bypassed/uncompressed signal.

## Recipes (concrete steps)
Each step is "set <param> = <intent>"; map names→indices at runtime as above.

- **Tame an uneven vocal** —
  - Threshold = medium (catches the loud phrases, leaves quiet ones alone)
  - Ratio = low-medium (~2:1–4:1) for transparency
  - Attack = fast-ish (catch plosives/peaks), Release = medium-fast so it recovers before the next word
  - Type = Soft (or Vintage for character), Gain = make up the level lost to compression.

- **Punchy drums (slow attack, fast release)** —
  - Threshold = set so only the hit's peak triggers reduction
  - Ratio = medium-high (~4:1–8:1)
  - Attack = slow (lets the initial transient/snap through before clamping)
  - Release = fast (recovers before the next hit, keeps the groove energetic)
  - Type = Hard or Medium for an obvious, punchy effect; Gain = make up the level.

- **Gentle bus glue** —
  - On a group/bus mixer track: Threshold = high-ish (only shaves the loudest peaks)
  - Ratio = low (~1.5:1–2:1), Attack = medium, Release = medium-slow (smooth, avoids pumping)
  - Type = Soft (transparent) or Vintage (a little warmth), Gain = small makeup only — the goal is a
    couple dB of gentle, mostly-inaudible reduction that ties the elements together, not an obvious
    "compressed" sound.

- **Parallel compression (note)** — Fruity Compressor has no built-in dry/wet mix knob, so parallel
  ("New York") compression is done at the mixer level: send/duplicate the source to a second mixer
  track, put Fruity Compressor there with an aggressive setting (low Threshold, high Ratio, fast
  Attack, medium Release) to squash it hard, then blend that heavily-compressed track's volume in
  underneath the original dry track. This adds density/punch/sustain without losing the original's
  natural transients and dynamics.

## Compressor vs Fruity Limiter's compressor (which to pick)
Fruity Limiter is a combined compressor + limiter + maximizer aimed at finishing a signal — pushing
loudness and catching peaks with a brick-wall ceiling, typically at the end of a chain (a bus or the
master). Its compressor stage exists to feed the limiter/maximizer, and it adds its own controls
(Curve tension, Sustain averaging, look-ahead tied to Attack) built for that combined job.

Use **Fruity Compressor** when the goal is dynamics shaping on an individual track or bus — precise,
independent control over Threshold/Ratio/Attack/Release/Knee without any ceiling or loudness-
maximizing behavior riding along with it. Use **Fruity Limiter's compressor** when the goal is
loudness/peak control as a finishing step (e.g. gluing + maximizing a group bus or the master, or
squashing and ceiling-limiting in one plugin) rather than shaping the character of one source.

## Tips
- Watch the plugin's gain-reduction indicator; if it's moving on almost every hit and the mix starts
  audibly "breathing"/pumping, raise Threshold, lower Ratio, or lengthen Release.
- Always level-match with Gain before judging a setting by ear — compressed-but-louder tends to sound
  "better" even when the compression itself isn't actually a good fit; match loudness first, then judge.
- Slow Attack preserves transient punch; fast Attack controls/clamps it. Pick based on whether the
  source needs to keep its snap (drums) or needs peaks caught immediately (a spiky vocal or bass DI).
