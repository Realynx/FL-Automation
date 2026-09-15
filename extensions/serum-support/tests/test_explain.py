"""Naming FL wrapper parameter values: sub shape enum, wavetable frames and the anonymous FX proxy slots."""

from __future__ import annotations

from typing import Any

from test_describe import smart_future_like

from fruitylink_serum import describe


class _Parameters:
    def __init__(self, rows: list[tuple[int, str, str, float | None]]) -> None:
        self.rows = rows

    def all(self, filter: str | None = None) -> list[Any]:
        import struct

        from fruitylink.models import PluginParameterInfo

        out = []
        for index, name, display, normalized in self.rows:
            raw = struct.unpack("<i", struct.pack("<f", normalized))[0] if normalized is not None else 12800
            out.append(PluginParameterInfo(index, name, raw, display))
        return [row for row in out if not filter or filter.casefold() in row.name.casefold()]


class _Channel:
    def __init__(self, rows: list[tuple[int, str, str, float | None]]) -> None:
        self.parameters = _Parameters(rows)


class _Fl:
    def __init__(self, rows: list[tuple[int, str, str, float | None]]) -> None:
        self.channels = {4: _Channel(rows)}


def test_sub_shape_values_are_named() -> None:
    meaning = describe.explain_parameter("Sub Shape", 0.25)
    assert meaning["known"] and meaning["value"] == "roundrect"        # live: 0.25 -> RoundRect
    assert meaning["values"] == {0.0: "sine", 0.2: "roundrect", 0.4: "triangle", 0.6: "saw", 0.8: "square", 1.0: "pulse"}
    assert describe.explain_parameter("Sub Shape", 1.0)["value"] == "pulse"


def test_wavetable_position_resolves_table_and_frame_from_state() -> None:
    state = smart_future_like()
    meaning = describe.explain_parameter("A WT Pos", 113.778 / 256, state=state)
    assert meaning["known"] and meaning["table"] == "/S2 Tables/Default Shapes.wav" and meaning["num_frames"] == 9
    assert meaning["value"] == {"frame": 4, "label": "square"}
    assert describe.explain_parameter("B WT Pos", 0.5, state=state)["table"] == "Mine"
    without = describe.explain_parameter("A WT Pos", 0.5)
    assert without["known"] and "state=" in without["note"] and "value" not in without


def test_fx_proxy_slots_are_declared_opaque_with_the_loaded_units() -> None:
    state = smart_future_like()
    names = describe.fx_slot_names(state)
    assert [u["label"] for u in names["racks"][0]["units"]] == ["Reverb", "Delay"]
    assert names["racks"][0]["fl_prefix"] == "FX Main Param" and names["racks"][1]["units"] == []
    assert "kParamWet" in names["racks"][0]["units"][0]["parameters"]
    meaning = describe.explain_parameter("FX Main Param 3", 0.5, state=state)
    assert meaning["known"] is False and meaning["rack"] == 0 and meaning["slot"] == 3
    assert meaning["units"] == ["Reverb", "Delay"] and "proxyParams" in meaning["note"]
    assert describe.explain_parameter("FX Bus2 Param 16")["rack"] == 2
    assert describe.explain_parameter("Filter 1 Freq", 0.5) == {"parameter": "Filter 1 Freq", "known": False}


def test_explain_parameters_annotates_the_wrapper_list() -> None:
    fl = _Fl([(59, "A Unison", "4", 0.2), (200, "Sub Shape", "RoundRect", 0.25), (62, "A WT Pos", "4", 113.778 / 256),
              (900, "FX Main Param 1", "0.50", 0.5)])
    rows = describe.explain_parameters(fl, 4, state=smart_future_like())
    by_name = {row["name"]: row for row in rows}
    assert by_name["A Unison"]["meaning"] == {"parameter": "A Unison", "known": False}
    assert by_name["Sub Shape"]["meaning"]["value"] == "roundrect"
    assert by_name["A WT Pos"]["meaning"]["value"] == {"frame": 4, "label": "square"}
    assert by_name["FX Main Param 1"]["meaning"]["units"] == ["Reverb", "Delay"]
    assert abs(by_name["A Unison"]["normalized"] - 0.2) < 1e-6 and by_name["A Unison"]["display"] == "4"
    only = describe.explain_parameters(fl, 4, filter="wt pos", state=smart_future_like())
    assert [row["index"] for row in only] == [62]
