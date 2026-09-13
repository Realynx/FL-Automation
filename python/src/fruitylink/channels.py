"""Zero-based channel rack objects. References are indices, not persistent IDs."""

from collections.abc import Iterator

from ._collection import checked_index
from ._properties import IndexedObject, NativeProperty
from .models import ChannelInfo
from .operations import Operations
from .plugins import Parameters


class Channel(IndexedObject):
    name = NativeProperty("get_channel_name", "set_channel_name", "index", "name", str)
    volume = NativeProperty("get_channel_volume", "set_channel_volume", "channel", "value", int)
    pan = NativeProperty("get_channel_pan", "set_channel_pan", "channel", "value", int)
    pitch = NativeProperty("get_channel_pitch", "set_channel_pitch", "channel", "cents", int)
    muted = NativeProperty("get_channel_muted", "set_channel_muted", "channel", "muted", bool)
    mixer_track = NativeProperty("get_channel_fx_route", "set_channel_fx_route", "channel", "mixerTrack", int)

    @property
    def parameters(self) -> Parameters:
        return Parameters(self._ops, self.index)

    def select(self) -> None:
        self._ops.select_channel(index=self.index)

    def toggle_solo(self) -> None:
        self._ops.set_channel_solo(index=self.index)

    def plugin_text(self) -> str:
        return self._ops.get_channel_plugin(channel=self.index)

    def replace_sample(self, path: str) -> None:
        self._ops.replace_channel_sample(channel=self.index, sample_path=path)


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
        channel = self[self._ops.add_channel(plugin_name=plugin)]
        if name is not None:
            channel.name = name
        return channel

    def add_sample(self, path: str, *, name: str | None = None) -> Channel:
        channel = self[self._ops.add_sample_channel(sample_path=path)]
        if name is not None:
            channel.name = name
        return channel

    def find(self, name: str) -> Channel:
        matches = [item.index for item in self.list() if item.name == name]
        if len(matches) != 1:
            raise LookupError(f"Expected one channel named {name!r}; found {len(matches)}.")
        return self[matches[0]]
