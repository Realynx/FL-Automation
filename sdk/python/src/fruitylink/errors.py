"""Failures preserve operation context without exposing endpoint credentials."""

from .values import JsonValue


class FruityLinkError(Exception):
    """Base error for the scripting client."""


class ConnectionError(FruityLinkError):
    """Discovery, endpoint identity, or transport failure."""


class ProtocolError(FruityLinkError):
    """The endpoint returned an invalid protocol response."""


class RemoteError(FruityLinkError):
    """An operation was rejected or failed inside the scripting plugin."""

    def __init__(self, code: str, message: str, data: JsonValue = None) -> None:
        super().__init__(f"{code}: {message}")
        self.code = code
        self.message = message
        self.data = data
