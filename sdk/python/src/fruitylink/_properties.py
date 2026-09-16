"""A shared descriptor for native scalar properties on indexed DAW objects."""

from typing import Generic, TypeVar, overload

from .errors import ProtocolError
from .operations import Operations

T = TypeVar("T", int, bool, str)


class IndexedObject:
    def __init__(self, ops: Operations, index: int) -> None:
        self._ops = ops
        self.index = index

    def __repr__(self) -> str:
        return f"{type(self).__name__}(index={self.index})"


class NativeProperty(Generic[T]):
    def __init__(self, getter: str, setter: str, index_arg: str, value_arg: str, value_type: type[T]) -> None:
        self._getter = getter
        self._setter = setter
        self._index_arg = index_arg
        self._value_arg = value_arg
        self._type: type[T] = value_type

    @overload
    def __get__(self, instance: None, owner: type[IndexedObject]) -> "NativeProperty[T]": ...

    @overload
    def __get__(self, instance: IndexedObject, owner: type[IndexedObject]) -> T: ...

    def __get__(self, instance: IndexedObject | None, owner: type[IndexedObject]) -> "T | NativeProperty[T]":
        if instance is None:
            return self
        result = instance._ops.invoke(self._getter, **{self._index_arg: instance.index})
        if not isinstance(result, self._type) or (self._type is int and isinstance(result, bool)):
            raise ProtocolError(f"{self._getter} returned an unexpected value type.")
        return result

    def __set__(self, instance: IndexedObject, value: T) -> None:
        instance._ops.invoke(self._setter, **{self._index_arg: instance.index, self._value_arg: value})
