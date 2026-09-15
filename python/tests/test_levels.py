"""The dB model of FL's fader law: anchors, inverses, scale bounds and the documented table."""

import math

import pytest

from fruitylink import levels
from fruitylink.levels import (
    CHANNEL_VOLUME_DEFAULT,
    FADER_EXPONENT,
    FADER_MAX_DB,
    channel_volume_from_db,
    channel_volume_to_db,
    fader_db,
    fader_position,
    mixer_volume_from_db,
    mixer_volume_to_db,
    send_level_from_db,
    send_level_to_db,
    volume_table,
)


def test_unity_anchor_and_the_calibrated_exponent() -> None:
    assert mixer_volume_to_db(12800) == pytest.approx(0.0)
    assert channel_volume_to_db(10240) == pytest.approx(0.0)
    assert send_level_to_db(0.8) == pytest.approx(0.0)
    # The fader top is the curve's consequence (about +4.05 dB), not FL's +5.6 dB hint.
    assert mixer_volume_to_db(16000) == pytest.approx(FADER_MAX_DB) == pytest.approx(4.05, abs=0.02)
    assert channel_volume_to_db(12800) == pytest.approx(FADER_MAX_DB)
    assert send_level_to_db(1.0) == pytest.approx(FADER_MAX_DB)
    assert levels.FL_HINT_TOP_DB == 5.6
    assert 2.05 < FADER_EXPONENT < 2.15


@pytest.mark.parametrize("to_db,loud,quiet,measured", [
    # Render calibration 2026-09-14 (Parking Lot Moon live-checks, isolated held chord, bars 110-111)
    # and the Ember Tides full-mix master move. One power law has to reach all four within 0.7 dB.
    (channel_volume_to_db, 12800, 6400, -12.54),
    (mixer_volume_to_db, 12800, 6400, -12.70),
    (mixer_volume_to_db, 12800, 5769, -14.21),
    (mixer_volume_to_db, 6800, 12800, 11.80),
])
def test_curve_matches_every_rendered_measurement(to_db: object, loud: int, quiet: int, measured: float) -> None:
    assert to_db(quiet) - to_db(loud) == pytest.approx(measured, abs=0.7)  # type: ignore[operator]


def test_documented_channel_examples_hold() -> None:
    assert channel_volume_to_db(CHANNEL_VOLUME_DEFAULT) == pytest.approx(-0.43, abs=0.05)
    assert channel_volume_to_db(5000) == pytest.approx(-13.0, abs=0.2)
    assert channel_volume_to_db(3200) == pytest.approx(-21.1, abs=0.2)
    assert mixer_volume_to_db(6400) == pytest.approx(-12.6, abs=0.2)


@pytest.mark.parametrize("raw", [0, 1, 3200, 5000, 10000, 10240, 12800])
def test_channel_round_trip(raw: int) -> None:
    assert channel_volume_from_db(channel_volume_to_db(raw)) == raw


@pytest.mark.parametrize("raw", [0, 6800, 12800, 15999, 16000])
def test_mixer_round_trip(raw: int) -> None:
    assert mixer_volume_from_db(mixer_volume_to_db(raw)) == raw


def test_silence_and_headroom_edges() -> None:
    assert mixer_volume_to_db(0) == -math.inf
    assert mixer_volume_from_db(-math.inf) == 0
    assert mixer_volume_from_db(0.0) == 12800
    assert channel_volume_from_db(0.0) == 10240
    assert send_level_from_db(0.0) == pytest.approx(0.8)
    assert mixer_volume_from_db(FADER_MAX_DB) == 16000
    with pytest.raises(ValueError, match="above the fader top"):
        mixer_volume_from_db(4.2)
    with pytest.raises(ValueError, match="above the fader top"):
        channel_volume_from_db(5.6)   # FL's fader hint is above this curve's top


@pytest.mark.parametrize("raw", [-1, 12801, 1.5, True, "10000"])
def test_channel_scale_bounds_are_enforced(raw: object) -> None:
    with pytest.raises(ValueError):
        channel_volume_to_db(raw)  # type: ignore[arg-type]


@pytest.mark.parametrize("raw", [-1, 16001, 2.0, False])
def test_mixer_scale_bounds_are_enforced(raw: object) -> None:
    with pytest.raises(ValueError):
        mixer_volume_to_db(raw)  # type: ignore[arg-type]


def test_exponent_override_recalibrates_every_helper() -> None:
    assert fader_db(0.5, exponent=2.0) == pytest.approx(40 * math.log10(0.625))
    assert fader_position(fader_db(0.5, exponent=2.0), exponent=2.0) == pytest.approx(0.5)
    assert mixer_volume_to_db(6800, exponent=2.0) == pytest.approx(-11.0, abs=0.1)
    with pytest.raises(ValueError):
        fader_db(0.5, exponent=0)
    with pytest.raises(ValueError):
        fader_position(float("nan"))


def test_volume_table_is_monotonic_and_matches_the_helpers() -> None:
    table = volume_table(1600)
    assert [row[0] for row in table] == list(range(0, 12801, 1600))
    assert all(a[1] < b[1] and a[2] < b[2] for a, b in zip(table, table[1:]))
    assert table[-1][1] == pytest.approx(FADER_MAX_DB)
    assert table[-1][2] == pytest.approx(mixer_volume_to_db(12800)) == pytest.approx(0.0)
    with pytest.raises(ValueError):
        volume_table(0)


def test_module_constants_are_the_documented_scales() -> None:
    assert (levels.MIXER_VOLUME_MAX, levels.MIXER_VOLUME_UNITY) == (16000, 12800)
    assert (levels.CHANNEL_VOLUME_MAX, levels.CHANNEL_VOLUME_UNITY, levels.CHANNEL_VOLUME_DEFAULT) == (12800, 10240, 10000)
    assert levels.SEND_UNITY == levels.FADER_UNITY == 0.8
    assert levels.FADER_EXPONENT == 2.09
    assert levels.FADER_MAX_DB == pytest.approx(20 * 2.09 * math.log10(1 / 0.8))
