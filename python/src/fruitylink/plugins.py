"""Plugin catalogue, loaded-plugin parameter enumeration and parameter writes."""

from collections.abc import Iterator

from ._collection import checked_index, iter_pages
from .models import Page, PluginParameterInfo
from .operations import Operations


class Parameters:
    def __init__(self, ops: Operations, channel_or_track: int, slot: int = -1) -> None:
        self._ops = ops
        self.channel_or_track = checked_index(channel_or_track)
        self.slot = checked_index(slot, minimum=-1, maximum=9)

    def list(self, filter: str | None = None) -> tuple[PluginParameterInfo, ...]:
        """Read every matching parameter. For bounded script results, use page()."""
        return tuple(self.iter(filter, page_size=512))

    def page(self, filter: str | None = None, *, offset: int = 0,
             limit: int = 64) -> Page[PluginParameterInfo]:
        """Read one raw-slot page; an empty filtered page may have next_offset.

        The filter is a case-insensitive name substring. Limit (1..512) bounds
        scanned slots, not matching items or serialized bytes. Continue with the
        returned next_offset, retaining the same filter and limit.
        """
        return self._ops.query_plugin_parameters(
            channel_or_track=self.channel_or_track, slot=self.slot, filter=filter,
            offset=checked_index(offset), limit=checked_index(limit, minimum=1, maximum=512))

    def iter(self, filter: str | None = None, *, page_size: int = 64) -> Iterator[PluginParameterInfo]:
        """Fetch pages lazily. Stopping iteration prevents further remote reads."""
        checked_index(page_size, minimum=1, maximum=512)
        return iter_pages(lambda offset: self.page(filter, offset=offset, limit=page_size))

    def __iter__(self) -> Iterator[PluginParameterInfo]:
        return self.iter()

    def list_text(self, filter: str | None = None) -> str:
        return self._ops.list_plugin_params(channel_or_track=self.channel_or_track, slot=self.slot, filter=filter)

    def set(self, index: int, value: float) -> None:
        self._ops.set_plugin_param(channel_or_track=self.channel_or_track, slot=self.slot,
                                   param_index=checked_index(index), value=value)

    def find(self, name: str) -> PluginParameterInfo:
        """Find one exact, case-sensitive name; reject duplicate names.

        Native filtering avoids reading values for unrelated parameters. A unique
        match still requires checking later pages; ambiguity stops at two matches.
        """
        match = None
        for item in self.iter(name, page_size=512):
            if item.name != name:
                continue
            if match is not None:
                raise LookupError(f"Expected one parameter named {name!r}; found at least 2.")
            match = item
        if match is None:
            raise LookupError(f"Expected one parameter named {name!r}; found 0.")
        return match


class Plugins:
    def __init__(self, ops: Operations) -> None:
        self._ops = ops

    def available_text(self, *, effects: bool = False) -> str:
        return self._ops.list_available_plugins(effects=effects)

    def samples_text(self, filter: str | None = None) -> str:
        return self._ops.list_samples(filter=filter)

    def channel_parameters(self, channel: int) -> Parameters:
        return Parameters(self._ops, channel)

    def effect_parameters(self, track: int, slot: int) -> Parameters:
        return Parameters(self._ops, track, checked_index(slot, maximum=9))
