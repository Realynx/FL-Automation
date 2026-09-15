"""JSON conversion shared by operations, batches, and the isolated worker."""

import math
from collections.abc import Mapping, Sequence
from dataclasses import fields, is_dataclass
from typing import TypeAlias, TypeVar

JsonValue: TypeAlias = None | bool | int | float | str | list["JsonValue"] | dict[str, "JsonValue"]
T = TypeVar("T")


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


def json_key(key: object) -> str:
    """Render one mapping key as the JSON object key it becomes.

    JSON names are strings, but analysis and describe helpers legitimately key results by number
    (parameter index maps, ``explain_parameters`` enum tables keyed by the normalized value), and
    refusing them made otherwise correct results unreturnable from the embedded worker. Numbers and
    booleans are converted the way ``str``/``repr`` write them (12 -> ``"12"``, 0.5 -> ``"0.5"``,
    True -> ``"True"``); every other key type is still refused, because there is no round-trippable
    spelling for it.
    """
    if isinstance(key, str):
        return key
    if isinstance(key, bool):
        return str(key)
    if isinstance(key, int):
        return str(int(key))
    if isinstance(key, float):
        if not math.isfinite(key):
            raise ValueError("JSON numbers must be finite.")
        return repr(key)
    raise TypeError(f"JSON object keys must be strings, numbers or booleans, not {type(key).__name__}.")


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
        converted: dict[str, JsonValue] = {}
        for key, item in value.items():
            name = json_key(key)
            if name in converted:
                raise ValueError(f"Duplicate JSON object key {name!r}: two keys of this mapping spell the same name.")
            converted[name] = to_json(item, depth=depth + 1)
        return converted
    if isinstance(value, Sequence) and not isinstance(value, (bytes, bytearray)):
        return [to_json(item, depth=depth + 1) for item in value]
    raise TypeError(f"Cannot serialize {type(value).__name__} as JSON.")


def resolve_alias(canonical: str, value: T | None, **aliases: T | None) -> T:
    """Return the one keyword that was given among the canonical name and its aliases.

    Generated setters accept ``volume=`` next to ``value=`` (and ``pan=``); passing none or more than
    one of them is a TypeError naming the canonical keyword, so a wrong guess costs one local error,
    not a remote round trip.
    """
    given: dict[str, T] = {name: item for name, item in {canonical: value, **aliases}.items() if item is not None}
    if len(given) != 1:
        names = ", ".join(f"{name}=" for name in (canonical, *aliases))
        raise TypeError(f"Pass exactly one of {names} (canonical: {canonical}=).")
    return next(iter(given.values()))


def json_object(value: JsonValue, context: str) -> dict[str, JsonValue]:
    if not isinstance(value, dict):
        raise ValueError(f"{context} must be a JSON object.")
    return value
