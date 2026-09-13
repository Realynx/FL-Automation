"""Typed FL Studio automation through the FruityLink scripting plugin."""

from .automation_records import AutomationClipResult, AutomationPointSpec, AutomationTarget
from .endpoint import Endpoint, discover
from .errors import ConnectionError, FruityLinkError, ProtocolError, RemoteError
from .operations import Operations
from .records import (
    Beats,
    ChannelIndex,
    ClipIndex,
    ClipMove,
    ClipResize,
    MixerTrackIndex,
    NoteEdit,
    NoteRef,
    NoteSpec,
    PatternClipSpec,
    PatternIndex,
    PlaylistTrackIndex,
    Ticks,
    Timebase,
)
from .studio import Studio, connect
from .transport import NamedPipeTransport, RequestTransport

__all__ = [
    "AutomationClipResult", "AutomationPointSpec", "AutomationTarget",
    "Beats", "ChannelIndex", "ClipIndex", "ClipMove", "ClipResize", "ConnectionError", "Endpoint",
    "FruityLinkError", "MixerTrackIndex", "NamedPipeTransport", "NoteEdit", "NoteRef", "NoteSpec",
    "Operations", "PatternClipSpec", "PatternIndex", "PlaylistTrackIndex", "ProtocolError", "RemoteError",
    "RequestTransport", "Studio", "Ticks", "Timebase", "connect", "discover",
]
