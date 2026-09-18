"""Typed FL Studio automation through the FruityLink scripting plugin."""

from .audition import RangeIsolation, isolate_bars, isolate_range
from .automation_links import AutomationLinkedWarning
from .automation_records import (
    TENSION_EASE_IN,
    TENSION_EASE_OUT,
    AutomationChannelInfo,
    AutomationClipResult,
    AutomationPointSpec,
    AutomationTarget,
    PumpResult,
    ReleaseResult,
)
from .capture import (
    CaptureDecision,
    CaptureError,
    CapturePlan,
    CapturePolicy,
    CaptureResult,
    RecordingFilterChange,
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
from .models import Gap, MixerSendInfo, SampleInfo
from .operations import Operations
from .playlist import (
    MAX_TILED_CLIPS,
    PatternClipLongerThanPatternWarning,
    PatternPlacement,
    PatternPlacementBatch,
)
from .project import (
    RECORDING_FILTER_ALL,
    RECORDING_FILTER_AUDIO,
    RECORDING_FILTER_AUTOMATION,
    RECORDING_FILTER_BITS,
    RECORDING_FILTER_CLIPS,
    RECORDING_FILTER_NOTES,
    Marker,
    SeekResult,
    recording_filter_flags,
    recording_filter_names,
)
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
    "MAX_TILED_CLIPS",
    "RECORDING_FILTER_ALL", "RECORDING_FILTER_AUDIO", "RECORDING_FILTER_AUTOMATION", "RECORDING_FILTER_BITS",
    "RECORDING_FILTER_CLIPS", "RECORDING_FILTER_NOTES",
    "SEND_UNITY", "TENSION_EASE_IN", "TENSION_EASE_OUT",
    "AutomationChannelInfo", "AutomationClipResult", "AutomationLinkedWarning", "AutomationPointSpec",
    "AutomationTarget", "Beats",
    "CaptureDecision", "CaptureError", "CapturePlan", "CapturePolicy", "CaptureResult", "ChannelControl",
    "ChannelIndex", "ClipIndex", "ClipMove", "ClipResize", "ConnectionError", "Endpoint", "FruityLinkError", "Gap",
    "Marker", "MixerSendInfo", "MixerTrackIndex", "NamedPipeTransport", "NoteEdit", "NoteRef", "NoteSpec",
    "Operations", "PatternClipLongerThanPatternWarning", "PatternClipSpec", "PatternIndex",
    "PatternPlacement", "PatternPlacementBatch", "PlaylistTrackIndex", "ProtocolError", "PumpResult",
    "ReleaseResult",
    "RangeIsolation", "RecordingFilterChange", "RemoteError", "RenderRequired", "RequestTransport",
    "SampleInfo", "SectionMeasurement", "SeekResult",
    "Studio", "StudioSession", "Ticks", "Timebase",
    "channel_volume_from_db", "channel_volume_to_db", "connect", "discover", "envelope", "isolate_bars",
    "isolate_range", "launch", "measure_wav", "mixer_volume_from_db", "mixer_volume_to_db", "plan_capture",
    "recording_filter_flags", "recording_filter_names", "send_level_from_db", "send_level_to_db",
]
