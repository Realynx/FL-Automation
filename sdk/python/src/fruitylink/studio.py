"""The composition root: DAW objects share one version-independent request transport."""

from types import TracebackType

from .analysis import Analysis
from .arrangements import Arrangements
from .automation import Automation
from .batch import Batch
from .capture import Audio
from .channels import Channels, _sample_paths
from .endpoint import Endpoint, discover
from .errors import ConnectionError
from .mixer import Mixer
from .operations import Operations
from .patterns import Patterns
from .playlist import Playlist
from .plugins import Plugins
from .project import Project, Transport
from .records import Timebase
from .samples import Samples
from .transport import NamedPipeTransport, RequestTransport
from .values import JsonValue


class Studio:
    """A DAW connection. Native calls execute synchronously and may mutate the active project.

    Inject a RequestTransport for a relay or tests. ``connect`` performs the native endpoint
    handshake. A Studio created directly trusts its supplied transport's identity policy.
    """

    def __init__(self, transport: RequestTransport) -> None:
        self._requests = transport
        self.ops = Operations(transport)
        self.project = Project(self.ops)
        self.transport = Transport(self.ops)
        self.channels = Channels(self.ops)
        self.patterns = Patterns(self.ops)
        self.playlist = Playlist(self.ops)
        self.clips = self.playlist.clips
        self.arrangements = Arrangements(self.ops)
        self.mixer = Mixer(self.ops)
        self.plugins = Plugins(self.ops)
        self.automation = Automation(self.ops)
        self.analysis = Analysis()
        self.samples = Samples(self.ops, _sample_paths(self.ops))
        self.audio = Audio(self)

    @property
    def timebase(self) -> Timebase:
        return Timebase(self.ops.get_ppq())

    @property
    def status(self) -> str:
        return self.ops.get_status()

    def catalog(self) -> JsonValue:
        return self._requests.request("catalog", {})

    def capabilities(self) -> JsonValue:
        return self._requests.request("capabilities", {})

    def batch(self, *, stop_on_error: bool = True) -> Batch:
        return Batch(self._requests, stop_on_error=stop_on_error)

    def close(self) -> None:
        """No persistent pipe is held; this never closes FL Studio or an injected relay."""

    def __enter__(self) -> "Studio":
        return self

    def __exit__(self, exc_type: type[BaseException] | None, exc: BaseException | None,
                 traceback: TracebackType | None) -> None:
        self.close()


def verify_identity(endpoint: Endpoint, capabilities: JsonValue) -> None:
    if not isinstance(capabilities, dict):
        raise ConnectionError("The scripting endpoint did not provide capabilities.")
    expected: dict[str, JsonValue] = {"apiVersion": endpoint.api_version, "pid": endpoint.pid,
                                     "instanceId": endpoint.instance_id}
    if any(capabilities.get(key) != value for key, value in expected.items()):
        raise ConnectionError("The scripting instance changed or the discovery record is stale.")


def connect(pid: int | None = None, *, endpoint: Endpoint | None = None,
            transport: RequestTransport | None = None, timeout: float = 30.0) -> Studio:
    """Connect to exactly one instance, an explicit endpoint, or a caller-managed transport."""
    if transport is not None:
        if endpoint is not None or pid is not None:
            raise ValueError("A caller-managed transport cannot be combined with endpoint discovery.")
        return Studio(transport)
    if endpoint is not None and pid is not None and endpoint.pid != pid:
        raise ValueError("Requested pid does not match the supplied endpoint.")
    target = endpoint if endpoint is not None else discover(pid)
    studio = Studio(NamedPipeTransport(target, timeout=timeout))
    verify_identity(target, studio.capabilities())
    return studio
