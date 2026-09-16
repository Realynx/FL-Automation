"""Optional typed queries advertised by current scripting backends."""

from .models import (
    ArrangementInfo,
    AutomationPointInfo,
    ChannelInfo,
    ClipInfo,
    MixerSendInfo,
    MixerTrackInfo,
    NoteInfo,
    Page,
    PatternInfo,
    PlaylistTrackInfo,
    PluginParameterInfo,
    ProjectInfo,
    decode_page,
    decode_record,
    decode_records,
)
from .transport import RequestTransport
from .values import JsonValue, wire_arguments


class QueryOperations:
    _transport: RequestTransport

    def _query(self, operation: str, **arguments: object) -> JsonValue:
        return self._transport.request("invoke", {"operation": operation, "arguments": wire_arguments(arguments)})

    def query_channels(self) -> tuple[ChannelInfo, ...]:
        return decode_records(ChannelInfo, self._query("query_channels"))

    def query_patterns(self) -> tuple[PatternInfo, ...]:
        return decode_records(PatternInfo, self._query("query_patterns"))

    def query_notes(self, *, pattern: int, channel: int = -1, offset: int = 0,
                    limit: int = 512) -> Page[NoteInfo]:
        return decode_page(NoteInfo, self._query("query_notes", pattern=pattern, channel=channel,
                                               offset=offset, limit=limit))

    def query_playlist_tracks(self) -> tuple[PlaylistTrackInfo, ...]:
        return decode_records(PlaylistTrackInfo, self._query("query_playlist_tracks"))

    def query_clips(self, *, track: int = -1, offset: int = 0, limit: int = 512) -> Page[ClipInfo]:
        return decode_page(ClipInfo, self._query("query_clips", track=track, offset=offset, limit=limit))

    def query_arrangements(self) -> tuple[ArrangementInfo, ...]:
        return decode_records(ArrangementInfo, self._query("query_arrangements"))

    def query_mixer_tracks(self) -> tuple[MixerTrackInfo, ...]:
        """Master and active ordinary inserts; excludes Current and dormant slots."""
        return decode_records(MixerTrackInfo, self._query("query_mixer_tracks"))

    def query_mixer_sends(self, *, track: int) -> tuple[MixerSendInfo, ...]:
        """Active sends of one track from the native send table (destination, name, level with 0.8 = unity,
        active). Disconnected destinations are omitted; a level-0 route is listed with level 0."""
        return decode_records(MixerSendInfo, self._query("query_mixer_sends", track=track))

    def query_plugin_parameters(self, *, channel_or_track: int, slot: int = -1, filter: str | None = None,
                                offset: int = 0, limit: int = 512) -> Page[PluginParameterInfo]:
        return decode_page(PluginParameterInfo, self._query("query_plugin_parameters", channel_or_track=channel_or_track,
                                                          slot=slot, filter=filter, offset=offset, limit=limit))

    def query_project(self) -> ProjectInfo:
        return decode_record(ProjectInfo, self._query("query_project"))

    def query_automation_points(self, *, channel: int) -> tuple[AutomationPointInfo, ...]:
        return decode_records(AutomationPointInfo, self._query("query_automation_points", channel=channel))
