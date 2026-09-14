"""Typed FL Studio automation through the FruityLink scripting plugin."""

from .audition import RangeIsolation, isolate_bars, isolate_range
from .automation_records import AutomationClipResult, AutomationPointSpec, AutomationTarget, PumpResult
from .endpoint import Endpoint, discover
from .errors import ConnectionError, FruityLinkError, ProtocolError, RemoteError
from .models import Gap
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
from .sessions import StudioSession, launch
from .studio import Studio, connect
from .transport import NamedPipeTransport, RequestTransport

__all__ = [
    "AutomationClipResult", "AutomationPointSpec", "AutomationTarget",
    "Beats", "ChannelIndex", "ClipIndex", "ClipMove", "ClipResize", "ConnectionError", "Endpoint",
    "FruityLinkError", "Gap", "MixerTrackIndex", "NamedPipeTransport", "NoteEdit", "NoteRef", "NoteSpec",
    "Operations", "PatternClipSpec", "PatternIndex", "PlaylistTrackIndex", "ProtocolError", "PumpResult",
    "RangeIsolation", "RemoteError", "RequestTransport", "Studio", "StudioSession", "Ticks", "Timebase", "connect",
    "discover", "isolate_bars", "isolate_range", "launch",
]
