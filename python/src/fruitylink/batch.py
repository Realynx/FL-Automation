"""Sequential operation batches with explicit partial-success results."""

from dataclasses import dataclass
from types import TracebackType

from .errors import ProtocolError, RemoteError
from .transport import RequestTransport
from .values import JsonValue, wire_arguments


@dataclass(frozen=True)
class BatchItem:
    operation: str
    result: JsonValue
    error: RemoteError | None


@dataclass(frozen=True)
class BatchResult:
    results: tuple[BatchItem, ...]
    stopped_on_error: bool

    @property
    def succeeded(self) -> bool:
        return all(item.error is None for item in self.results)

    def raise_for_errors(self) -> None:
        for item in self.results:
            if item.error is not None:
                raise item.error


def _decode_item(value: JsonValue) -> BatchItem:
    if not isinstance(value, dict) or not isinstance(value.get("operation"), str):
        raise ProtocolError("Malformed batch result item.")
    error = value.get("error")
    failure = None
    if error is not None:
        if not isinstance(error, dict) or not isinstance(error.get("message"), str):
            raise ProtocolError("Malformed batch error item.")
        failure = RemoteError(str(error.get("code", "operation_failed")), str(error["message"]), error.get("data"))
    elif "result" not in value:
        raise ProtocolError("Batch success item is missing its result.")
    return BatchItem(str(value["operation"]), value.get("result"), failure)


class Batch:
    """Queue operations and run once. Context exit runs only when its body succeeds.

    A failed item never rolls back earlier operations. Inspect ``result`` afterwards.
    Native bulk note/clip methods are preferable when editing many objects of one kind.
    """

    def __init__(self, transport: RequestTransport, *, stop_on_error: bool = True) -> None:
        self._transport = transport
        self._stop_on_error = stop_on_error
        self._operations: list[JsonValue] = []
        self._executed = False
        self.result: BatchResult | None = None

    def add(self, operation: str, **arguments: object) -> "Batch":
        if self._executed:
            raise RuntimeError("This batch has already executed.")
        if len(self._operations) >= 256:
            raise ValueError("A batch can contain at most 256 operations.")
        self._operations.append({"operation": operation, "arguments": wire_arguments(arguments)})
        return self

    def run(self) -> BatchResult:
        if self._executed:
            raise RuntimeError("A batch cannot be replayed; some operations may already have completed.")
        self._executed = True
        value = self._transport.request("batch", {"operations": self._operations, "stopOnError": self._stop_on_error})
        if not isinstance(value, dict) or not isinstance(value.get("results"), list):
            raise ProtocolError("Malformed batch response.")
        items = value["results"]
        if not isinstance(items, list) or type(value.get("stoppedOnError")) is not bool:
            raise ProtocolError("Malformed batch completion state.")
        result = BatchResult(tuple(_decode_item(item) for item in items), bool(value["stoppedOnError"]))
        self._validate_result(result)
        self.result = result
        return self.result

    def _validate_result(self, result: BatchResult) -> None:
        expected = [str(item["operation"]) for item in self._operations if isinstance(item, dict)]
        names = [item.operation for item in result.results]
        if names != expected[:len(names)] or len(names) > len(expected):
            raise ProtocolError("Batch response operations do not match the submitted order.")
        if not result.stopped_on_error and len(names) != len(expected):
            raise ProtocolError("Batch response omitted operations without reporting a stop.")
        if result.stopped_on_error and (not self._stop_on_error or not result.results or result.results[-1].error is None):
            raise ProtocolError("Batch response reported an inconsistent error stop.")

    def __enter__(self) -> "Batch":
        return self

    def __exit__(self, exc_type: type[BaseException] | None, exc: BaseException | None,
                 traceback: TracebackType | None) -> None:
        if exc_type is None and not self._executed:
            self.run().raise_for_errors()
