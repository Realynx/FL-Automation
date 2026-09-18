"""Mixer tracks and effect slots. Layout compatibility is decided by the native profile."""

import base64
import re
from collections.abc import Iterator

from ._collection import checked_index
from ._properties import IndexedObject, NativeProperty
from .automation_links import LinkMode, check
from .automation_records import AutomationTarget
from .errors import FruityLinkError, ProtocolError
from .levels import (
    MIXER_PAN_MAX,
    MIXER_VOLUME_MAX,
    SEND_UNITY,
    mixer_volume_from_db,
    mixer_volume_to_db,
    send_level_from_db,
    send_level_to_db,
)
from .models import MixerSendInfo, MixerTrackInfo
from .operations import Operations
from .plugins import Parameters

MAX_INSERTS = 500
"""FL's mixer capacity in ordinary inserts (native count 502 = Master + 500 inserts + Current)."""

_SLOT_LINE = re.compile(r"^slot (\d+): (.+)$")
_UNREAD = object()
"""Sentinel for "this slot's plugin name has not been read yet" (None means "the slot is empty")."""


def effect_names(ops: Operations, track: int) -> dict[int, str]:
    """``{slot: plugin name}`` for one mixer track, from ONE ``list_mixer_effects`` call.

    Only slots that hold an effect appear; FL exposes no per-slot name getter, so this parses the
    host's ``slot N: <name>`` lines (the same two native fields a load verifies itself against).
    """
    names: dict[int, str] = {}
    for line in ops.list_mixer_effects(track=checked_index(track)).splitlines():
        found = _SLOT_LINE.match(line.strip())
        if found:
            names[int(found.group(1))] = found.group(2).strip()
    return names


class EffectSlot:
    """One mixer FX slot. ``plugin_name`` says what is loaded, ``is_empty`` whether anything is.

    ``add_mixer_effect`` returns a verification line but nothing else described a slot afterwards, so
    an agent could not confirm what it had loaded. Reading ``plugin_name`` costs ONE
    ``list_mixer_effects`` call and is then remembered on this object; ``refresh()`` forgets it, and
    ``load`` / ``load_state`` / ``clear`` forget it for you. A fresh ``fl.mixer[t].effects[s]`` starts
    with no memory, so it reads the live slot.
    """

    def __init__(self, ops: Operations, track: int, slot: int) -> None:
        self._ops = ops
        self.track = checked_index(track)
        self.index = checked_index(slot, maximum=9)
        self._name: object = _UNREAD

    @property
    def parameters(self) -> Parameters:
        return Parameters(self._ops, self.track, self.index)

    @property
    def plugin_name(self) -> str | None:
        """The effect loaded in this slot as FL names it (``"Pro-Q 4"``), or None when the slot is empty.

        One ``list_mixer_effects`` call per read, cached on this object until ``refresh()``. The name is
        the plugin database name ``EffectSlot.load`` / ``add_mixer_effect`` take, so it round-trips.
        """
        if self._name is _UNREAD:
            self._name = effect_names(self._ops, self.track).get(self.index)
        return self._name if isinstance(self._name, str) else None

    @property
    def is_empty(self) -> bool:
        """True when no effect is loaded here (reads ``plugin_name``, so the same one cached call)."""
        return self.plugin_name is None

    def refresh(self) -> None:
        """Forget the cached plugin name so the next read asks the host again."""
        self._name = _UNREAD

    def __repr__(self) -> str:
        """``EffectSlot(track=64, slot=0, plugin='Pro-Q 4')``; the plugin is omitted when unreadable.

        A repr must never fail or hide a problem, so a host that cannot answer ``list_mixer_effects``
        yields ``EffectSlot(track=64, slot=0)`` rather than an exception or a wrong name.
        """
        where = f"EffectSlot(track={self.track}, slot={self.index}"
        try:
            return f"{where}, plugin={self.plugin_name!r})"
        except (FruityLinkError, LookupError, OSError, TimeoutError, ValueError):
            return f"{where})"

    def load(self, plugin: str) -> str:
        """Load an installed effect into this slot and return the host's verification line.

        Instantiating a plugin is guarded for 20 s (not the ordinary bridge budget) because the first
        plugin load of a session runs a cold VST scan on FL's UI thread; when even that expires but the
        effect did land, the line carries ``loaded after N ms; FL's UI was blocked while the plugin
        initialised`` instead of raising.
        """
        self.refresh()
        return self._ops.add_mixer_effect(track=self.track, slot=self.index, plugin_name=plugin)

    def clear(self) -> None:
        self.refresh()
        self._ops.remove_mixer_effect(track=self.track, slot=self.index)

    def clone_type_to(self, slot: int) -> None:
        """Copy plugin type to a slot on this track; parameter state is not copied."""
        self._ops.clone_mixer_effect(track=self.track, from_slot=self.index, to_slot=checked_index(slot, maximum=9))

    def load_state(self, path: str) -> str:
        """Load a plugin preset/state file into the effect already loaded in this slot. Returns the host's
        verification line; the slot must already hold the matching plugin."""
        self.refresh()
        return self._ops.load_mixer_effect_state(track=self.track, slot=self.index, path=path)

    def get_state(self) -> bytes:
        """Current wrapper state of the effect in this slot (raw FL plugin-data record), read through a
        temporary project copy; the live project is unchanged."""
        return base64.b64decode(self._ops.get_mixer_effect_state(track=self.track, slot=self.index))


class Effects:
    def __init__(self, ops: Operations, track: int) -> None:
        self._ops = ops
        self.track = track

    def __getitem__(self, slot: int) -> EffectSlot:
        return EffectSlot(self._ops, self.track, slot)

    def __iter__(self) -> Iterator[EffectSlot]:
        return (self[index] for index in range(10))

    def names(self) -> dict[int, str]:
        """``{slot: plugin name}`` for the loaded slots of this track, from one ``list_mixer_effects`` call.

        The cheap way to see a whole chain: ten ``EffectSlot.plugin_name`` reads are ten calls, this is one.
        """
        return effect_names(self._ops, self.track)

    def loaded(self) -> tuple[EffectSlot, ...]:
        """The slots that hold an effect, in slot order, each already carrying its cached plugin name."""
        found = []
        for slot, name in sorted(self.names().items()):
            item = self[slot]
            item._name = name
            found.append(item)
        return tuple(found)

    def list_text(self) -> str:
        return self._ops.list_mixer_effects(track=self.track)


def _volume_link(track: IndexedObject) -> object:
    return check(track._ops, AutomationTarget.mixer_volume(track.index), stacklevel=4)


def _pan_link(track: IndexedObject) -> object:
    return check(track._ops, AutomationTarget.mixer_pan(track.index), stacklevel=4)


class MixerTrack(IndexedObject):
    """One mixer track. ``pan`` is a SIGNED native integer -6400..6400 with 0 = center and
    negative = left; this differs from channel pan (0..12800, 6400 = center). ``volume`` is
    the raw native 0..16000 fader scale (12800 = 0 dB, 16000 = about +4.05 dB); ``volume_db`` converts
    through the SDK's model of FL's fader law (see ``fruitylink.levels``).

    ``volume`` and ``pan`` warn with ``AutomationLinkedWarning`` when an automation clip channel owns
    the control: FL reapplies that clip's initial value on every play, so the write is not audible
    afterwards (see ``fruitylink.automation_links``). ``set_volume`` / ``set_pan`` take ``linked=`` to
    raise or skip that check instead.
    """

    name = NativeProperty("get_mixer_track_name", "set_mixer_track_name", "track", "name", str)
    volume = NativeProperty("get_mixer_volume", "set_mixer_volume", "track", "value", int, guard=_volume_link)
    pan = NativeProperty("get_mixer_pan", "set_mixer_pan", "track", "value", int, guard=_pan_link)
    muted = NativeProperty("get_mixer_track_muted", "set_mixer_track_muted", "track", "muted", bool)
    # Disk-recording arm (the disc button). Refused by the host until the native arm symbols resolve;
    # ``fl.audio.capture`` arms and disarms through this property.
    armed = NativeProperty("get_mixer_track_armed", "set_mixer_track_armed", "track", "armed", bool)

    @property
    def effects(self) -> Effects:
        return Effects(self._ops, self.index)

    @property
    def volume_db(self) -> float:
        """Volume as modelled dB (12800 -> 0.0). Setting it writes ``mixer_volume_from_db(db)``."""
        return mixer_volume_to_db(self.volume)

    @volume_db.setter
    def volume_db(self, db: float) -> None:
        self.volume = mixer_volume_from_db(db)

    def set_volume(self, value: int | None = None, *, db: float | None = None,
                   linked: LinkMode = "warn") -> int:
        """Set the raw volume (0..16000) or a modelled dB gain (``db=``, at most +4.05); returns the raw value written.

        ``linked`` decides what happens when an automation clip channel owns this track's volume:
        ``"warn"`` (default) emits ``AutomationLinkedWarning``, ``"raise"`` raises it before writing,
        ``"ignore"`` writes without the lookup. FL keeps reapplying the clip's initial value on every
        play, so a warned write is audible only until playback restarts; ``fl.automation.release()``
        flattens the curve to the value you want.
        """
        if (value is None) == (db is None):
            raise TypeError("Pass exactly one of value= (raw 0..16000) or db=.")
        raw = mixer_volume_from_db(db) if db is not None else checked_index(-1 if value is None else value,
                                                                            maximum=MIXER_VOLUME_MAX)
        check(self._ops, AutomationTarget.mixer_volume(self.index), linked=linked)
        self._ops.set_mixer_volume(track=self.index, value=raw)
        return raw

    def set_pan(self, value: int, *, linked: LinkMode = "warn") -> int:
        """Set the SIGNED pan -6400..6400 (0 = center); returns the value written.

        ``linked`` works as in ``set_volume``: an automation clip that owns this track's pan reapplies
        its initial value on every play.
        """
        if isinstance(value, bool) or not isinstance(value, int) or not -MIXER_PAN_MAX <= value <= MIXER_PAN_MAX:
            raise ValueError("Mixer pan must be an integer within -6400..6400 (0 = center).")
        check(self._ops, AutomationTarget.mixer_pan(self.index), linked=linked)
        self._ops.set_mixer_pan(track=self.index, value=value)
        return value

    def send_to(self, destination: int, level: float | None = None, *, db: float | None = None,
                active: bool = True) -> None:
        """Route this track into ``destination`` at a send level.

        ``level`` is FL's send scale: 0.8 (``SEND_UNITY``) is unity/0 dB, the level every insert's
        default Master route reads back; 1.0 is the knob top (about +4.05 dB) and 0 keeps the route
        connected but silent. Pass ``db=`` instead to use the modelled dB law. Without either the
        level defaults to 1.0 for compatibility with earlier releases, which is hotter than unity.
        ``active=False`` disconnects the route (see ``disconnect``). Verify with ``sends()``.
        """
        if level is not None and db is not None:
            raise TypeError("Pass level= or db=, not both.")
        chosen = send_level_from_db(db) if db is not None else (1.0 if level is None else level)
        self._ops.set_mixer_send(src_track=self.index, dst_track=checked_index(destination), level=chosen,
                                 active=active)

    def disconnect(self, destination: int) -> None:
        """Disconnect the route to ``destination`` (level 0 and inactive), so ``sends()`` no longer lists it.

        Uses FL's route-active core with enable 0; FL can raise a "Disable routing?" confirmation when the
        destination is used as a plugin sidechain source, which the SDK cannot create, so unattended runs
        are expected to complete. Needs live confirmation; ``send_to(destination, 0.0)`` is the proven
        silent-but-connected alternative.
        """
        self._ops.set_mixer_send(src_track=self.index, dst_track=checked_index(destination), level=0.0,
                                 active=False)

    def sends(self) -> tuple[MixerSendInfo, ...]:
        """Active sends of this track from the native send table: destination, its name, level and
        ``level_db`` (modelled). A route set to level 0 is listed with level 0; a disconnected one is not.
        Sidechain-flagged routes cannot be told apart from plain sends."""
        return self._ops.query_mixer_sends(track=self.index)

    def send_level(self, destination: int) -> float | None:
        """Level of the active send to ``destination`` (0.8 = unity), or None when it is not connected."""
        checked_index(destination)
        for send in self.sends():
            if send.destination == destination:
                return send.level
        return None

    def set_eq_gain(self, band: int, value: int) -> None:
        self._ops.set_mixer_eq_gain(track=self.index, band=checked_index(band, maximum=2), value=value)


class Mixer:
    def __init__(self, ops: Operations) -> None:
        self._ops = ops

    def __getitem__(self, index: int) -> MixerTrack:
        return MixerTrack(self._ops, checked_index(index))

    def __len__(self) -> int:
        return len(self.list())

    def __iter__(self) -> Iterator[MixerTrack]:
        return (self[item.index] for item in self.list())

    @property
    def master(self) -> MixerTrack:
        return self[0]

    @property
    def insert_count(self) -> int:
        """Number of ordinary inserts currently in the mixer (addressable tracks are 0..insert_count).

        The default template has 16 (native count 18 = Master + 16 + Current); ``fl.mixer[17]`` on it is
        refused until ``add()`` or ``ensure_inserts(17)`` grows the mixer.
        """
        count = self._ops.get_mixer_track_count()
        if type(count) is not int or count < 3:
            raise ProtocolError("get_mixer_track_count returned an invalid native mixer count.")
        return count - 2

    @property
    def capacity(self) -> int:
        """Largest insert count FL allows (500)."""
        return MAX_INSERTS

    def ensure_inserts(self, count: int) -> int:
        """Append inserts until at least ``count`` ordinary inserts exist; returns how many were added.

        Appending never shifts existing indices, so handles stay valid. Refuses counts above ``capacity``
        before any edit. Name the new inserts afterwards (``fl.mixer[i].name = ...``).
        """
        checked_index(count, maximum=MAX_INSERTS)
        added = 0
        while self.insert_count < count:
            index = self._ops.add_mixer_track(after_track=-1)
            if type(index) is not int or index < 1:
                raise ProtocolError("add_mixer_track returned an invalid insert index; inspect mixer state before retrying.")
            added += 1
        return added

    def list(self) -> tuple[MixerTrackInfo, ...]:
        """Read Master and active ordinary inserts, excluding Current/dormant slots."""
        return self._ops.query_mixer_tracks()

    def list_text(self) -> str:
        return self._ops.list_mixer_tracks()

    def routes(self) -> tuple[MixerSendInfo, ...]:
        """Every active send in the mixer (one send-table read per track), source-major order.

        The typed way to verify bus routing: a drum insert feeding a bus reads as
        ``(source=8, destination=15, level=0.8)`` plus its Master route at whatever level it was left.
        """
        result: list[MixerSendInfo] = []
        for item in self.list():
            result.extend(self._ops.query_mixer_sends(track=item.index))
        return tuple(result)

    def add(self, name: str | None = None, *, after: int | None = None) -> MixerTrack:
        """Append an ordinary insert, or insert after an explicit track index.

        Requery indices and routing after insertion. Naming is a separate edit;
        if it fails, the added track remains. Inspect state before retrying.
        """
        if name is not None and not isinstance(name, str):
            raise TypeError("Mixer track name must be a string or None.")
        after_track = -1 if after is None else checked_index(after)
        index = self._ops.add_mixer_track(after_track=after_track)
        if type(index) is not int or index < 1:
            raise ProtocolError("add_mixer_track returned an invalid insert index; inspect mixer state before retrying.")
        track = self[index]
        if name is not None:
            track.name = name
        return track

    def find(self, name: str) -> MixerTrack:
        matches = [item.index for item in self.list() if item.name == name]
        if len(matches) != 1:
            raise LookupError(f"Expected one mixer track named {name!r}; found {len(matches)}.")
        return self[matches[0]]


__all__ = ["MAX_INSERTS", "SEND_UNITY", "EffectSlot", "Effects", "Mixer", "MixerTrack", "effect_names",
           "send_level_to_db"]
