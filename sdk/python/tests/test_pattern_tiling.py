"""Pattern clips do not loop: tiling, last-clip shortening and the over-long-clip warning.

Live evidence this mirrors (FL 26.1.3.5570, 2026-09-18): a 1-bar pattern placed with
``add_patterns([PatternClipSpec(p, 1, 0, 4 * BAR)], enforce_lengths=True)`` produced the clip
``(0, 1536)``; a master capture had audio in bar 1 only and bars 2-4 measured silent.

Song facts: PPQ 96, 4/4, so a bar is 384 ticks.
"""

import warnings

import pytest
from conftest import RecordingTransport

from fruitylink import (
    MAX_TILED_CLIPS,
    PatternClipLongerThanPatternWarning,
    PatternClipSpec,
    Studio,
)
from fruitylink.playlist import tile_specs
from fruitylink.values import JsonValue

PPQ = 96
BAR = 4 * PPQ


def operations(transport: RecordingTransport) -> list[str]:
    return [str(call[1]["operation"]) for call in transport.calls]


def arguments(transport: RecordingTransport, position: int) -> dict[str, JsonValue]:
    value = transport.calls[position][1]["arguments"]
    assert isinstance(value, dict)
    return value


def pattern_info(index: int, length: int | None) -> dict[str, JsonValue]:
    return {"index": index, "name": f"Pattern {index}", "lengthTick": length, "noteCount": None, "current": False}


def install_patterns(transport: RecordingTransport, *patterns: tuple[int, int | None]) -> None:
    transport.responses["query_patterns"] = [pattern_info(index, length) for index, length in patterns]


def placed(transport: RecordingTransport, position: int) -> list[JsonValue]:
    value = arguments(transport, position)["clips"]
    assert isinstance(value, list)
    return value


# ---- tile_pattern -------------------------------------------------------------------------------

def test_tile_pattern_places_one_clip_per_repetition(fl: Studio, transport: RecordingTransport) -> None:
    install_patterns(transport, (7, BAR))

    result = fl.playlist.tile_pattern(7, track=3, start_tick=0, length_tick=4 * BAR)

    assert result == 4, "the live case: a 1-bar pattern across 4 bars is four clips, not one 4-bar clip"
    assert result.pattern_length_tick == BAR and result.repeated and result.notice == ""
    assert operations(transport) == ["query_patterns", "add_pattern_clips"]
    assert placed(transport, 1) == [
        {"pattern": 7, "track": 3, "startTick": bar * BAR, "lengthTick": BAR} for bar in range(4)]


def test_tile_pattern_shortens_the_last_clip_to_end_exactly_at_the_span(fl: Studio,
                                                                       transport: RecordingTransport) -> None:
    install_patterns(transport, (7, 2 * BAR))

    result = fl.playlist.tile_pattern(7, track=3, start_tick=8 * BAR, length_tick=5 * BAR)

    assert result == 3
    assert placed(transport, 1) == [
        {"pattern": 7, "track": 3, "startTick": 8 * BAR, "lengthTick": 2 * BAR},
        {"pattern": 7, "track": 3, "startTick": 10 * BAR, "lengthTick": 2 * BAR},
        {"pattern": 7, "track": 3, "startTick": 12 * BAR, "lengthTick": BAR}]


def test_tile_pattern_beats_converts_at_the_project_ppq(fl: Studio, transport: RecordingTransport) -> None:
    install_patterns(transport, (7, BAR))

    assert fl.playlist.tile_pattern_beats(7, track=1, start_beats=4.0, length_beats=12.0) == 3
    assert operations(transport) == ["get_ppq", "query_patterns", "add_pattern_clips"]
    assert placed(transport, 2) == [
        {"pattern": 7, "track": 1, "startTick": BAR + bar * BAR, "lengthTick": BAR} for bar in range(3)]


def test_tile_pattern_refuses_a_pattern_the_host_cannot_measure(fl: Studio, transport: RecordingTransport) -> None:
    install_patterns(transport, (7, None))

    with pytest.raises(LookupError):
        fl.playlist.tile_pattern(7, track=1, start_tick=0, length_tick=4 * BAR)
    assert "add_pattern_clips" not in operations(transport)


def test_tile_pattern_refuses_bad_ranges_and_runaway_counts_before_any_write(fl: Studio,
                                                                            transport: RecordingTransport) -> None:
    install_patterns(transport, (7, BAR))
    for call in (lambda: fl.playlist.tile_pattern(7, track=1, start_tick=-1, length_tick=BAR),
                 lambda: fl.playlist.tile_pattern(7, track=1, start_tick=0, length_tick=0),
                 lambda: fl.playlist.tile_pattern(7, track=1, start_tick=0, length_tick=(MAX_TILED_CLIPS + 1) * BAR)):
        with pytest.raises(ValueError):
            call()
    assert "add_pattern_clips" not in operations(transport)


def test_tile_specs_is_pure_and_needs_no_connection() -> None:
    specs = tile_specs(2, track=5, start_tick=0, length_tick=3 * BAR, pattern_length_tick=2 * BAR)
    assert specs == [PatternClipSpec(2, 5, 0, 2 * BAR), PatternClipSpec(2, 5, 2 * BAR, BAR)]
    with pytest.raises(ValueError):
        tile_specs(2, track=5, start_tick=0, length_tick=BAR, pattern_length_tick=0)


# ---- the warning on the non-looping path --------------------------------------------------------

def test_add_pattern_ticks_warns_that_the_extra_span_is_silent(fl: Studio, transport: RecordingTransport) -> None:
    install_patterns(transport, (12, BAR))

    with pytest.warns(PatternClipLongerThanPatternWarning) as caught:
        result = fl.playlist.add_pattern_ticks(12, track=2, start=0, length=4 * BAR)

    message = str(caught[0].message)
    assert "Pattern 12" in message and str(BAR) in message and str(4 * BAR) in message
    assert "SILENT" in message and "tile_pattern" in message
    assert result == 1 and result.notice == message and not result.repeated
    assert result.pattern_length_tick == BAR and result.length_tick == 4 * BAR
    assert operations(transport) == ["query_patterns", "add_pattern_clip"]


def test_add_pattern_ticks_is_quiet_for_a_clip_that_fits_and_for_a_following_clip(
        fl: Studio, transport: RecordingTransport) -> None:
    install_patterns(transport, (12, 4 * BAR))
    with warnings.catch_warnings():
        warnings.simplefilter("error")
        assert fl.playlist.add_pattern_ticks(12, track=2, start=0, length=4 * BAR).notice == ""
        # length 0 follows the pattern, so nothing has to be looked up at all.
        assert fl.playlist.add_pattern_ticks(12, track=2, start=0).notice == ""
    assert operations(transport) == ["query_patterns", "add_pattern_clip", "add_pattern_clip"]


def test_a_host_that_cannot_describe_patterns_never_fails_the_placement(fl: Studio,
                                                                       transport: RecordingTransport) -> None:
    with warnings.catch_warnings():
        warnings.simplefilter("error")
        result = fl.playlist.add_pattern_ticks(12, track=2, start=0, length=4 * BAR)
    assert result == 1 and result.notice == "" and result.pattern_length_tick == 0
    assert operations(transport) == ["query_patterns", "add_pattern_clip"]


def test_add_pattern_beats_warns_and_repeats_through_the_tick_path(fl: Studio,
                                                                   transport: RecordingTransport) -> None:
    install_patterns(transport, (12, BAR))
    with pytest.warns(PatternClipLongerThanPatternWarning):
        fl.playlist.add_pattern(12, track=2, start_beats=0, length_beats=8)
    assert operations(transport) == ["get_ppq", "query_patterns", "add_pattern_clip"]

    transport.calls.clear()
    with warnings.catch_warnings():
        warnings.simplefilter("error")
        assert fl.playlist.add_pattern(12, track=2, start_beats=0, length_beats=8, repeat=True) == 2
    assert operations(transport) == ["get_ppq", "query_patterns", "add_pattern_clips"]


# ---- repeat=True --------------------------------------------------------------------------------

def test_add_pattern_ticks_repeat_tiles_instead_of_one_long_clip(fl: Studio,
                                                                 transport: RecordingTransport) -> None:
    install_patterns(transport, (12, BAR))
    with warnings.catch_warnings():
        warnings.simplefilter("error")
        result = fl.playlist.add_pattern_ticks(12, track=2, start=0, length=4 * BAR, repeat=True)
    assert result == 4 and result.repeated and result.notice == ""
    assert operations(transport) == ["query_patterns", "add_pattern_clips"]
    assert placed(transport, 1) == [
        {"pattern": 12, "track": 2, "startTick": bar * BAR, "lengthTick": BAR} for bar in range(4)]


def test_add_patterns_repeat_expands_every_spec_in_one_native_pass(fl: Studio,
                                                                   transport: RecordingTransport) -> None:
    install_patterns(transport, (12, BAR), (13, 2 * BAR))
    specs = [PatternClipSpec(12, 1, 0, 2 * BAR), PatternClipSpec(13, 2, 0, 3 * BAR), PatternClipSpec(13, 3, 0)]

    with warnings.catch_warnings():
        warnings.simplefilter("error")
        result = fl.playlist.add_patterns(specs, repeat=True)

    assert result == 0 and result.placed == 5 and result.repeated and result.notices == ()
    assert operations(transport) == ["query_patterns", "add_pattern_clips"], "one lookup, one placement pass"
    assert placed(transport, 1) == [
        {"pattern": 12, "track": 1, "startTick": 0, "lengthTick": BAR},
        {"pattern": 12, "track": 1, "startTick": BAR, "lengthTick": BAR},
        {"pattern": 13, "track": 2, "startTick": 0, "lengthTick": 2 * BAR},
        {"pattern": 13, "track": 2, "startTick": 2 * BAR, "lengthTick": BAR},
        {"pattern": 13, "track": 3, "startTick": 0, "lengthTick": 0}]


def test_add_patterns_collects_one_notice_per_over_long_spec(fl: Studio, transport: RecordingTransport) -> None:
    install_patterns(transport, (12, BAR), (13, 4 * BAR))
    specs = [PatternClipSpec(12, 1, 0, 4 * BAR), PatternClipSpec(13, 2, 0, 4 * BAR), PatternClipSpec(12, 3, 0, 2 * BAR)]

    with pytest.warns(PatternClipLongerThanPatternWarning) as caught:
        result = fl.playlist.add_patterns(specs)

    assert len(caught) == 2, "pattern 13 fills its clip exactly, so only the two pattern-12 specs warn"
    assert result.notices == tuple(str(item.message) for item in caught)
    assert result.placed == 3 and not result.repeated and result == 0


def test_add_patterns_repeat_refuses_a_runaway_expansion_before_any_write(fl: Studio,
                                                                          transport: RecordingTransport) -> None:
    install_patterns(transport, (12, PPQ // 2))
    with pytest.raises(ValueError):
        fl.playlist.add_patterns([PatternClipSpec(12, 1, 0, (MAX_TILED_CLIPS + 1) * (PPQ // 2))], repeat=True)
    assert "add_pattern_clips" not in operations(transport)


def test_pattern_length_reads_the_host_reported_length(fl: Studio, transport: RecordingTransport) -> None:
    install_patterns(transport, (7, 2 * BAR))
    assert fl.playlist.pattern_length(7) == 2 * BAR
    assert fl.patterns[7].length_tick == 2 * BAR
    with pytest.raises(LookupError):
        fl.playlist.pattern_length(9)
