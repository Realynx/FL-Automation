"""Known parameter scales: the JSON table, lookup and conversions against the live evidence in the log."""

import pytest

from fruitylink.plugins import ParameterScale, add_scale, known_plugins, scale_for, scales_for


def test_table_lists_the_measured_plugins() -> None:
    names = known_plugins()
    for plugin in ("Fruity Reeverb 2", "Fruity Delay 3", "FabFilter Pro-L 2", "FabFilter Pro-Q 4", "Super VHS", "Serum 2"):
        assert plugin in names
    assert all(scale.confidence in ("verified", "measured", "inferred", "rough") for plugin in names for scale in scales_for(plugin))


@pytest.mark.parametrize("plugin,parameter,normalized,display", [
    ("Pro-L 2", "Gain", 0.6, 18.0),                     # Ember Tides: 0.6 -> +18 dB
    ("fabfilter pro-l 2", "Output Level", 0.9, -3.0),   # Parking Lot Moon: 0.9 -> -3.00 dBTP
    ("Fruity Reeverb 2", "Predelay", 0.02, 20.0),
    ("Reeverb 2", "Dry", 1.0, 125.0),
    ("Fruity Delay 3", "Feedback", 0.4, 50.0),
    ("Fruity Delay 3", "Time", 0.1875, 3.0),            # "3:1" at v 0.1875
    ("Serum 2", "Filter 1 Freq", 0.5, 8 * 2756 ** 0.5),
    ("Serum 2", "A Unison", 0.2, 4.0),
    ("Serum 2", "Env 1 Sustain", 1.0, 0.0),
    ("Serum 2", "Main Vol", 0.5 ** 0.5, -3.0),          # state 0.5 -> -3.0 dB (builder regression points)
])
def test_formula_scales_reproduce_the_live_points(plugin: str, parameter: str, normalized: float, display: float) -> None:
    scale = scale_for(plugin, parameter)
    assert scale.to_display(normalized) == pytest.approx(display, abs=0.05)
    assert scale.to_normalized(display) == pytest.approx(normalized, abs=2e-3)   # displays are rounded


def test_table_scales_interpolate_between_measured_points_and_invert() -> None:
    low_cut = scale_for("Fruity Reeverb 2", "Low cut")
    assert low_cut.to_display(0.1) == pytest.approx(317)
    between = low_cut.to_display(0.125)
    assert isinstance(between, float) and 317 < between < 466
    assert low_cut.to_normalized(between) == pytest.approx(0.125, abs=1e-9)
    assert low_cut.to_normalized(466) == pytest.approx(0.15)
    beyond = low_cut.to_display(0.2)
    assert isinstance(beyond, float) and beyond > 466   # extrapolated beyond the last point
    decay = scale_for("Fruity Reeverb 2", "Decay")
    assert decay.to_display(0.12) == pytest.approx(2.5)
    assert 0.1 < decay.to_normalized(2.3) < 0.12
    assert "table 0.1..0.35" in decay.describe() and "[measured]" in decay.describe()


def test_enum_and_log_scales() -> None:
    shape = scale_for("Serum 2", "Sub Shape")
    assert shape.to_display(0.25) == "RoundRect"        # live: 0.25 read back as RoundRect
    assert shape.to_display(0.0) == "Sine" and shape.to_display(1.0) == "Pulse"
    assert shape.to_normalized("saw") == 0.6
    with pytest.raises(LookupError):
        shape.to_normalized("Noise")
    sustain = scale_for("Serum 2", "Env 2 Sustain")
    assert sustain.to_display(0.9) == pytest.approx(-1.83, abs=0.01)
    assert sustain.to_display(0.0) == float("-inf")
    assert scale_for("Super VHS", "Output").to_display(0.8) == pytest.approx(-8.0)
    assert scale_for("Super VHS", "Output").confidence == "rough"


def test_lookup_errors_name_what_is_known() -> None:
    with pytest.raises(LookupError) as unknown:
        scale_for("Ozone 11", "Gain")
    assert "Fruity Reeverb 2" in str(unknown.value)
    with pytest.raises(LookupError) as missing:
        scale_for("Fruity Reeverb 2", "Diffusion")
    assert "Low cut" in str(missing.value)
    with pytest.raises(LookupError):
        scale_for("Fruity Reeverb 2", 3)
    with pytest.raises(ValueError):
        scale_for("Fruity Reeverb 2", "Dry").to_display(1.5)


def test_glob_parameters_and_runtime_additions() -> None:
    freq = scale_for("Pro-Q 4", "Band 3 Frequency")
    assert freq.to_display(0.5) == pytest.approx(10 * 3000 ** 0.5)
    assert scale_for("Pro-Q 4", "Band 12 Gain").to_display(0.5) == pytest.approx(0.0)
    add_scale(ParameterScale("Fruity Compressor", "Threshold", "linear", "dB", "measured", spec={"a": 60, "b": -60}))
    assert scale_for("Fruity Compressor", "Threshold").to_display(0.5) == -30.0
    assert "Fruity Compressor" in known_plugins()
