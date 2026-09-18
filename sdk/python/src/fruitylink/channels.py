"""Zero-based channel rack objects. References are indices, not persistent IDs."""

import base64
import weakref
from collections.abc import Callable, Iterator
from enum import IntEnum

from ._collection import checked_index
from ._properties import IndexedObject, NativeProperty
from .automation_links import LinkMode, check
from .automation_records import AutomationTarget
from .levels import CHANNEL_PAN_MAX, CHANNEL_VOLUME_MAX, channel_volume_from_db, channel_volume_to_db
from .models import ChannelInfo
from .operations import Operations
from .plugins import Parameters

RETIRED_PREFIX = "(unused) "
"""Name prefix ``Channel.retire`` applies; FL has no channel-delete path the bridge can call."""


class ChannelControl(IntEnum):
    """FL REC_Chan event indices addressed by ``Channel.control`` / ``set_control``.

    VOLUME, PAN, PITCH, MUTE and MIXER_TRACK are live-verified through the same command bus. The
    others come from the FL SDK's REC_Chan table and are NOT live-verified yet: read the current
    value first, write, then confirm in the Channel settings window (values are FL's raw units).
    Sampler settings that are not REC events (reverse, fade in/out, trim/sample end, stretch mode)
    have no path at all; pre-process the audio file and ``replace_sample`` instead.
    """

    VOLUME = 0
    PAN = 1
    FILTER_CUTOFF = 2
    FILTER_RESONANCE = 3
    PITCH = 4
    MUTE = 7
    MIXER_TRACK = 8
    SAMPLE_OFFSET = 13
    STRETCH_TIME = 14
_registries: "weakref.WeakKeyDictionary[Operations, dict[int, str]]" = weakref.WeakKeyDictionary()


def _sample_paths(ops: Operations) -> dict[int, str]:
    """Session-local channel index -> sample path memory shared by Channels and fl.samples."""
    return _registries.setdefault(ops, {})


def _link_guard(kind: str) -> "Callable[[IndexedObject], object]":
    def guard(channel: IndexedObject) -> object:
        return check(channel._ops, AutomationTarget(kind, channel.index), stacklevel=4)
    return guard


class Channel(IndexedObject):
    """One channel-rack channel. ``pan`` is the native 0..12800 scale with 6400 = center
    (mixer track pan uses a different, signed scale). ``volume`` is raw 0..12800 on FL's power
    curve (10240 = 0 dB, FL's default 10000 = about -0.4 dB, 5000 = about -13 dB); ``volume_db``
    converts through the SDK's model of that law (see ``fruitylink.levels``).

    ``volume``, ``pan`` and ``pitch`` warn with ``AutomationLinkedWarning`` when an automation clip
    channel owns the control: FL reapplies that clip's initial value on every play, so the write is
    not audible afterwards (see ``fruitylink.automation_links``). ``set_volume`` / ``set_pan`` /
    ``set_pitch`` take ``linked=`` to raise or skip that check instead, and the raw ``set_control``
    path is never checked.
    """

    name = NativeProperty("get_channel_name", "set_channel_name", "channel", "name", str)
    volume = NativeProperty("get_channel_volume", "set_channel_volume", "channel", "value", int,
                            guard=_link_guard("channel_volume"))
    pan = NativeProperty("get_channel_pan", "set_channel_pan", "channel", "value", int,
                         guard=_link_guard("channel_pan"))
    pitch = NativeProperty("get_channel_pitch", "set_channel_pitch", "channel", "cents", int,
                           guard=_link_guard("channel_pitch"))
    muted = NativeProperty("get_channel_muted", "set_channel_muted", "channel", "muted", bool)
    mixer_track = NativeProperty("get_channel_fx_route", "set_channel_fx_route", "channel", "mixerTrack", int)

    @property
    def parameters(self) -> Parameters:
        return Parameters(self._ops, self.index)

    @property
    def volume_db(self) -> float:
        """Volume as modelled dB (10240 -> 0.0, 10000 -> about -0.4). Setting writes ``channel_volume_from_db``."""
        return channel_volume_to_db(self.volume)

    @volume_db.setter
    def volume_db(self, db: float) -> None:
        self.volume = channel_volume_from_db(db)

    def set_volume(self, value: int | None = None, *, db: float | None = None,
                   linked: LinkMode = "warn") -> int:
        """Set the raw volume (0..12800) or a modelled dB gain (``db=``, at most +4.05); returns the raw value written.

        ``linked`` decides what happens when an automation clip channel owns this channel's volume:
        ``"warn"`` (default) emits ``AutomationLinkedWarning``, ``"raise"`` raises it before writing,
        ``"ignore"`` writes without the lookup. FL reapplies the clip's initial value on every play,
        so a warned write lasts only until playback restarts; release the curve with
        ``fl.automation.release()`` to move the pinned value instead.
        """
        if (value is None) == (db is None):
            raise TypeError("Pass exactly one of value= (raw 0..12800) or db=.")
        raw = channel_volume_from_db(db) if db is not None else checked_index(-1 if value is None else value,
                                                                              maximum=CHANNEL_VOLUME_MAX)
        check(self._ops, AutomationTarget.channel_volume(self.index), linked=linked)
        self._ops.set_channel_volume(channel=self.index, value=raw)
        return raw

    def set_pan(self, value: int, *, linked: LinkMode = "warn") -> int:
        """Set the raw pan 0..12800 (6400 = center); returns the value written. ``linked`` as in ``set_volume``."""
        checked_index(value, maximum=CHANNEL_PAN_MAX)
        check(self._ops, AutomationTarget.channel_pan(self.index), linked=linked)
        self._ops.set_channel_pan(channel=self.index, value=value)
        return value

    def set_pitch(self, cents: int, *, linked: LinkMode = "warn") -> int:
        """Set the pitch offset in cents (0 = center); returns the value written. ``linked`` as in ``set_volume``."""
        if isinstance(cents, bool) or not isinstance(cents, int):
            raise TypeError("Channel pitch is an integer number of cents.")
        check(self._ops, AutomationTarget.channel_pitch(self.index), linked=linked)
        self._ops.set_channel_pitch(channel=self.index, cents=cents)
        return cents

    def control(self, index: int) -> int:
        """Raw value of a built-in channel control by REC_Chan index (``ChannelControl``); see its verification notes."""
        return self._ops.get_channel_control(channel=self.index, control=checked_index(int(index), maximum=0x1FFF))

    def set_control(self, index: int, value: int) -> None:
        """Write a built-in channel control by REC_Chan index with a raw native integer (not clamped)."""
        if isinstance(value, bool) or not isinstance(value, int):
            raise TypeError("Channel control values are raw native integers.")
        self._ops.set_channel_control(channel=self.index, control=checked_index(int(index), maximum=0x1FFF),
                                      value=value)

    @property
    def stretch_time(self) -> int:
        """Sampler time-stretch "Time" control (REC_Chan_StretchTime = 14) as a raw native integer.

        Not live-verified: the index follows the FL SDK table and the unit is FL's own. Harvest it
        by reading, writing a value and checking the Time knob in the Channel settings window.
        """
        return self.control(ChannelControl.STRETCH_TIME)

    @stretch_time.setter
    def stretch_time(self, value: int) -> None:
        self.set_control(ChannelControl.STRETCH_TIME, value)

    @property
    def sample_offset(self) -> int:
        """Sampler sample-start offset control (REC_Chan_SmpOffset = 13) as a raw native integer; not live-verified."""
        return self.control(ChannelControl.SAMPLE_OFFSET)

    @sample_offset.setter
    def sample_offset(self, value: int) -> None:
        self.set_control(ChannelControl.SAMPLE_OFFSET, value)

    def select(self) -> None:
        self._ops.select_channel(channel=self.index)

    def toggle_solo(self) -> None:
        self._ops.set_channel_solo(channel=self.index)

    def plugin_text(self) -> str:
        return self._ops.get_channel_plugin(channel=self.index)

    def replace_sample(self, path: str) -> None:
        self._ops.replace_channel_sample(channel=self.index, sample_path=path)
        _sample_paths(self._ops)[self.index] = path

    def retire(self, *, name: str | None = None) -> str:
        """Park a channel that cannot be deleted: mute it, route it to Master and rename it.

        FL exposes channel deletion only through the channel-rack context menu (no engine call the
        bridge can make), so the template's empty Sampler channel stays in the rack. This makes it
        inert and obvious: muted, routed to Master (0) so it holds no bus, and renamed
        ``"(unused) <old name>"`` (or ``name``). Notes and clips are untouched; clear patterns
        yourself if it had any. Returns the new name. Reversible by hand.
        """
        if name is not None and not isinstance(name, str):
            raise TypeError("Retired channel name must be a string or None.")
        current = self.name
        new_name = name if name is not None else (current if current.startswith(RETIRED_PREFIX)
                                                   else RETIRED_PREFIX + current)
        self.muted = True
        self.mixer_track = 0
        self.name = new_name
        return new_name

    def load_state(self, path: str, *, use_channel_loader: bool = False) -> str:
        """Load a plugin preset/state file (.fst, or the hosted plugin's native format such as .vstpreset)
        into the generator already on this channel. An FL ``.fst`` for one of FL's own generators (Sytrus,
        Harmor, ...) is routed through FL's channel loader automatically, with the channel's name, mute state
        and mixer route restored afterwards. Returns the host's verification line (it names the route used);
        verify the sound through parameter displays or an isolated render, not the return value alone."""
        return self._ops.load_channel_plugin_state(channel=self.index, path=path, use_channel_loader=use_channel_loader)

    def load_preset(self, path: str) -> str:
        """Load the hosted plugin's own preset file into the generator already on this channel.

        Same entry as ``load_state``, named for the common case. Formats known live on FL 26.1.3: FL ``.fst``;
        VST3 ``.vstpreset`` (Serum 2, class id = GUID string without braces/dashes); GMS ``.gmsynth``
        (factory folder ``<FL>\\Data\\Patches\\Plugin presets\\Generators\\GMS``). A raw ``.SerumPreset``
        is ignored (use ``fruitylink_serum.load_preset``).

        A wrapped plugin's preset goes through the wrapper's "load state from file" entry (opcode 0x12, no
        re-instantiation). An FL-native generator's ``.fst`` (``Data\\Patches\\Plugin presets\\Generators\\
        Sytrus|Harmor|...``) goes through FL's channel loader instead, because that dispatcher is a silent
        no-op for those files; the channel loader mutes the channel and renames it to the preset's base name,
        so the SDK restores the name, mute state and mixer route and says so in its verification line. Do it
        BEFORE authoring by parameter: a fresh GMS created by ``channels.add`` has no oscillator waveforms
        loaded (its "Synth Waves" are chosen in the GUI and are not parameters), so parameter-only patches
        render silence until a preset is loaded. Returns the host's verification line (route, same instance,
        differing state bytes); confirm with parameter displays in the next request or an isolated render.
        """
        return self.load_state(path)

    def get_state(self) -> bytes:
        """Current wrapper state of the hosted generator: the raw FL plugin-data record (for a VST3 such as
        Serum 2 it embeds the XferJson component blocks). Read through a temporary project copy written by FL's
        serializer, so the live project is unchanged. Pair with load_state() to learn parameter mappings."""
        return base64.b64decode(self._ops.get_channel_plugin_state(channel=self.index))


class Channels:
    def __init__(self, ops: Operations) -> None:
        self._ops = ops

    def __getitem__(self, index: int) -> Channel:
        return Channel(self._ops, checked_index(index))

    def __len__(self) -> int:
        return self._ops.get_channel_count()

    def __iter__(self) -> Iterator[Channel]:
        return (self[item.index] for item in self.list())

    def list(self) -> tuple[ChannelInfo, ...]:
        return self._ops.query_channels()

    def list_text(self) -> str:
        return self._ops.list_channels()

    def add(self, plugin: str, *, name: str | None = None) -> Channel:
        """Add a channel hosting an installed generator.

        Instantiating a plugin is guarded for 20 s (not the ordinary bridge budget) because the first
        plugin load of a session runs a cold VST scan on FL's UI thread; when even that expires the host
        re-reads the channel once and only fails if no generator arrived.
        """
        channel = self[self._ops.add_channel(plugin_name=plugin)]
        if name is not None:
            channel.name = name
        return channel

    def add_sample(self, path: str, *, name: str | None = None) -> Channel:
        channel = self[self._ops.add_sample_channel(sample_path=path)]
        _sample_paths(self._ops)[channel.index] = path
        if name is not None:
            channel.name = name
        return channel

    def retire(self, index: int, *, name: str | None = None) -> str:
        """``self[index].retire(name=name)``: the documented stand-in for channel deletion."""
        return self[index].retire(name=name)

    def find(self, name: str) -> Channel:
        matches = [item.index for item in self.list() if item.name == name]
        if len(matches) != 1:
            raise LookupError(f"Expected one channel named {name!r}; found {len(matches)}.")
        return self[matches[0]]

    def load_preset_file(self, index: int, path: str) -> str:
        """``fl.channels.load_preset_file(index, path)``: load a plugin preset file into the generator on
        channel ``index`` (see ``Channel.load_preset`` for the formats known to work and the GMS recipe)."""
        return self[index].load_preset(path)
