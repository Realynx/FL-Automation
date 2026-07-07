---
name: Fruity Reeverb 2
aliases: fruity reverb 2, fruity reeverb 2, reverb 2, reeverb 2, reverb, reeverb, fruity reverb, fruityreverb2, fruity reverb2
type: effect
category: Reverb (spatial / time-based)
summary: FL's main algorithmic reverb — rooms, halls, plates and glue. Place near the END of a mixer chain, post-EQ.
---
# Fruity Reeverb 2 (effect)

FL Studio's primary algorithmic reverb. Adds a sense of space (room → hall → cavern) and can glue a
group of tracks together. FL spells it **"Fruity Reeverb 2"** (double-e) in the plugin picker; users
say "reverb", so this manual answers to both. It is a MIXER (effect) plugin — it processes whatever is
routed through its mixer track slot.

## How the AI drives it
Parameter NAMES + INTENT are below; exact numeric INDICES are discovered at runtime. Fruity Reeverb 2
is an effect on a MIXER track, so:
1. `native_list_mixer_plugin_params(mixerTrack, slot)` → read the live name→index map for this instance.
2. `native_set_mixer_plugin_params(mixerTrack, slot, index, value)` → set each param by that index.

Map the names below to indices via the list call first; do not assume a fixed order. Values are
normalized (typically 0..1 unless the list call reports otherwise); read a param back if unsure.

## When to use
- Add depth/space to a dry source (vocals, keys, snare, pads, plucks, leads).
- "Glue" a bus of instruments into one shared room.
- Create a sense of distance (more wet + longer decay = further away).
- Avoid heavy reverb on sub bass and kicks — it muddies the low end (high-pass or keep them dry).

## Chain placement
- Put reverb **near the END** of a mixer chain, and **AFTER EQ** (Fruity Parametric EQ 2). Reverbing a
  muddy/harsh signal smears those problems into the tail — clean with EQ first, then add space.
- Compression usually goes BEFORE reverb (compress the dry source, then reverb the controlled signal).
- For shared spaces, use a **send**: route several tracks to one mixer track holding the reverb and set
  it wet-heavy, so every source shares one coherent room (cheaper + more cohesive than per-track verbs).

## Parameters (names + intent)
- **Decay / Decay time** — how long the tail rings out. Short = tight room; long = hall/ambient.
- **Room size / Size** — dimensions of the modeled space. Larger = longer, more diffuse reflections.
- **Diffusion** — density/smoothness of the reflections. High = smooth wash; low = grainier, more
  discrete early reflections.
- **Predelay** — gap before the reverb starts. A little predelay (10–30 ms) keeps the dry source
  clear and up-front by separating it from the tail (great on vocals).
- **Low cut (high-pass / HP)** and **High cut (low-pass / LP)** — band-limit the reverb. Low-cut is a
  high-pass that keeps the tail out of the sub/mud region; high-cut is a low-pass that tames fizz and
  sits the reverb behind the source.
- **Low/High cross (crossover)** — the frequency split points that separate how low vs high bands decay.
- **Color / Tone / Damping** — brightness of the tail; darker = warmer/more distant, brighter = airier.
- **Dry / Wet / Mix** — balance of unprocessed vs reverberated signal. On a per-track insert keep some
  dry; on a dedicated SEND/return track set (near) 100% wet (the dry lives on the source track).
- **Stereo / Width** — stereo spread of the reverb tail.

## Recipes (concrete steps)
Each step is "set <param> = <intent>"; map names→indices at runtime as above.

- **Short room (subtle body)** —
  - Room size = small, Decay = short (~0.6–1.0 s)
  - Predelay = low, Mix/Wet = low (~10–15%)
  - High cut = moderate to keep it out of the way. Adds space without an obvious tail.

- **Long hall (cinematic)** —
  - Room size = large, Decay = long (~3–6 s)
  - Predelay = 20–40 ms so the source stays defined
  - Low cut = high-ish (keep the sub clean), High cut = moderate. Best on a wet SEND track.

- **Vocal plate** —
  - Diffusion = high (smooth), Decay = medium (~1.5–2.5 s)
  - Predelay = 20–30 ms (clarity), High cut = gentle for air
  - Mix = to taste on an insert, or ~100% wet on a vocal reverb send.

- **Subtle glue (bus)** —
  - On a group/bus mixer track: Room size = small–medium, Decay = short–medium
  - Wet = low (~8–12%), Low cut engaged. Just enough shared space to fuse the elements — the tail
    should be felt, not heard.

## Tips
- If a mix gets muddy after adding reverb, raise the reverb's Low cut and/or lower Decay/Wet first.
- Longer predelay = source stays forward; near-zero predelay = source sits "in" the space (further back).
- Prefer one shared reverb send over many small insert reverbs for a cohesive, CPU-friendly space.
