# Automation links: what a clip owns, and how to get it back

An automation clip is not a recording that plays while its clip is on the playlist. It is a **link**,
and the link is stronger than the clip:

- **FL writes the clip's initial value into the linked control every time playback starts.** The
  initial value is the value the envelope holds before the clip begins - FL's manual calls this the
  automation clip's initial value, and it is applied on every play, not only when the playhead
  reaches the clip.
- **The link lives on the automation CHANNEL, not on the playlist clip.** Deleting the clip removes a
  placement; the channel keeps the link.
- **FL has no channel delete the bridge can call.** The native automation verbs are create, read,
  add point, delete point and replace points - there is no unlink, no retarget and no channel
  removal (see [Linked automation clips](automation-clips.md) for the verified symbols; the 2025
  additional-link helper takes an automation editor *form*, not a channel, and is not exposed).

Put together: once a control is automated, it stays owned. A plain write to it is accepted, reads
back correctly and is then silently overridden by the audio engine on the next play.

## The defect this API exists for

FL 26.1.3.5570, 2026-09-17. `fl.automation.pump(AutomationTarget.mixer_volume(5), ...)` created
automation channel 12 linked to insert 5's volume, with a clip at bars 33-36. The clip was later
deleted with `fl.clips.delete(...)`. From then on:

```python
fl.mixer[5].volume = 6400        # accepted; reads back 6400
fl.mixer[5].volume               # -> 6400
```

...and the engine still played insert 5 at the automation's value: a master capture of bars 1-2
measured the **same RMS at raw 12800 and at raw 6400**. Routing the channel to insert 10 instead
gave the expected -12.44 dB, which is the workaround below. Nothing in the project state was wrong;
the SDK was simply writing to a control somebody else owned.

## Discovery: who owns this control?

```python
from fruitylink import AutomationTarget

owners = fl.automation.links_to(AutomationTarget.mixer_volume(5))
for item in owners:
    print(item.channel, item.name, item.target, len(item.clips))
# 12 Insert 5 pump AutomationTarget(kind='mixer_volume', index=5, slot=-1, parameter=-1) 0
```

- `fl.automation.links_to(target)` - every automation clip channel whose decoded target equals
  `target`.
- `fl.automation.targets_of(kind, index, slot=-1, parameter=-1)` - the same thing from loose fields:
  `targets_of("mixer_volume", 5)`, `targets_of("plugin_parameter", 1, 0, 556)`.
- `fl.automation.list()` / `fl.automation.describe()` - the full, freshly read inventory, including
  envelope point counts and placements.

Note the `0` placements above: the clip was deleted and the link is still there. That is also why
the lookup cannot be cheap - a link with no clip left cannot be found through the playlist, so the
SDK asks the host to describe **every channel** in the rack (one `query_clips` pass, one
`get_channel_plugin` per channel) and decodes the `event 0x...` ids it reports into
`AutomationTarget`s.

### Caching, and when to refresh

That scan is cached per connection:

- It is built on the first `links_to` / `targets_of` / guarded write and reused afterwards.
- `fl.automation.create` / `pump` / `duck` / `tile` invalidate it, so a link the SDK just made is
  never missed.
- `fl.automation.refresh()` rescans explicitly. Use it after automation was created, retargeted or
  renamed **in FL's GUI or by another client**, which the SDK cannot observe.
- `fl.automation.list()` never uses the cache. Cached records carry `point_count == -1` because the
  cache does not read envelopes; call `list()` when you want point counts.
- A host that cannot answer the scan (an older build, or a project without the `automation_clips`
  capability) is remembered as unavailable: the guard is skipped from then on and no write ever
  fails because of it.

## Guarded writes

Every SDK write to a control an automation clip can own checks that index first and emits
`AutomationLinkedWarning`:

```python
import warnings
from fruitylink import AutomationLinkedWarning

fl.mixer[5].volume = 6400
# AutomationLinkedWarning: mixer_volume 5 is linked to automation clip channel 12 'Insert 5 pump':
# FL writes that clip's initial value to this control every time playback starts, so this write is
# not audible after the next play. Flatten the curve with fl.automation.release(12), move the
# material to another insert/channel, or pass linked='ignore'.
```

The write still happens - the value is not wrong, it is merely temporary. Guarded surfaces:

| Surface | Target kind | Explicit escape |
| --- | --- | --- |
| `fl.mixer[t].volume`, `.volume_db`, `set_volume` | `mixer_volume` | `set_volume(v, linked=...)` |
| `fl.mixer[t].pan`, `set_pan` | `mixer_pan` | `set_pan(v, linked=...)` |
| `fl.channels[c].volume`, `.volume_db`, `set_volume` | `channel_volume` | `set_volume(v, linked=...)` |
| `fl.channels[c].pan`, `set_pan` | `channel_pan` | `set_pan(v, linked=...)` |
| `fl.channels[c].pitch`, `set_pitch` | `channel_pitch` | `set_pitch(cents, linked=...)` |

`linked=` takes `"warn"` (default), `"raise"` (raises `AutomationLinkedWarning` **before** writing)
or `"ignore"` (writes with no lookup at all, which is also the cheap path in a tight loop). Every
existing signature still works unchanged. Writes through `fl.ops` and
`fl.channels[c].set_control(...)` are raw and never checked.

`AutomationLinkedWarning` is an ordinary `UserWarning` subclass, so a script can promote it:

```python
warnings.simplefilter("error", AutomationLinkedWarning)   # fail the run instead of warning
```

Plugin parameters are guarded too, on both write paths:

```python
parameters = fl.mixer[4].effects[0].parameters
parameters.set(48, 0.5)                                  # warns if channel 21 automates it
parameters.set(48, 0.5, linked="ignore")                 # same escape as the faders
result = parameters.set_verified(48, 0.5)
result.verified                 # True: the write landed and read back
result.automation_linked        # ('insert 4 slot 0 parameter 48 is linked to automation clip ...',)
```

`VerifiedWrite.automation_linked` is the important part: a readback cannot see this problem, so a
`verified=True` write can still be inaudible after the next play. When the tuple is non-empty, report
the write as not surviving playback. The check runs once per `set_verified` (the internal `set` uses
`linked="ignore"`), and `set_named` inherits the default.

## Releasing: making the pinned value the value you want

There is no unlink, so a linked control cannot be freed. The next best thing is to make the value FL
keeps reapplying the value you asked for:

```python
fl.automation.release(12)             # adopt the target's CURRENT value (mixer volume 6400 -> 0.4)
fl.automation.release(12, 0.0)        # or pin an explicit automation value 0..1
fl.automation[12].release()           # same call on the curve object
```

`release` replaces the whole envelope with two points at the same value (FL needs at least two), so:

- the curve is flat and the clip is audibly inert;
- the value FL writes on every play is the value you chose;
- the previous envelope's span is preserved, so any remaining clip still covers its bars;
- the channel and its link are untouched - this is neutralising, not unlinking.

### A curve is only evaluated through a placed clip

**Live evidence 2026-09-17, and the reason `release` edits the playlist.** With the channel's only
clip deleted, `release(channel, 0.8)` and then `release(channel, 0.4)` produced the *same* master
level (-19.68 and -19.46 dBFS): FL kept reapplying whatever value the link last held and never
looked at the flattened envelope. After one clip was placed for that curve
(`fl.automation[2].add_clip(track=2, start_tick=0, length_tick=2 * BAR)`), the same two releases
measured **-32.18 dBFS at 0.4 and -19.60 dBFS at 0.8** - a 12.58 dB rise, exactly the fader model.

So `release` makes sure at least one playlist clip references the channel: if none does, it places
one at tick 0 on `fl.playlist.first_free_track(...)`, as long as the previous envelope's span and
never shorter than one bar. The return value reports it:

```python
result = fl.automation.release(12)
float(result)                       # 0.4 - it IS the float release has always returned
result.value                        # 0.4, spelled out
result.placed_clip                  # True when release had to place a clip
result.track, result.start_tick, result.length_tick, result.clip_index
```

`release(..., place_clip=False)` skips the check and edits nothing but the envelope - use it when you
know a clip exists, or when the playlist must not change.

**A readback of the target never shows the automation-applied value.** Through the whole live run
`fl.mixer[1].volume` read 12800 while the engine played the insert at the automation's level. The
mixer/channel/parameter getters report the *project* value, not what the audio engine is using, so
there is no readback that can detect this problem: use `links_to` for the state and a render or
capture for the level.

With no argument, `release` reads the target's current value and converts it to the 0..1 an
automation point carries: mixer volume `raw / 16000`, mixer pan `(raw + 6400) / 12800`, channel
volume and pan `raw / 12800`, and a plugin parameter through its decoded `normalized`. This is
exactly what you want after the defect above: the plain write already left the control at the level
you wanted (that value is in the project even though the engine ignored it), and `release()` makes
that level the one FL applies. Two cases have no readable automation value
and require an explicit `value`: FL's channel **pitch** (cents, no documented normalized mapping) and
native plugin scales that are not float32 bits (see `fruitylink.plugins.normalized_from_raw`).

## The other workaround: move the material

Releasing leaves the automation channel in the rack. If you would rather keep a real, moving curve
somewhere else, route around the pinned control instead:

- Route the channels feeding the pinned insert to a **different insert** and mix there. In the live
  case, routing to insert 10 immediately restored normal fader behaviour (-12.44 dB as modelled).
- Or reuse the automation instead of fighting it: `fl.automation[channel].set_points(...)` /
  `set_point(...)` rewrites the envelope, and `add_clip` places the existing channel again without
  creating or relinking anything. A linked control is best set *through* its automation.
- `Channel.retire()` is the documented stand-in for the channel delete FL does not offer: it mutes
  the channel, routes it to Master and renames it `"(unused) ..."`. It does **not** remove the link,
  so release the curve as well.
- **Retargeting the automation channel in FL's GUI** also frees the fader (verified live), but no
  native call the bridge can make does it: the 2025 additional-link helper takes an automation editor
  form rather than a channel, so there is no SDK path and no MCP path either.

Creation results say all of this out loud:

```python
result = fl.automation.pump(AutomationTarget.mixer_volume(5), 0, 16 * ppq, track=8)
print(result.link_notice)
# Automation channel 12 now owns this target: FL reapplies this clip's initial value to it every
# time playback starts, and deleting the clip does not unlink it, so plain writes stay inaudible
# until fl.automation.release(12).
```

## Not done yet

- **Notes, patterns and routing are unguarded**, because nothing can automate them. The guard covers
  exactly the six `AutomationTarget` kinds; mixer **send levels** have no target at all (their event
  ids are unknown to the bridge), so a send write is never checked and never pinned.
- **No real unlink exists.** If a future native profile exposes one, it belongs next to
  `create_automation_clip` in `INativeFlControl` and should replace `release` as the primary advice.
- **Live status (FL 26.1.3.5570, 2026-09-17).** Verified live: `links_to` finds the owner after its
  clip was deleted; a bare `fl.mixer[1].volume = 6400` raises `AutomationLinkedWarning`;
  `set_volume(..., linked="raise")` refuses; `set_verified` reports the link; and a released curve
  **with a placed clip** holds its target at the released value (-32.18 dBFS at 0.4, -19.60 at 0.8).
  Not yet re-measured: `release`'s own clip placement (added after that run, so the automatic
  placement is fixed-unverified), and whether reapply-on-play happens for every target kind (it is
  verified for `mixer_volume`).
