"""Generated from INativeFlControl.cs; regenerate with tools/generate_operations.py.

Python arguments use snake_case; generated mappings preserve native wire names.
Native integer scales and index conventions are documented by each operation.
"""

from collections.abc import Sequence
from typing import cast

from .automation_records import AutomationClipResult, AutomationPointSpec, AutomationTarget
from .models import decode_record
from .queries import QueryOperations
from .records import ClipMove, ClipResize, NoteEdit, NoteRef, NoteSpec, PatternClipSpec
from .transport import RequestTransport
from .values import JsonValue, resolve_alias, wire_arguments

ARGUMENT_ALIASES: dict[str, dict[str, str]] = {'set_channel_volume': {'volume': 'value'}, 'set_mixer_volume': {'volume': 'value'}, 'set_master_volume': {'volume': 'value'}, 'set_channel_pan': {'pan': 'value'}, 'set_mixer_pan': {'pan': 'value'}}
"""Keyword aliases each operation accepts next to its canonical argument (alias -> canonical)."""


class Operations(QueryOperations):
    def __init__(self, transport: RequestTransport) -> None:
        self._transport = transport

    def invoke(self, operation: str, **arguments: object) -> JsonValue:
        """Invoke a catalogue operation, including capability-dependent extensions."""
        return self._transport.request("invoke", {"operation": operation, "arguments": wire_arguments(arguments)})

    def is_available(self) -> bool:
        """True if the injected bridge is loaded in FL and responding."""
        return cast(bool, self.invoke("is_available"))

    def set_tempo(self, *, bpm: float) -> None:
        """Set tempo in beats per minute (10..522)."""
        self.invoke("set_tempo", **{"bpm": bpm})

    def get_tempo(self) -> float:
        """Read tempo in beats per minute."""
        return cast(float, self.invoke("get_tempo"))

    def set_master_volume(self, *, value: int | None = None, volume: int | None = None) -> None:
        """Master volume as a raw native integer, 0..12800. No dB conversion is defined. Accepts ``volume=`` as an alias of ``value=``."""
        value = resolve_alias("value", value, volume=volume)
        self.invoke("set_master_volume", **{"value": value})

    def get_master_volume(self) -> int:
        """Read master volume, 0..12800 (symmetric with SetMasterVolumeAsync)."""
        return cast(int, self.invoke("get_master_volume"))

    def set_master_pitch(self, *, cents: int) -> None:
        """Master pitch in cents, -1200..+1200."""
        self.invoke("set_master_pitch", **{"cents": cents})

    def get_master_pitch(self) -> int:
        """Read master pitch in cents (symmetric with SetMasterPitchAsync)."""
        return cast(int, self.invoke("get_master_pitch"))

    def set_shuffle(self, *, value: int) -> None:
        """Global shuffle/swing, 0..128."""
        self.invoke("set_shuffle", **{"value": value})

    def get_shuffle(self) -> int:
        """Read global shuffle/swing, 0..128 (symmetric with SetShuffleAsync)."""
        return cast(int, self.invoke("get_shuffle"))

    def set_mixer_volume(self, *, track: int, value: int | None = None, volume: int | None = None) -> None:
        """Mixer track volume as a raw native integer on FL's fader scale 0..16000 (track 0 = master): 12800 (= 0.8, every track's default) is 0 dB, 16000 is the fader top (about +4.05 dB on the render-calibrated curve; FL's own hint says +5.6 dB) and 0 is silence. FL's fader law is not linear in dB: the SDK models it as dB = 20 * 2.09 * log10(raw / 12800), the exponent measured by render calibration 2026-09-14 (so 6400 is about -12.6 dB, measured -12.70, and 3200 about -25 dB); use the Python helpers fruitylink.levels.mixer_volume_from_db / MixerTrack.volume_db for dB values. Values above 16000 are clamped. Accepts ``volume=`` as an alias of ``value=``."""
        value = resolve_alias("value", value, volume=volume)
        self.invoke("set_mixer_volume", **{"track": track, "value": value})

    def get_mixer_volume(self, *, track: int) -> int:
        """Read a mixer track volume 0..16000 (12800 = 0 dB; symmetric with SetMixerVolumeAsync)."""
        return cast(int, self.invoke("get_mixer_volume", **{"track": track}))

    def set_mixer_pan(self, *, track: int, value: int | None = None, pan: int | None = None) -> None:
        """Mixer track pan as a SIGNED native integer, -6400..6400: 0 = center, negative = left, 6400 = hard right. This scale differs from channel pan (0..12800, 6400 = center); a mixer value of 6400 or more is fully right. Live-verified by isolated renders (Ember Tides v006). Accepts ``pan=`` as an alias of ``value=``."""
        value = resolve_alias("value", value, pan=pan)
        self.invoke("set_mixer_pan", **{"track": track, "value": value})

    def get_mixer_pan(self, *, track: int) -> int:
        """Read a mixer track pan -6400..6400 (0 = center; symmetric with SetMixerPanAsync). Untouched tracks read 0."""
        return cast(int, self.invoke("get_mixer_pan", **{"track": track}))

    def set_mixer_track_muted(self, *, track: int, muted: bool) -> None:
        """Mute/unmute a mixer track (the enabled flag; solo state untouched)."""
        self.invoke("set_mixer_track_muted", **{"track": track, "muted": muted})

    def get_mixer_track_muted(self, *, track: int) -> bool:
        """Read a mixer track's mute state (symmetric with SetMixerTrackMutedAsync)."""
        return cast(bool, self.invoke("get_mixer_track_muted", **{"track": track}))

    def set_mixer_track_armed(self, *, track: int, armed: bool) -> None:
        """Arm or disarm a mixer track's disk recording (the disc button; track 0 = Master). While FL's transport records, every armed track writes one WAV of its post-FX output into FL's recorded-audio folder, which is how live per-insert capture works. Idempotent: FL's own setter (the routine behind its scripting armTrack) is only invoked when the state differs, and the state is re-read afterwards. Requires the verified mixer layout plus the harvested FLmx_SetTrackArmed/MixerTrackArmedOffset symbols; refused otherwise."""
        self.invoke("set_mixer_track_armed", **{"track": track, "armed": armed})

    def get_mixer_track_armed(self, *, track: int) -> bool:
        """Read a mixer track's disk-recording arm state (symmetric with SetMixerTrackArmedAsync): the byte FL's own isTrackArmed reads. Refused until the mixer layout and the armed-byte offset resolve on the running build."""
        return cast(bool, self.invoke("get_mixer_track_armed", **{"track": track}))

    def set_mixer_fx_param(self, *, track: int, slot: int, param_index: int, value: int) -> None:
        """A mixer FX-slot plugin parameter (normalized fixed-point value)."""
        self.invoke("set_mixer_fx_param", **{"track": track, "slot": slot, "paramIndex": param_index, "value": value})

    def set_channel_volume(self, *, channel: int, value: int | None = None, volume: int | None = None) -> None:
        """Channel volume as a raw native integer, 0..12800 (FL's default is 10000 = 78 %). The scale is a power curve, not linear dB: the SDK models FL's fader law as dB = 20 * 2.09 * log10(raw / 10240) (10240 = 0 dB, 12800 = about +4.05 dB, 10000 = about -0.4 dB, 6400 = about -8.5 dB, 5000 = about -13 dB, 3200 = about -21 dB), so halving the raw value costs about 12.6 dB (render-measured 12.54). Use fruitylink.levels.channel_volume_from_db / Channel.volume_db for dB values; keep channel volumes near 10000 and trim with plugin gains for large changes. Accepts ``volume=`` as an alias of ``value=``."""
        value = resolve_alias("value", value, volume=volume)
        self.invoke("set_channel_volume", **{"channel": channel, "value": value})

    def get_channel_volume(self, *, channel: int) -> int:
        """Read channel volume 0..12800 (symmetric with SetChannelVolumeAsync)."""
        return cast(int, self.invoke("get_channel_volume", **{"channel": channel}))

    def set_channel_pan(self, *, channel: int, value: int | None = None, pan: int | None = None) -> None:
        """Channel pan 0..12800 (6400 = center). Accepts ``pan=`` as an alias of ``value=``."""
        value = resolve_alias("value", value, pan=pan)
        self.invoke("set_channel_pan", **{"channel": channel, "value": value})

    def get_channel_pan(self, *, channel: int) -> int:
        """Read channel pan 0..12800 (symmetric with SetChannelPanAsync)."""
        return cast(int, self.invoke("get_channel_pan", **{"channel": channel}))

    def set_channel_pitch(self, *, channel: int, cents: int) -> None:
        """Channel pitch in cents (0 = center)."""
        self.invoke("set_channel_pitch", **{"channel": channel, "cents": cents})

    def get_channel_pitch(self, *, channel: int) -> int:
        """Read channel pitch in cents (symmetric with SetChannelPitchAsync)."""
        return cast(int, self.invoke("get_channel_pitch", **{"channel": channel}))

    def set_channel_muted(self, *, channel: int, muted: bool) -> None:
        """Mute/unmute a channel."""
        self.invoke("set_channel_muted", **{"channel": channel, "muted": muted})

    def get_channel_muted(self, *, channel: int) -> bool:
        """Read a channel's mute state (symmetric with SetChannelMutedAsync)."""
        return cast(bool, self.invoke("get_channel_muted", **{"channel": channel}))

    def set_channel_fx_route(self, *, channel: int, mixer_track: int) -> None:
        """Route a channel to Master (0) or an active ordinary mixer insert (within 1..500). Query mixer tracks for current indices; Current and dormant slots are unavailable."""
        self.invoke("set_channel_fx_route", **{"channel": channel, "mixerTrack": mixer_track})

    def get_channel_fx_route(self, *, channel: int) -> int:
        """Read a channel's mixer-track route (symmetric with SetChannelFxRouteAsync)."""
        return cast(int, self.invoke("get_channel_fx_route", **{"channel": channel}))

    def get_channel_control(self, *, channel: int, control: int) -> int:
        """Read a built-in channel control by its FL REC_Chan event index (the same command-bus namespace as volume 0, pan 1, pitch 4, mute 7 and mixer route 8, which are live-verified) as a raw native integer. Other entries follow the FL SDK REC_Chan table and are NOT live-verified yet: 2 filter cutoff, 3 filter resonance, 13 Sampler sample start offset, 14 Sampler time-stretch time; every value is FL's raw unit for that control, so read the current value first, write, and confirm in the Channel settings window. Index must be 0..8191 (below the hosted-plugin parameter block, which SetPluginParamAsync covers)."""
        return cast(int, self.invoke("get_channel_control", **{"channel": channel, "control": control}))

    def set_channel_control(self, *, channel: int, control: int, value: int) -> None:
        """Write a built-in channel control by REC_Chan event index with a raw native integer (see GetChannelControlAsync for the index table and its verification status). Built-in Sampler settings that are not REC events (reverse, fade in/out, trim/sample end, stretch mode) have no command-bus path; pre-process the audio file and ReplaceChannelSampleAsync instead."""
        self.invoke("set_channel_control", **{"channel": channel, "control": control, "value": value})

    def add_note(self, *, pattern: int, channel: int, key: int, start_tick: int, length_tick: int, velocity: int) -> None:
        """Add a note to a pattern's piano roll for a channel (pattern: 1-based, or <=0 = current). channel must be an existing zero-based channel rack index; query channels after adding or removing one. key = MIDI 0..131 (60 = middle C), startTick >= 0, lengthTick > 0 in PPQ ticks, velocity 0..127. Invalid values and startTick + lengthTick above int.MaxValue are rejected, not clamped."""
        self.invoke("add_note", **{"pattern": pattern, "channel": channel, "key": key, "startTick": start_tick, "lengthTick": length_tick, "velocity": velocity})

    def add_notes(self, *, pattern: int, notes: Sequence[NoteSpec]) -> None:
        """Add many notes to a pattern's piano roll in one batch — resolves the pattern and refreshes the editor once for the whole set, far faster than repeated AddNoteAsync. Each note carries its own channel, so a single call can author chords, melodies, or multi-channel drum grids. Every channel must exist in the current channel rack. Invalid channel references are rejected before any note in the batch is added; channel indices are never wrapped into another channel. Note values use the same strict ranges as AddNoteAsync. Cancellation may leave a completed prefix of the batch, but never an unfinished note-on waiting for its length."""
        self.invoke("add_notes", **{"pattern": pattern, "notes": notes})

    def get_ppq(self) -> int:
        """Project timebase: ticks per quarter note (PPQ)."""
        return cast(int, self.invoke("get_ppq"))

    def get_current_pattern(self) -> int:
        """Read the selected one-based pattern index."""
        return cast(int, self.invoke("get_current_pattern"))

    def select_pattern(self, *, index: int) -> None:
        """Select a one-based pattern index."""
        self.invoke("select_pattern", **{"index": index})

    def create_pattern(self) -> int:
        """Selects the first empty pattern; returns its index."""
        return cast(int, self.invoke("create_pattern"))

    def clear_pattern(self, *, index: int) -> None:
        """Remove all notes from the specified one-based pattern."""
        self.invoke("clear_pattern", **{"index": index})

    def get_pattern_name(self, *, index: int) -> str:
        """Read the name of a one-based pattern."""
        return cast(str, self.invoke("get_pattern_name", **{"index": index}))

    def list_patterns(self) -> str:
        """List patterns and their names."""
        return cast(str, self.invoke("list_patterns"))

    def get_channel_count(self) -> int:
        """Read the number of channels in the rack."""
        return cast(int, self.invoke("get_channel_count"))

    def select_channel(self, *, channel: int) -> None:
        """Exclusively select a zero-based channel (so the piano roll edits it)."""
        self.invoke("select_channel", **{"channel": channel})

    def get_channel_name(self, *, channel: int) -> str:
        """Read the name of a zero-based channel."""
        return cast(str, self.invoke("get_channel_name", **{"channel": channel}))

    def list_channels(self) -> str:
        """List channel indices and names."""
        return cast(str, self.invoke("list_channels"))

    def set_channel_name(self, *, channel: int, name: str) -> None:
        """Rename a channel (persists across save/reload) so the model's own name→index lookups keep working on channels it created."""
        self.invoke("set_channel_name", **{"channel": channel, "name": name})

    def set_channel_solo(self, *, channel: int) -> None:
        """Toggle exclusive SOLO on a channel (solo again = un-solo) — hear one part without muting every other channel by hand. The channel is zero-based, like every other channel operation."""
        self.invoke("set_channel_solo", **{"channel": channel})

    def get_mixer_track_count(self) -> int:
        """Native mixer cardinality: Master + active ordinary inserts + Current. Current has a special physical index, not count-1; use IFlStructuredQuery.QueryMixerTracksAsync to enumerate addressable Master/insert tracks."""
        return cast(int, self.invoke("get_mixer_track_count"))

    def add_mixer_track(self, *, after_track: int = -1) -> int:
        """Add an ordinary mixer insert after afterTrack (0 = Master), or append after the last ordinary insert when -1. Returns the new track index; requery track indices and routing after this structural edit. Unsupported native builds fail without adding a track."""
        return cast(int, self.invoke("add_mixer_track", **{"afterTrack": after_track}))

    def get_mixer_track_name(self, *, track: int) -> str:
        """Effective mixer track name (custom if set, else default by type: Master/Insert n/Current)."""
        return cast(str, self.invoke("get_mixer_track_name", **{"track": track}))

    def list_mixer_tracks(self) -> str:
        """Custom-named mixer tracks (+ Master) as "index: name", for name→index resolution."""
        return cast(str, self.invoke("list_mixer_tracks"))

    def set_mixer_track_name(self, *, track: int, name: str) -> None:
        """Rename a mixer track/bus (persists) so a bus the model creates is resolvable by name later."""
        self.invoke("set_mixer_track_name", **{"track": track, "name": name})

    def set_mixer_send(self, *, src_track: int, dst_track: int, level: float, active: bool = True) -> None:
        """Set a mixer send srcTrack->dstTrack. level uses FL's send scale where 0.8 = unity (0 dB; the level every insert's default Master route reads back), 1.0 = the knob top (about +4.05 dB) and 0 = silent but still connected; the same fader law as mixer volume (native int = level * 16000). With active=false the route is disconnected instead (FL's route-active core with enable 0; FL may show a "Disable routing?" confirmation when the destination is used as a plugin sidechain, so for unattended runs prefer level 0 on a route you cannot confirm). There is no sidechain flag: FL's "Sidechain to this track" is a differently flagged route whose location is not in the verified mixer layout, so a send always sums audio into the destination. Read routes back with IFlStructuredQuery.QueryMixerSendsAsync or the "sends:" line of ListMixerEffectsAsync."""
        self.invoke("set_mixer_send", **{"srcTrack": src_track, "dstTrack": dst_track, "level": level, "active": active})

    def set_mixer_eq_gain(self, *, track: int, band: int, value: int) -> None:
        """Mixer track EQ band gain (band 0=low,1=mid,2=high; value 0..0x40000000, ~0x20000000 = 0 dB)."""
        self.invoke("set_mixer_eq_gain", **{"track": track, "band": band, "value": value})

    def transport_play(self) -> None:
        """Start playback."""
        self.invoke("transport_play")

    def transport_stop(self) -> None:
        """Stop playback."""
        self.invoke("transport_stop")

    def transport_toggle_record(self) -> None:
        """Toggle recording."""
        self.invoke("transport_toggle_record")

    def set_loop_region(self, *, start_tick: int, end_tick: int) -> None:
        """Set the song loop / time-selection region to [startTick, endTick), with an exclusive end (for example, 0..1536 spans four bars at 96 PPQ). endTick <= the nonnegative start clears the loop."""
        self.invoke("set_loop_region", **{"startTick": start_tick, "endTick": end_tick})

    def list_available_plugins(self, *, effects: bool) -> str:
        """List installed plugins of a kind (effects=true → mixer effects, false → channel generators)."""
        return cast(str, self.invoke("list_available_plugins", **{"effects": effects}))

    def get_channel_plugin(self, *, channel: int) -> str:
        """Describe a channel's loaded generator plugin."""
        return cast(str, self.invoke("get_channel_plugin", **{"channel": channel}))

    def add_channel(self, *, plugin_name: str) -> int:
        """Add a new channel hosting the named generator plugin; returns its index. Instantiating a plugin runs its constructor on FL's UI thread, so this call is guarded for 20 seconds instead of the ordinary bridge budget (the FIRST plugin load of a session is the slow one: a cold VST scan/instantiate can hold FL's UI thread for many seconds). If even that expires, the channel's plugin name is re-read once after a short settle: when the generator did load the call succeeds and the recovery is recorded in the op log, and only a channel that still reports no generator raises a timeout saying the plugin is still initialising or waiting on a dialog."""
        return cast(int, self.invoke("add_channel", **{"pluginName": plugin_name}))

    def list_mixer_effects(self, *, track: int) -> str:
        """List the effects loaded in a mixer track's FX slots."""
        return cast(str, self.invoke("list_mixer_effects", **{"track": track}))

    def add_mixer_effect(self, *, track: int, slot: int, plugin_name: str) -> str:
        """Load/replace the named effect into a mixer track's FX slot (0-9) and return a verification line: the slot plus the effect name the slot reports after the load. Instantiating a plugin runs its constructor on FL's UI thread, so this call is guarded for 20 seconds instead of the ordinary bridge budget (the FIRST plugin load of a session is the slow one: a cold VST scan/instantiate can hold FL's UI thread for many seconds). If even that expires, the slot is re-read once after a short settle: when the effect did load the call succeeds and the verification line carries "loaded after N ms; FL's UI was blocked while the plugin initialised", and only a slot that is still empty raises a timeout saying the plugin is still initialising or waiting on a dialog."""
        return cast(str, self.invoke("add_mixer_effect", **{"track": track, "slot": slot, "pluginName": plugin_name}))

    def remove_mixer_effect(self, *, track: int, slot: int) -> None:
        """Clear a mixer track's FX slot."""
        self.invoke("remove_mixer_effect", **{"track": track, "slot": slot})

    def clone_mixer_effect(self, *, track: int, from_slot: int, to_slot: int) -> None:
        """Copy the effect type from one FX slot to another (type only, not parameter state)."""
        self.invoke("clone_mixer_effect", **{"track": track, "fromSlot": from_slot, "toSlot": to_slot})

    def list_plugin_params(self, *, channel_or_track: int, slot: int, filter: str | None = None) -> str:
        """List a plugin's parameters ("index: name"). slot < 0 = channel generator; else mixer track+slot. The name filter is OPTIONAL: omit it (or pass null) to list every parameter."""
        return cast(str, self.invoke("list_plugin_params", **{"channelOrTrack": channel_or_track, "slot": slot, "filter": filter}))

    def set_plugin_param(self, *, channel_or_track: int, slot: int, param_index: int, value: float) -> None:
        """Set a plugin parameter to a normalized value 0..1. slot < 0 = channel generator; else mixer track+slot."""
        self.invoke("set_plugin_param", **{"channelOrTrack": channel_or_track, "slot": slot, "paramIndex": param_index, "value": value})

    def list_samples(self, *, filter: str | None = None) -> str:
        """List available audio samples from every configured search root, optionally filtered by name (a case-insensitive substring of the path). The filter is OPTIONAL: omit it (or pass null) to browse. Entries are root-tagged relative paths with a legend line: [P] = FL's factory packs, [U] = the user's Image-Line documents content, [B1], [B2], ... = the folders FL's browser searches in addition to those (its "extra search folders"), which is where a user's own sample library normally lives. Pass an entry back verbatim to add_sample_channel / replace_channel_sample."""
        return cast(str, self.invoke("list_samples", **{"filter": filter}))

    def add_sample_channel(self, *, sample_path: str) -> int:
        """Add a new channel that plays the given audio sample file (drum/one-shot/loop); returns its index."""
        return cast(int, self.invoke("add_sample_channel", **{"samplePath": sample_path}))

    def replace_channel_sample(self, *, channel: int, sample_path: str) -> None:
        """Replace an existing channel's sample with a new audio file."""
        self.invoke("replace_channel_sample", **{"channel": channel, "samplePath": sample_path})

    def load_channel_plugin_state(self, *, channel: int, path: str, use_channel_loader: bool = False) -> str:
        """Load a plugin state or preset file into the generator ALREADY hosted by a channel, without replacing the plugin instance. Uses the wrapper's own state-file loader (dispatcher opcode 0x12). Live-verified on FL 26.1.3: a VST3 .vstpreset whose class id is the plugin's GUID string with braces/dashes removed loads into Serum 2 and changes the state in place, and a native plugin's own preset format can load too (GMS .gmsynth from Data/Patches/Plugin presets/Generators/GMS applied in place: 226 differing state bytes, the pad became audible), while a third-party proprietary preset (.SerumPreset) is silently ignored. Load a factory preset BEFORE authoring a native synth by parameter: a fresh GMS has no oscillator waveforms (chosen in the GUI, not parameters) and renders silence. An FL .fst preset for one of FL's OWN generators (Sytrus, Harmor, ... — a channel whose plugin is not the "Fruity Wrapper" VST host) is routed through FL's channel file loader automatically, because the dispatcher is a silent no-op for those files (FL 26.1.3.5570: a Sytrus factory preset left the state record byte-identical, the channel loader changed 99% of it). That loader also mutes the channel and renames it to the preset's base name, so the SDK snapshots the channel's name, mute state and mixer route before the load and restores all three after it; the verification line names the route used and what was restored. Set useChannelLoader to force that route for a wrapped plugin's .fst as well; it is refused for other formats because live it applied no state and renamed the channel. Refuses channels without a hosted plugin and, for .fst files, files that do not name the channel's current plugin (the name is matched both as UTF-16, how wrapped plugins store it, and as the single-byte string FL's own generators store). Returns a verification line: plugin name, the route used, same-instance check, parameter count and a comparison of the plugin's wrapper state record before and after the load (sizes, short hashes and the number of differing bytes), or an explicit "unavailable" note when no snapshot could be taken. Confirm the sound with parameter displays in a separate request or an isolated render."""
        return cast(str, self.invoke("load_channel_plugin_state", **{"channel": channel, "path": path, "useChannelLoader": use_channel_loader}))

    def load_mixer_effect_state(self, *, track: int, slot: int, path: str) -> str:
        """Load a plugin state or preset file into the effect ALREADY loaded in a mixer FX slot (0-9) through the wrapper's state-file loader (dispatcher opcode 0x12). Same format and identity rules as LoadChannelPluginStateAsync. Refuses empty slots."""
        return cast(str, self.invoke("load_mixer_effect_state", **{"track": track, "slot": slot, "path": path}))

    def get_channel_plugin_state(self, *, channel: int) -> str:
        """Read the CURRENT state of the generator hosted by a channel as base64 of its FL wrapper plugin-data record: the same bytes an FL project stores for the plugin (for a VST3 such as Serum 2 this embeds the processor and controller component states). Implemented through FL's own serializer: a temporary project copy is written with the direct writer used by SaveCopyAsync and the channel's record is extracted, so project note validation applies and the live project's path, title and dirty flag do not change. Pair with LoadChannelPluginStateAsync to learn parameter mappings by set-then-read, or to snapshot a patch without saving the project. Refuses channels without a hosted plugin; built-in Sampler channels store no wrapper record."""
        return cast(str, self.invoke("get_channel_plugin_state", **{"channel": channel}))

    def get_mixer_effect_state(self, *, track: int, slot: int) -> str:
        """Read the CURRENT state of the effect in a mixer FX slot (0-9; track 0 = Master) as base64 of its FL wrapper plugin-data record, through the same temporary project copy as GetChannelPluginStateAsync. Refuses empty slots."""
        return cast(str, self.invoke("get_mixer_effect_state", **{"track": track, "slot": slot}))

    def get_notes(self, *, pattern: int, channel: int, offset: int = 0) -> str:
        """Read piano-roll notes of a pattern (1-based, or <=0 = current); channel<0 = all. Paged: offset skips the first N notes (raw index); the output's continuation hint feeds it back in."""
        return cast(str, self.invoke("get_notes", **{"pattern": pattern, "channel": channel, "offset": offset}))

    def edit_notes(self, *, pattern: int, edits: Sequence[NoteEdit], allow_multiple: bool = False) -> int:
        """Edit EXISTING piano-roll notes in place, WITHOUT clearing the pattern (every other note is untouched, including fields the read tool doesn't surface — pan, fine pitch, release, cut, res). Each NoteEdit identifies a note by the (channel, key, startTick) triple GetNotesAsync shows, optionally narrowed by its current lengthTick, and applies whichever new fields it carries. FL allows several notes with the same triple (stacked duplicates), so an edit that matches more than one note is refused before anything is written unless allowMultiple is true, in which case every matching note receives the edit. Returns the number of notes changed. Playlist clips of the pattern keep their lengths (FL would otherwise re-derive them from the edited notes)."""
        return cast(int, self.invoke("edit_notes", **{"pattern": pattern, "edits": edits, "allowMultiple": allow_multiple}))

    def delete_notes(self, *, pattern: int, targets: Sequence[NoteRef], allow_multiple: bool = False) -> int:
        """Delete SPECIFIC existing piano-roll notes (matched by the (channel, key, startTick) triple, optionally narrowed by lengthTick), leaving the rest of the pattern intact — the surgical counterpart to ClearPatternAsync. A target that matches several stacked duplicates is refused before anything is deleted unless allowMultiple is true, which deletes all of them. Returns the number of notes deleted. Playlist clips of the pattern keep their lengths (FL would otherwise shrink them to the remaining notes); resize clips explicitly with ResizeClips."""
        return cast(int, self.invoke("delete_notes", **{"pattern": pattern, "targets": targets, "allowMultiple": allow_multiple}))

    def clone_pattern(self, *, source_pattern: int) -> int:
        """Duplicate a pattern's notes into a new empty pattern; returns the new pattern's 1-based index (0 if the source has nothing to clone). The full 24-byte note structs are copied, so pan/fine-pitch/ mute/etc. survive — a "make a variation of this part" without hand-recreating every note."""
        return cast(int, self.invoke("clone_pattern", **{"sourcePattern": source_pattern}))

    def set_pattern_name(self, *, index: int, name: str) -> None:
        """Rename a pattern (1-based; persists across save/reload) so the model can label its verse/ chorus/drop parts instead of leaving "Pattern N" — which its own list_patterns navigation relies on."""
        self.invoke("set_pattern_name", **{"index": index, "name": name})

    def list_playlist_tracks(self) -> str:
        """List customized playlist tracks and summarize default tracks."""
        return cast(str, self.invoke("list_playlist_tracks"))

    def set_track_name(self, *, track: int, name: str) -> None:
        """Rename a one-based playlist track."""
        self.invoke("set_track_name", **{"track": track, "name": name})

    def get_track_name(self, *, track: int) -> str:
        """Read a playlist track's name ("" when default; symmetric with SetTrackNameAsync)."""
        return cast(str, self.invoke("get_track_name", **{"track": track}))

    def set_track_color(self, *, track: int, rgb: int) -> None:
        """Set a playlist track color as packed RGB."""
        self.invoke("set_track_color", **{"track": track, "rgb": rgb})

    def get_track_color(self, *, track: int) -> int:
        """Read a playlist track's RGB color (symmetric with SetTrackColorAsync)."""
        return cast(int, self.invoke("get_track_color", **{"track": track}))

    def set_track_mute(self, *, track: int, muted: bool) -> None:
        """Mute or unmute a playlist track."""
        self.invoke("set_track_mute", **{"track": track, "muted": muted})

    def get_track_mute(self, *, track: int) -> bool:
        """Read a playlist track's mute state (symmetric with SetTrackMuteAsync)."""
        return cast(bool, self.invoke("get_track_mute", **{"track": track}))

    def set_track_solo(self, *, track: int) -> None:
        """Toggle exclusive SOLO on a playlist track (solo again = un-solo)."""
        self.invoke("set_track_solo", **{"track": track})

    def set_track_collapsed(self, *, track: int, collapsed: bool) -> None:
        """Collapse or expand a playlist track."""
        self.invoke("set_track_collapsed", **{"track": track, "collapsed": collapsed})

    def get_track_collapsed(self, *, track: int) -> bool:
        """Read a playlist track's collapsed state (symmetric with SetTrackCollapsedAsync)."""
        return cast(bool, self.invoke("get_track_collapsed", **{"track": track}))

    def select_track(self, *, track: int) -> None:
        """Select a playlist track."""
        self.invoke("select_track", **{"track": track})

    def list_clips(self, *, offset: int = 0, track: int = -1) -> str:
        """List active playlist clips, paged: offset skips the first N matching clips; track>0 filters to one playlist track (<=0 = all)."""
        return cast(str, self.invoke("list_clips", **{"offset": offset, "track": track}))

    def add_pattern_clip(self, *, pattern: int, track: int, start_tick: int, length_tick: int) -> None:
        """Add a pattern clip (pattern 1-based, matching notes/patterns; 0 or out-of-range throws) to a track at startTick; lengthTick<=0 = pattern length. A PATTERN CLIP DOES NOT LOOP: FL plays the pattern once from the clip start and the rest of the clip is silent (live-verified 2026-09-18, FL 26.1.3.5570: a 1-bar pattern in a 4-bar clip sounded in bar 1 only, bars 2-4 measured silent at the master), so a span that should repeat needs one clip per repetition - keep lengthTick at most the pattern's own length, or use the Python helper fl.playlist.tile_pattern, which places the whole run in one pass."""
        self.invoke("add_pattern_clip", **{"pattern": pattern, "track": track, "startTick": start_tick, "lengthTick": length_tick})

    def move_clip(self, *, clip_index: int, start_tick: int, track: int) -> None:
        """Move a playlist clip to a tick position and track."""
        self.invoke("move_clip", **{"clipIndex": clip_index, "startTick": start_tick, "track": track})

    def resize_clip(self, *, clip_index: int, length_tick: int) -> None:
        """Set a playlist clip duration in ticks."""
        self.invoke("resize_clip", **{"clipIndex": clip_index, "lengthTick": length_tick})

    def delete_clip(self, *, clip_index: int) -> None:
        """Remove a playlist clip by its collection index."""
        self.invoke("delete_clip", **{"clipIndex": clip_index})

    def set_clip_muted(self, *, clip_index: int, muted: bool) -> None:
        """Mute/unmute a playlist clip."""
        self.invoke("set_clip_muted", **{"clipIndex": clip_index, "muted": muted})

    def get_clip_muted(self, *, clip_index: int) -> bool:
        """Read a playlist clip's mute state (clip+0x13 bit 0x20), symmetric with SetClipMutedAsync — the read-before-write for granular clip-mute undo."""
        return cast(bool, self.invoke("get_clip_muted", **{"clipIndex": clip_index}))

    def delete_clips(self, *, clip_indices: Sequence[int]) -> None:
        """Delete many playlist clips in one pass. Indices are DEDUPED and removed high→low so the TList shift from an earlier removal never invalidates a later index; one recount + one repaint."""
        self.invoke("delete_clips", **{"clipIndices": clip_indices})

    def move_clips(self, *, moves: Sequence[ClipMove]) -> None:
        """Move many playlist clips in one pass (per-clip start/track poke), then one repaint. Moves don't reorder the collection, so all indices stay valid within the call."""
        self.invoke("move_clips", **{"moves": moves})

    def add_pattern_clips(self, *, clips: Sequence[PatternClipSpec]) -> None:
        """Place many pattern clips in one pass (each realized + inserted atomically), then one refresh/repaint. Clips are addressed by (pattern,track,start), so add-order index shifts don't matter. A positive lengthTick is pinned (as ResizeClips does), so the clip keeps that length even when the pattern's own content is longer or shorter; lengthTick <= 0 takes the pattern length and follows it. A pinned length is NOT a loop: FL plays the pattern once from the clip start and the rest of the clip is silent, so a repeating span is one spec per repetition (the Python helper fl.playlist.add_patterns(..., repeat=True) expands them for you)."""
        self.invoke("add_pattern_clips", **{"clips": clips})

    def resize_clips(self, *, resizes: Sequence[ClipResize]) -> None:
        """Resize many playlist clips in one pass (per-clip length poke), then one repaint."""
        self.invoke("resize_clips", **{"resizes": resizes})

    def set_clips_muted(self, *, clip_indices: Sequence[int], muted: bool) -> None:
        """Mute/unmute many playlist clips in one pass, then one repaint."""
        self.invoke("set_clips_muted", **{"clipIndices": clip_indices, "muted": muted})

    def slice_clip(self, *, clip_index: int, tick: int) -> None:
        """Slice/chop a clip into two at an absolute tick (audio stays continuous)."""
        self.invoke("slice_clip", **{"clipIndex": clip_index, "tick": tick})

    def duplicate_clip(self, *, clip_index: int) -> None:
        """Duplicate a clip right after itself on the same track."""
        self.invoke("duplicate_clip", **{"clipIndex": clip_index})

    def get_song_state(self) -> str:
        """Describe playback state, mode, and song position."""
        return cast(str, self.invoke("get_song_state"))

    def get_song_mode(self) -> bool:
        """Read song mode (true) vs pattern mode (false) — symmetric with SetSongModeAsync."""
        return cast(bool, self.invoke("get_song_mode"))

    def get_status(self) -> str:
        """FL's current status/hint bar text (the name/tooltip of whatever is under the mouse + current-operation messages, e.g. "Opening: Fruity Wrapper" at load), cleaned of FL's internal "tooltip|status" split and '^' markup. Empty when there is no active hint. Read-only; safe to poll."""
        return cast(str, self.invoke("get_status"))

    def set_song_mode(self, *, song: bool) -> None:
        """Select song playback when true, or pattern playback when false."""
        self.invoke("set_song_mode", **{"song": song})

    def get_metronome(self) -> bool:
        """Read FL's metronome click. It is mixed into what FL plays, so it also lands in a live per-insert capture; capture switches it off for the pass and restores it. Symmetric with SetMetronomeAsync; refused until the toggle's symbols resolve on the running build."""
        return cast(bool, self.invoke("get_metronome"))

    def set_metronome(self, *, on: bool) -> None:
        """Set FL's metronome click. It is mixed into what FL plays, so it also lands in a live per-insert capture; capture switches it off for the pass and restores it. The change goes through the very setter FL's own toggle Action calls, on FL's main thread, so FL repaints and reacts exactly as it does for a click; the value is re-read afterwards and a refusal is reported instead of assumed. Refused until the toggle's symbols resolve on the running build."""
        self.invoke("set_metronome", **{"on": on})

    def get_countdown(self) -> bool:
        """Read FL's countdown before recording (the "precount" toolbar toggle). With it on, a record+play pass spends a bar counting in before FL records anything, which silently shortens or empties a captured span; capture switches it off for the pass and restores it. Symmetric with SetCountdownAsync; refused until the toggle's symbols resolve on the running build."""
        return cast(bool, self.invoke("get_countdown"))

    def set_countdown(self, *, on: bool) -> None:
        """Set FL's countdown before recording (the "precount" toolbar toggle). With it on, a record+play pass spends a bar counting in before FL records anything, which silently shortens or empties a captured span; capture switches it off for the pass and restores it. The change goes through the very setter FL's own toggle Action calls, on FL's main thread, so FL repaints and reacts exactly as it does for a click; the value is re-read afterwards and a refusal is reported instead of assumed. Refused until the toggle's symbols resolve on the running build."""
        self.invoke("set_countdown", **{"on": on})

    def get_wait_for_input(self) -> bool:
        """Read FL's "wait for input to start playing" toggle. With it on, play does not start until FL sees note or audio input, so an automated record+play pass hangs until its deadline with nothing recorded; capture switches it off for the pass and restores it. Symmetric with SetWaitForInputAsync; refused until the toggle's symbols resolve on the running build."""
        return cast(bool, self.invoke("get_wait_for_input"))

    def set_wait_for_input(self, *, on: bool) -> None:
        """Set FL's "wait for input to start playing" toggle. With it on, play does not start until FL sees note or audio input, so an automated record+play pass hangs until its deadline with nothing recorded; capture switches it off for the pass and restores it. The change goes through the very setter FL's own toggle Action calls, on FL's main thread, so FL repaints and reacts exactly as it does for a click; the value is re-read afterwards and a refusal is reported instead of assumed. Refused until the toggle's symbols resolve on the running build."""
        self.invoke("set_wait_for_input", **{"on": on})

    def get_loop_record(self) -> bool:
        """Read FL's loop-recording toggle. With it on, a pass over a looped range keeps every take instead of one recording, which changes what a capture writes and what the project ends up holding; capture switches it off for the pass and restores it. Symmetric with SetLoopRecordAsync; refused until the toggle's symbols resolve on the running build."""
        return cast(bool, self.invoke("get_loop_record"))

    def set_loop_record(self, *, on: bool) -> None:
        """Set FL's loop-recording toggle. With it on, a pass over a looped range keeps every take instead of one recording, which changes what a capture writes and what the project ends up holding; capture switches it off for the pass and restores it. The change goes through the very setter FL's own toggle Action calls, on FL's main thread, so FL repaints and reacts exactly as it does for a click; the value is re-read afterwards and a refusal is reported instead of assumed. Refused until the toggle's symbols resolve on the running build."""
        self.invoke("set_loop_record", **{"on": on})

    def get_blend_recorded_notes(self) -> bool:
        """Read FL's "blend recorded notes" (overdub) toggle: recorded notes are merged into the existing pattern instead of replacing it. Irrelevant to audio capture, exposed because note recording needs it. Symmetric with SetBlendRecordedNotesAsync; refused until the toggle's symbols resolve on the running build."""
        return cast(bool, self.invoke("get_blend_recorded_notes"))

    def set_blend_recorded_notes(self, *, on: bool) -> None:
        """Set FL's "blend recorded notes" (overdub) toggle: recorded notes are merged into the existing pattern instead of replacing it. Irrelevant to audio capture, exposed because note recording needs it. The change goes through the very setter FL's own toggle Action calls, on FL's main thread, so FL repaints and reacts exactly as it does for a click; the value is re-read afterwards and a refusal is reported instead of assumed. Refused until the toggle's symbols resolve on the running build."""
        self.invoke("set_blend_recorded_notes", **{"on": on})

    def get_recording_active(self) -> bool:
        """Read whether FL's audio engine currently has a recording pass running, independently of the toolbar record button's paint state. This is the counter FL's own apply-recording-filter routine checks before it computes the effective filter, so it is true while the engine is actually recording and false otherwise; use it to verify that a record+play pass really started when the button byte is in doubt. Refused until the recording-state symbol resolves on the running build."""
        return cast(bool, self.invoke("get_recording_active"))

    def get_record_pressed(self) -> bool:
        """Read whether FL's transport record button is engaged (the toolbar toggle FL's own scripting reports as ui.isRecording): true means the next play records. TransportToggleRecordAsync only flips it, and FL leaves the button engaged after a recording pass, so a caller that needs recording ON must read this first and toggle only when it differs -- a blind toggle before a second pass switches recording OFF and that pass records nothing. Refused until the record-button symbols resolve on the running build."""
        return cast(bool, self.invoke("get_record_pressed"))

    def get_recording_filter(self) -> int:
        """Read FL's global recording filter — the bitmask behind the record button's right-click "Recording filter" submenu, which decides what a recording pass is allowed to capture. Bits (FL's own menu-item tags): 1 = Automation, 2 = Notes, 4 = Audio, 8 = Clips; FL 2025 has no Clips item, so bit 8 is unused there. Bit 4 must be set or FL silently writes no WAV for an armed mixer insert, which is the single most common reason live audio capture produces nothing. FL keeps this value only in memory while it runs (its registry home, RecordingFilter2 under HKCU > Software > Image-Line > FL Studio 26 > General > FruityLoopsMainForm, is read at startup and written at exit), so it must be read and set through the running engine. Refused until the recording-filter symbols resolve on the running build."""
        return cast(int, self.invoke("get_recording_filter"))

    def set_recording_filter(self, *, flags: int) -> None:
        """Set FL's global recording filter bitmask (see GetRecordingFilterAsync for the bits) by invoking the very routine FL's own "Recording filter" menu items call, on FL's main thread, so the menu checkmarks and the record button follow. The value is re-read afterwards and a mismatch is reported instead of assumed. Only the flags given are kept, so read first and combine when preserving the user's other choices; the value is restored to what it was by callers that changed it for one pass. Refused until the recording-filter symbols resolve on the running build."""
        self.invoke("set_recording_filter", **{"flags": flags})

    def seek(self, *, tick: int) -> None:
        """Move the song playhead to an absolute tick (PPQ)."""
        self.invoke("seek", **{"tick": tick})

    def list_markers(self) -> str:
        """List song time markers."""
        return cast(str, self.invoke("list_markers"))

    def add_marker(self, *, tick: int, name: str) -> None:
        """Add a named song marker at a tick position."""
        self.invoke("add_marker", **{"tick": tick, "name": name})

    def delete_marker(self, *, index: int) -> None:
        """Delete a song time marker by its zero-based index in ListMarkersAsync order. Refuses a missing index without changing the project. FL extends renders and the play range to the last marker, so remove trailing markers to shorten an audition."""
        self.invoke("delete_marker", **{"index": index})

    def open_project(self, *, path: str) -> None:
        """Open the project at the specified path."""
        self.invoke("open_project", **{"path": path})

    def save_project(self, *, path: str) -> None:
        """Save the project using the specified path. Rejects orphan note channel references before invoking FL's serializer; inspect and repair the reported note before retrying."""
        self.invoke("save_project", **{"path": path})

    def new_project(self) -> None:
        """Create a new project through FL Studio."""
        self.invoke("new_project")

    def get_project_info(self) -> str:
        """Read project identity and metadata."""
        return cast(str, self.invoke("get_project_info"))

    def save_project_as(self, *, path: str) -> None:
        """Save As: write to a new path and make it the current project (updates title + recent files). Orphan note validation runs before changing project identity."""
        self.invoke("save_project_as", **{"path": path})

    def save_copy(self, *, path: str) -> None:
        """Save a full .flp copy of the live project to a path WITHOUT changing the current project path/title. Supports UNTITLED projects through FL's low-level direct writer. Orphan note channel references are rejected before writing; no notes are deleted automatically."""
        self.invoke("save_copy", **{"path": path})

    def save_new_version(self) -> None:
        """Save an auto-incremented new version and make it current. Orphan note validation runs before changing project identity."""
        self.invoke("save_new_version")

    def list_recent_projects(self) -> str:
        """List recently opened project paths."""
        return cast(str, self.invoke("list_recent_projects"))

    def list_arrangements(self) -> str:
        """List arrangement indices and names."""
        return cast(str, self.invoke("list_arrangements"))

    def add_arrangement(self, *, name: str | None) -> int:
        """Add a new (empty) arrangement and switch to it; returns its index."""
        return cast(int, self.invoke("add_arrangement", **{"name": name}))

    def clone_arrangement(self, *, src_idx: int, name: str | None) -> int:
        """Clone an arrangement (deep copy incl. clips); srcIdx<0 = current. Returns the new index."""
        return cast(int, self.invoke("clone_arrangement", **{"srcIdx": src_idx, "name": name}))

    def rename_arrangement(self, *, idx: int, name: str) -> None:
        """Rename an arrangement by index."""
        self.invoke("rename_arrangement", **{"idx": idx, "name": name})

    def get_arrangement_name(self, *, idx: int) -> str:
        """Read an arrangement's name ("" when unnamed; symmetric with RenameArrangementAsync)."""
        return cast(str, self.invoke("get_arrangement_name", **{"idx": idx}))

    def delete_arrangement(self, *, idx: int) -> None:
        """Delete an arrangement by index."""
        self.invoke("delete_arrangement", **{"idx": idx})

    def select_arrangement(self, *, idx: int) -> None:
        """Switch to an arrangement by index."""
        self.invoke("select_arrangement", **{"idx": idx})

    def create_automation_clip(self, *, target: AutomationTarget, track: int, start_tick: int, length_tick: int, name: str | None = None) -> AutomationClipResult:
        """Create a linked automation channel and place its clip on one-based playlist track 1..500. Times are ticks, length positive. Initial linking is part of native creation; failures may leave the channel created, so inspect before retrying."""
        return decode_record(AutomationClipResult, self.invoke("create_automation_clip", **{"target": target, "track": track, "startTick": start_tick, "lengthTick": length_tick, "name": name}))

    def add_automation_clip(self, *, channel: int, track: int, start_tick: int, length_tick: int) -> int:
        """Place an existing Automation Clip generator on one-based playlist track 1..500. Returns the new playlist clip index; startTick is nonnegative and lengthTick positive."""
        return cast(int, self.invoke("add_automation_clip", **{"channel": channel, "track": track, "startTick": start_tick, "lengthTick": length_tick}))

    def set_automation_points(self, *, channel: int, points: Sequence[AutomationPointSpec]) -> None:
        """Replace an automation envelope with 2..4000 linear points. Times are beats, first time zero, later times strictly increasing; values 0..1, tension -1..1, curve must be zero."""
        self.invoke("set_automation_points", **{"channel": channel, "points": points})

    def list_automation_points(self, *, channel: int) -> str:
        """List an automation channel curve with times, values, and tension."""
        return cast(str, self.invoke("list_automation_points", **{"channel": channel}))

    def add_automation_point(self, *, channel: int, time_beats: float, value: float, tension: float) -> None:
        """Add an automation point: time in beats, value 0..1, tension -1..1 (inserts in time order)."""
        self.invoke("add_automation_point", **{"channel": channel, "timeBeats": time_beats, "value": value, "tension": tension})

    def delete_automation_point(self, *, channel: int, index: int) -> None:
        """Delete an automation point by index and recompute its curve. The first and last points are protected endpoints and cannot be deleted; edit them with SetAutomationPointAsync."""
        self.invoke("delete_automation_point", **{"channel": channel, "index": index})

    def set_automation_point(self, *, channel: int, index: int, value: float, tension: float) -> None:
        """Change one existing automation point's value 0..1 and tension -1..1 in place, keeping its time. Works for the protected first and last points. Refuses a missing index and curves that contain non-linear points (replace those with SetAutomationPointsAsync)."""
        self.invoke("set_automation_point", **{"channel": channel, "index": index, "value": value, "tension": tension})

    def open_export_dialog(self, *, format_index: int = 0) -> None:
        """Opens FL's audio Export dialog for the user to finish (format/path/Render)."""
        self.invoke("open_export_dialog", **{"formatIndex": format_index})

    def open_chat_tab(self) -> None:
        """Open (or focus) the native "FruityLink AI" chat tab in FL's browser."""
        self.invoke("open_chat_tab")

    def close_chat_tab(self) -> None:
        """Hide the chat tab and restore the browser content hook."""
        self.invoke("close_chat_tab")

    def chat_poll(self) -> str:
        """Return + clear the user's submitted chat message (empty if none pending)."""
        return cast(str, self.invoke("chat_poll"))

    def chat_say(self, *, text: str) -> None:
        """Append a line to the chat display (runs on FL's main thread)."""
        self.invoke("chat_say", **{"text": text})
