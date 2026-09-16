"""Overlapped local named-pipe I/O with cancellation and server PID verification."""

import ctypes
import os
import time
from ctypes import wintypes
from types import TracebackType

from .endpoint import Endpoint
from .errors import ConnectionError


class _Overlapped(ctypes.Structure):
    _fields_ = [
        ("Internal", ctypes.c_size_t), ("InternalHigh", ctypes.c_size_t),
        ("Offset", wintypes.DWORD), ("OffsetHigh", wintypes.DWORD), ("hEvent", wintypes.HANDLE),
    ]


class WindowsPipe:
    def __init__(self, endpoint: Endpoint, timeout: float) -> None:
        if os.name != "nt":
            raise ConnectionError("Direct named-pipe connections require Windows; use an injected transport.")
        self._kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self._configure()
        self._deadline = time.monotonic() + timeout
        path = "\\\\.\\pipe\\" + endpoint.pipe_name
        self._handle: int | None = self._open(path)
        pid = wintypes.ULONG()
        valid = self._kernel.GetNamedPipeServerProcessId(self._handle, ctypes.byref(pid))
        if not valid or pid.value != (endpoint.server_pid or endpoint.pid):
            self.close()
            raise ConnectionError("Scripting pipe server process does not match the endpoint.")

    def _open(self, path: str) -> int:
        # A fresh-connection server briefly has no listener between requests, and
        # another client can win the CreateFile race after WaitNamedPipe succeeds.
        while True:
            available = self._kernel.WaitNamedPipeW(path, min(self._remaining_ms(), 100))
            if available:
                handle = self._kernel.CreateFileW(path, 0xC0000000, 0, None, 3, 0x40000000, None)
                if handle not in (None, ctypes.c_void_p(-1).value):
                    return int(handle)
            if ctypes.get_last_error() not in (2, 121, 231):
                raise ConnectionError("Cannot open the scripting pipe.")
            time.sleep(min(0.01, self._remaining_ms() / 1000))

    def _configure(self) -> None:
        kernel = self._kernel
        kernel.WaitNamedPipeW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD]
        kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                                      wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        kernel.CreateFileW.restype = wintypes.HANDLE
        kernel.GetNamedPipeServerProcessId.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.ULONG)]
        kernel.CreateEventW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR]
        kernel.CreateEventW.restype = wintypes.HANDLE
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        for name in ("ReadFile", "WriteFile"):
            getattr(kernel, name).argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD,
                                             ctypes.POINTER(wintypes.DWORD), ctypes.POINTER(_Overlapped)]
        kernel.GetOverlappedResult.argtypes = [wintypes.HANDLE, ctypes.POINTER(_Overlapped),
                                             ctypes.POINTER(wintypes.DWORD), wintypes.BOOL]
        kernel.CancelIoEx.argtypes = [wintypes.HANDLE, ctypes.POINTER(_Overlapped)]

    def _remaining_ms(self) -> int:
        remaining = self._deadline - time.monotonic()
        if remaining <= 0:
            raise ConnectionError("Scripting request timed out; completed edits may remain.")
        return max(1, min(int(remaining * 1000), 0xFFFFFFFE))

    def _io(self, buffer: ctypes.Array[ctypes.c_char], count: int, *, writing: bool) -> int:
        event = self._kernel.CreateEventW(None, True, False, None)
        if not event:
            raise ConnectionError("Cannot allocate a scripting I/O event.")
        pending = _Overlapped(hEvent=event)
        transferred = wintypes.DWORD()
        try:
            action = self._kernel.WriteFile if writing else self._kernel.ReadFile
            success = action(self._handle, buffer, count, ctypes.byref(transferred), ctypes.byref(pending))
            if not success and ctypes.get_last_error() != 997:
                raise ConnectionError("Scripting pipe I/O failed or the endpoint disconnected.")
            if not success:
                self._wait(pending)
            if not self._kernel.GetOverlappedResult(self._handle, ctypes.byref(pending), ctypes.byref(transferred), False):
                raise ConnectionError("Scripting pipe did not complete the request.")
            return int(transferred.value)
        finally:
            self._kernel.CloseHandle(event)

    def _wait(self, pending: _Overlapped) -> None:
        try:
            if self._kernel.WaitForSingleObject(pending.hEvent, self._remaining_ms()) == 0:
                return
        except BaseException:
            self._cancel(pending)
            raise
        self._cancel(pending)
        raise ConnectionError("Scripting request timed out; completed edits may remain.")

    def _cancel(self, pending: _Overlapped) -> None:
        self._kernel.CancelIoEx(self._handle, ctypes.byref(pending))
        transferred = wintypes.DWORD()
        # The buffer/OVERLAPPED must remain alive until cancellation has completed.
        self._kernel.GetOverlappedResult(self._handle, ctypes.byref(pending), ctypes.byref(transferred), True)

    def read(self, count: int) -> bytes:
        buffer = ctypes.create_string_buffer(count)
        received = self._io(buffer, count, writing=False)
        return buffer.raw[:received]

    def write(self, data: bytes) -> None:
        offset = 0
        while offset < len(data):
            buffer = ctypes.create_string_buffer(data[offset:])
            written = self._io(buffer, len(data) - offset, writing=True)
            if written == 0:
                raise ConnectionError("Scripting pipe stopped accepting request bytes.")
            offset += written

    def close(self) -> None:
        if self._handle is not None:
            self._kernel.CloseHandle(self._handle)
            self._handle = None

    def __enter__(self) -> "WindowsPipe":
        return self

    def __exit__(self, exc_type: type[BaseException] | None, exc: BaseException | None,
                 traceback: TracebackType | None) -> None:
        self.close()
