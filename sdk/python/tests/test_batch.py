import pytest
from conftest import RecordingTransport

from fruitylink import ProtocolError, RemoteError, Studio
from fruitylink.values import JsonValue, wire_arguments


def test_partial_failure_is_visible_and_never_replayed(fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["batch"] = {"results": [
        {"operation": "set_tempo", "result": None},
        {"operation": "set_channel_volume", "error": {"code": "invalid_index", "message": "Missing channel"}}],
        "stoppedOnError": True}
    batch = fl.batch().add("set_tempo", bpm=120).add("set_channel_volume", channel=99, value=10000)
    result = batch.run()
    assert not result.succeeded and result.stopped_on_error
    with pytest.raises(RemoteError):
        result.raise_for_errors()
    with pytest.raises(RuntimeError):
        batch.run()
    assert len(transport.calls) == 1


def test_context_body_exception_does_not_submit(fl: Studio, transport: RecordingTransport) -> None:
    with pytest.raises(ValueError), fl.batch() as batch:
        batch.add("set_tempo", bpm=120)
        raise ValueError("Do not commit this batch")
    assert transport.calls == []


def test_batch_limit_and_argument_alias_collision(fl: Studio) -> None:
    batch = fl.batch()
    for _ in range(256):
        batch.add("get_tempo")
    with pytest.raises(ValueError, match="256"):
        batch.add("get_tempo")
    with pytest.raises(ValueError, match="Duplicate"):
        wire_arguments({"start_tick": 1, "startTick": 2})


def test_argument_conversion_preserves_nested_user_keys() -> None:
    assert wire_arguments({"source_pattern": 1, "metadata": {"user_key": "name"}}) == {
        "sourcePattern": 1, "metadata": {"user_key": "name"}}


@pytest.mark.parametrize("results,stopped", [([], False), ([{"operation": "wrong", "result": None}], False),
    ([{"operation": "set_tempo"}], False), ([{"operation": "set_tempo", "result": None}], True)])
def test_incomplete_or_wrong_batch_response_is_not_success(results: list[JsonValue], stopped: bool,
                                                          fl: Studio, transport: RecordingTransport) -> None:
    transport.responses["batch"] = {"results": results, "stoppedOnError": stopped}
    with pytest.raises(ProtocolError):
        fl.batch().add("set_tempo", bpm=120).run()
