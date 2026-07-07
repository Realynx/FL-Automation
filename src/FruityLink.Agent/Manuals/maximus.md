---
name: Maximus
aliases: maximus, multiband, mastering, maximizer
type: effect
category: Dynamics (multiband maximizer / compressor / limiter)
summary: FL's multiband maximizer, compressor and limiter — LOW/MID/HIGH + MASTER bands, each with its own curve + envelope, plus sidechain ducking and a noise gate. Place near the END of a master/bus chain for glue and loudness.
---
# Maximus (effect)

FL Studio's flagship dynamics processor. Maximus is a **multiband maximizer / compressor / limiter**
(and noise-gate / de-esser) that splits the incoming audio into three frequency bands — **LOW**, **MID**
and **HIGH** — processes each one independently, recombines them, and then runs the sum through a fourth
**MASTER** band for a final overall pass. Each of the four bands has its own compression/limiting
**curve** and its own attack/release **envelope**, so you can, e.g., limit the highs, glue the mids and
gate the lows all at once. It is a MIXER (effect) plugin — it processes whatever audio is routed through
its mixer track slot.

Maximus is **deep and heavily preset-driven**: the compression **curve** shape defines the character
(compressor vs limiter vs gate vs expander), and Image-Line ships many presets. In practice the AI
should **pick/keep a suitable curve preset**, then dial the sound in with per-band **pre-gain**,
**post-gain** and **attack/release**, and reach for the **MASTER band** for the final limiting / loudness
ceiling.

## How the AI drives it
Parameter NAMES + INTENT are below; exact numeric INDICES are discovered at runtime. Maximus is an
effect on a MIXER track, so:
1. `native_list_mixer_plugin_params(mixerTrack, slot)` → read the live name→index map for this instance.
2. `native_set_mixer_plugin_params(mixerTrack, slot, index, value)` → set each param by that index.

Map the names below to indices via the list call first; do not assume a fixed order. Many params are
per-band, so expect the same names prefixed/grouped by band (LOW / MID / HIGH / MASTER) — match the band
too, not just the control. Values are normalized (typically 0..1 unless the list call reports otherwise);
read a param back if unsure.

## When to use
- **Mastering / loudness**: as the last-ish dynamics stage on the master, to control peaks and raise
  overall loudness transparently (or aggressively) without clipping.
- **Bus glue**: across a drum bus, instrument bus or full mix to fuse elements with gentle multiband
  compression.
- **Multiband problem-solving**: tame just one region — de-ess a harsh HIGH band, control boomy LOW-end,
  or compress muddy MIDs — without touching the rest of the spectrum.
- **Punch / character on drums**: shape transients per band (slow attack = more punch; fast = tighter).
- **Multiband sidechain / ducking**: duck specific bands to a kick (e.g. only the LOW band) for a
  tighter, more musical pump than full-range ducking.
- **Noise gate / expansion**: use the bottom of the curve to pull low-level signal down to silence.
- Avoid stacking heavy limiting stages — one well-set Maximus at the end usually beats several.

## Chain placement
- Put Maximus **near the END** of a master or bus chain — dynamics/loudness maximizing is a final step.
  It goes **AFTER** corrective EQ (Fruity Parametric EQ 2) so it reacts to the balanced signal, not to
  problems you're about to fix.
- For **mastering**, it typically sits last (or just before a final true-peak/dither stage): EQ → glue →
  **Maximus (limit + loudness)**.
- For **bus glue**, put it on the group/bus mixer track after that bus's EQ.
- The **MASTER band** inside Maximus is itself the "end of the chain" — reserve it for the final overall
  limit/ceiling and do per-region shaping in LOW/MID/HIGH.

## Signal flow (bands)
Input → **crossovers** split it into **LOW / MID / HIGH** → each band is processed by its own PRE-gain →
curve (compress/limit/gate) → envelope (attack/release) → POST-gain → the three bands are summed →
**MASTER** band applies one more PRE → curve → envelope → POST over the whole signal → output (with a
global Mix/dry-wet and gain). The two crossover frequencies decide *where* LOW/MID and MID/HIGH split.

## Parameters (names + intent)

### Per-band controls (repeat for LOW, MID, HIGH and MASTER)
Each band exposes the same set of controls; match the band as well as the control name.

- **Pre-gain (PRE)** — input gain into the band, *before* the curve. This is the main **threshold-style**
  control: pushing more signal into a fixed curve makes it cross the compression/limiting knee sooner, so
  **more pre-gain = more gain reduction / harder limiting**. Turn it up to drive the band into the curve.
- **Compression / limiter CURVE** — the transfer function that maps input level → output level for the
  band. A straight diagonal = no change; a knee that bends flatter toward the top = **compression**; a
  hard flat ceiling at the top = **limiting/maximizing**; a curve that pulls low input down toward
  silence = **noise gate / downward expansion**; an upward bend = **upward expansion**. The curve's bend
  point acts as the band's **threshold**, and its steepness acts as the **ratio**. This is usually chosen
  via a **preset** rather than drawn param-by-param; steer character by preset + pre-gain.
- **Attack (ATT)** — envelope attack: how fast the band clamps down once signal exceeds the curve. **Fast
  attack** catches transients (tighter, good for true limiting/de-essing); **slow attack** lets
  transients punch through before compressing (more punch on drums).
- **Release (REL)** — envelope release: how fast the band recovers after the signal drops. **Fast
  release** = louder/denser but can pump or distort; **slow release** = smoother, more transparent.
- **Post-gain (POST)** — make-up/output gain *after* the curve, to restore level lost to compression or
  to push the band louder in the final blend. Pair with pre-gain: pre drives compression, post restores
  loudness.

### Crossovers (global — where the bands split)
- **Low / Low-Mid crossover freq** — the split frequency between the **LOW** and **MID** bands. Lower it
  to keep only sub/bass in LOW.
- **High / Mid-High crossover freq** — the split frequency between the **MID** and **HIGH** bands. Raise
  or lower it to define how much "top" the HIGH band captures (e.g. set it to isolate sibilance for
  de-essing).

### Global / master output
- **Master gain / Output** — overall output level of the plugin.
- **Mix (dry / wet)** — blend of processed vs unprocessed signal, for **parallel compression** (blend in
  dry to keep transients/loudness of the source while adding the crushed, dense processed signal).

### Sidechain / ducking + stereo
- **Sidechain source / input** — selects an external audio source (a sidechain-routed track, e.g. the
  kick) to *drive* the envelope instead of (or alongside) the band's own signal.
- **Ducking** — how strongly the sidechain input pushes the band's gain down. Can be applied **per band**,
  so you can duck only the LOW band against a kick for a clean, targeted pump while leaving mids/highs
  untouched. Higher = deeper duck.
- **Stereo separation (SEP)** — how independently the left/right channels are processed in the band, from
  **linked/mono** (both channels share one envelope — stable, no image shift) through to **fully
  independent stereo** (each channel processed on its own — wider, but can pull the stereo image). Keep
  toward mono/linked on the MASTER band for a stable mastering image.

## Recipes (concrete steps)
Each step is "set <param> = <intent>" for the named band; map names→indices at runtime as above. Prefer
starting from a fitting **preset** and then nudging these.

- **Transparent mastering (loudness + peak control)** —
  - Start from a gentle/mastering-style curve preset (soft knee).
  - MASTER band: Pre-gain = raise slowly to taste (more = louder + more limiting), Attack = fast,
    Release = medium-slow (smooth, avoid pump), Post-gain = to hit target ceiling.
  - LOW / MID / HIGH: keep curves gentle; small per-band Pre-gain trims to even out the balance.
  - Stereo separation = linked/mono-ish on MASTER for a stable image. Mix = 100% wet.

- **Multiband de-essing (HIGH band only)** —
  - Set the **Mid-High crossover** up into the sibilance region (~5–8 kHz) so the HIGH band captures the
    "ess"/harshness.
  - HIGH band: use a compressing/limiting curve, Attack = fast, Release = fast–medium, Pre-gain = up
    until the sibilance is caught; leave LOW/MID/MASTER near flat so only the top is tamed.

- **Punch on drums (drum bus)** —
  - Slow-ish Attack per band so transients snap through before compression, Release = medium (recover
    before the next hit).
  - LOW band: modest compression for a solid, controlled kick; MID: light glue; HIGH: gentle.
  - Optionally blend **Mix** below 100% for parallel "punch" (dry transients + dense processed body).

- **Multiband sidechain to a kick** —
  - Route the kick to Maximus's **sidechain input**.
  - Apply **Ducking** mainly on the **LOW band** (and lightly on MID) so the low end ducks out of the
    kick's way; leave HIGH untouched so cymbals/air don't pump. Set Release so the band recovers in time
    with the groove.

- **Loudness maximizing (aggressive)** —
  - Curve = harder-knee limiter/maximizer preset.
  - MASTER band: Pre-gain = high (drives hard into the ceiling), Attack = very fast, Release = fast for
    density (watch for pumping/distortion), Post-gain = to the ceiling.
  - Use per-band Pre-gain to keep the LOW band from eating all the headroom. Back off if it starts to
    distort or lose punch.

## Tips
- **Pre-gain drives compression, Post-gain restores loudness** — think of Pre as "threshold" (how hard
  you hit the curve) and Post as make-up gain.
- The **curve shape is the character** — compressor, limiter, gate and expander are all just different
  curves. Change behavior by loading a different **preset** curve, not by hunting for a "mode" switch.
- Reserve the **MASTER band** for the final overall limit/ceiling; do surgical, per-region work in
  LOW/MID/HIGH so the master pass stays clean.
- If it pumps or distorts, **slow the Release** and/or **back off Pre-gain** first.
- For de-essing or targeting one region, move the **crossover frequencies** to isolate the problem band,
  then process only that band.
- Keep **Stereo separation** linked/mono-leaning on the master pass to avoid an unstable stereo image;
  more separation is fine for creative width on individual bands.
- Use **Mix** below 100% for parallel compression — keep the source's punch while adding density.
