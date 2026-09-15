"""Typed FL Studio automation through the FruityLink scripting plugin."""

from .audition import RangeIsolation, isolate_bars, isolate_range
from .automation_records import (
    TENSION_EASE_IN,
    TENSION_EASE_OUT,
    AutomationChannelInfo,
    AutomationClipResult,
    AutomationPointSpec,
    AutomationTarget,
    PumpResult,
)
from .capture import (
    CaptureDecision,
    CaptureError,
    CapturePlan,
    CapturePolicy,
    CaptureResult,
    RenderRequired,
    SectionMeasurement,
    envelope,
    measure_wav,
    plan_capture,
)
from .channels import ChannelControl
from .endpoint import Endpoint, discover
from .errors import ConnectionError, FruityLinkError, ProtocolError, RemoteError
from .levels import (
    SEND_UNITY,
    channel_volume_from_db,
    channel_volume_to_db,
    mixer_volume_from_db,
    mixer_volume_to_db,
    send_level_from_db,
    send_level_to_db,
)
from .models import Gap, MixerSendInfo
from .operations import Operations
from .project import Marker, SeekResult
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
    "SEND_UNITY", "TENSION_EASE_IN", "TENSION_EASE_OUT",
    "AutomationChannelInfo", "AutomationClipResult", "AutomationPointSpec", "AutomationTarget", "Beats",
    "CaptureDecision", "CaptureError", "CapturePlan", "CapturePolicy", "CaptureResult", "ChannelControl",
    "ChannelIndex", "ClipIndex", "ClipMove", "ClipResize", "ConnectionError", "Endpoint", "FruityLinkError", "Gap",
    "Marker", "MixerSendInfo", "MixerTrackIndex", "NamedPipeTransport", "NoteEdit", "NoteRef", "NoteSpec",
    "Operations", "PatternClipSpec", "PatternIndex", "PlaylistTrackIndex", "ProtocolError", "PumpResult",
    "RangeIsolation", "RemoteError", "RenderRequired", "RequestTransport", "SectionMeasurement", "SeekResult",
    "Studio", "StudioSession", "Ticks", "Timebase",
    "channel_volume_from_db", "channel_volume_to_db", "connect", "discover", "envelope", "isolate_bars",
    "isolate_range", "launch", "measure_wav", "mixer_volume_from_db", "mixer_volume_to_db", "plan_capture",
    "send_level_from_db", "send_level_to_db",
]
