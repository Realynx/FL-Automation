"""Mixer tracks and effect slots. Layout compatibility is decided by the native profile."""

import base64
from collections.abc import Iterator

from ._collection import checked_index
from ._properties import IndexedObject, NativeProperty
from .errors import ProtocolError
from .models import MixerTrackInfo
from .operations import Operations
from .plugins import Parameters


class EffectSlot:
    def __init__(self, ops: Operations, track: int, slot: int) -> None:
        self._ops = ops
        self.track = checked_index(track)
        self.index = checked_index(slot, maximum=9)

    @property
    def parameters(self) -> Parameters:
        return Parameters(self._ops, self.track, self.index)

    def load(self, plugin: str) -> None:
        self._ops.add_mixer_effect(track=self.track, slot=self.index, plugin_name=plugin)

    def clear(self) -> None:
        self._ops.remove_mixer_effect(track=self.track, slot=self.index)

    def clone_type_to(self, slot: int) -> None:
        """Copy plugin type to a slot on this track; parameter state is not copied."""
        self._ops.clone_mixer_effect(track=self.track, from_slot=self.index, to_slot=checked_index(slot, maximum=9))

    def load_state(self, path: str) -> str:
        """Load a plugin preset/state file into the effect already loaded in this slot. Returns the host's
        verification line; the slot must already hold the matching plugin."""
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

    def list_text(self) -> str:
        return self._ops.list_mixer_effects(track=self.track)


class MixerTrack(IndexedObject):
    """One mixer track. ``pan`` is a SIGNED native integer -6400..6400 with 0 = center and
    negative = left; this differs from channel pan (0..12800, 6400 = center). ``volume`` is
    the raw native 0..12800 scale with no defined dB conversion."""

    name = NativeProperty("get_mixer_track_name", "set_mixer_track_name", "track", "name", str)
    volume = NativeProperty("get_mixer_volume", "set_mixer_volume", "track", "value", int)
    pan = NativeProperty("get_mixer_pan", "set_mixer_pan", "track", "value", int)
    muted = NativeProperty("get_mixer_track_muted", "set_mixer_track_muted", "track", "muted", bool)

    @property
    def effects(self) -> Effects:
        return Effects(self._ops, self.index)

    def send_to(self, destination: int, level: float = 1.0) -> None:
        self._ops.set_mixer_send(src_track=self.index, dst_track=checked_index(destination), level=level)

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

    def list(self) -> tuple[MixerTrackInfo, ...]:
        """Read Master and active ordinary inserts, excluding Current/dormant slots."""
        return self._ops.query_mixer_tracks()

    def list_text(self) -> str:
        return self._ops.list_mixer_tracks()

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
