"""JSON conversion shared by operations, batches, and the isolated worker."""

import math
from collections.abc import Mapping, Sequence
from dataclasses import fields, is_dataclass
from typing import TypeAlias

JsonValue: TypeAlias = None | bool | int | float | str | list["JsonValue"] | dict[str, "JsonValue"]


def camel_case(name: str) -> str:
    head, *tail = name.split("_")
    return head + "".join(part.capitalize() for part in tail)


def wire_arguments(arguments: Mapping[str, object]) -> dict[str, JsonValue]:
    """Normalize top-level argument names only; reject snake/camel duplicate aliases."""
    result: dict[str, JsonValue] = {}
    for key, value in arguments.items():
        wire_key = camel_case(key)
        if wire_key in result:
            raise ValueError(f"Duplicate argument aliases for {wire_key}.")
        result[wire_key] = to_json(value)
    return result


def to_json(value: object, *, depth: int = 0) -> JsonValue:
    """Convert SDK records and ordinary JSON values; reject opaque objects and NaN."""
    if depth > 32:
        raise ValueError("JSON value exceeds maximum nesting depth (32).")
    if value is None or isinstance(value, (str, bool, int)):
        return value
    if isinstance(value, float):
        if not math.isfinite(value):
            raise ValueError("JSON numbers must be finite.")
        return value
    if is_dataclass(value) and not isinstance(value, type):
        return {camel_case(f.name): to_json(getattr(value, f.name), depth=depth + 1) for f in fields(value)}
    if isinstance(value, Mapping):
        if any(not isinstance(key, str) for key in value):
            raise TypeError("JSON object keys must be strings.")
        return {str(key): to_json(item, depth=depth + 1) for key, item in value.items()}
    if isinstance(value, Sequence) and not isinstance(value, (bytes, bytearray)):
        return [to_json(item, depth=depth + 1) for item in value]
    raise TypeError(f"Cannot serialize {type(value).__name__} as JSON.")


def json_object(value: JsonValue, context: str) -> dict[str, JsonValue]:
    if not isinstance(value, dict):
        raise ValueError(f"{context} must be a JSON object.")
    return value
