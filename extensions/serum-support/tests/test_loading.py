"""Container, codec and generation tests on synthetic data only (no proprietary presets)."""

from __future__ import annotations

import base64
import hashlib
import json
import struct
from pathlib import Path
from typing import Any

import pytest

from fruitylink_serum import cbor, loading, vstpreset, xfer

ZSTD = xfer.zstd_available()
needs_zstd = pytest.mark.skipif(not ZSTD, reason="zstd support requires Python 3.14 or the zstandard package")


def synthetic_state() -> dict[str, Any]:
    return {
        "Oscillator0": {
            "plainParams": {"kParamLevel": cbor.Float32(0.5), "kParamUnison": cbor.Float32(3.0), "kParamDetune": 0.1},
            "WTOsc0": {"plainParams": {}, "relativePathToWT": "S2 Tables/Default Shapes.wav", "numFrames": 4},
        },
        # float32-exact values so decoded == source holds without tolerance
        "VoiceFilter0": {"plainParams": {"kParamCutoff": cbor.Float32(0.375), "kParamEnabled": cbor.Float32(1.0)}},
        "Global0": {"plainParams": {"kParamMasterVolume": 0.3}},
        "presetName": "Synthetic",
        "tags": ["Test"],
        "scalars": {"lockTuning": False},
    }


def synthetic_container() -> xfer.XferContainer:
    metadata = {"fileType": "SerumPreset", "presetName": "Synthetic", "product": "Serum2",
                "productVersion": "2.1.4", "vendor": "Test", "version": 5.0}
    return xfer.XferContainer(metadata=metadata, state=synthetic_state())


def test_cbor_round_trip_preserves_float_widths() -> None:
    state = synthetic_state()
    encoded = cbor.encode(state)
    decoded = cbor.decode(encoded)
    assert decoded == state
    assert isinstance(decoded["Oscillator0"]["plainParams"]["kParamLevel"], cbor.Float32)
    assert not isinstance(decoded["Oscillator0"]["plainParams"]["kParamDetune"], cbor.Float32)
    assert cbor.encode(decoded) == encoded
    assert cbor.decode(b"\xfa\x3f\x80\x00\x00") == 1.0
    assert cbor.decode(b"\x20") == -1
    assert cbor.decode(bytes([0xC1, 0x18, 0x2A])) == cbor.Tagged(1, 42)
    with pytest.raises(cbor.CborError):
        cbor.decode(b"\x01\x02")


@needs_zstd
def test_container_round_trip_and_hash() -> None:
    container = synthetic_container()
    blob = xfer.write_container(container)
    assert blob.startswith(xfer.MAGIC)
    meta_length = struct.unpack_from("<I", blob, len(xfer.MAGIC))[0]
    meta = json.loads(blob[len(xfer.MAGIC) + 8 : len(xfer.MAGIC) + 8 + meta_length])
    payload = blob[len(xfer.MAGIC) + 8 + meta_length + 8 :]
    assert meta["hash"] == hashlib.md5(payload).hexdigest()
    parsed = xfer.read_container(blob)
    assert parsed.state == container.state
    assert parsed.metadata["presetName"] == "Synthetic"
    with pytest.raises(xfer.XferError):
        xfer.read_container(b"notxfer" + blob)


@needs_zstd
def test_split_wrapper_blocks_finds_processor_and_controller() -> None:
    processor = xfer.write_container(synthetic_container())
    controller = xfer.write_container(xfer.XferContainer(metadata={"component": "controller"}, state={"ui": 1}))
    chunk = b"\x01\x00\x00\x00header" + struct.pack("<III", 3, len(processor), 0) + processor
    chunk += struct.pack("<III", 2, len(controller), 0) + controller + b"trailer"
    blocks = xfer.split_wrapper_blocks(chunk)
    assert [kind for kind, _ in blocks] == [3, 2]
    assert blocks[0][1] == processor and blocks[1][1] == controller


@needs_zstd
def test_build_preset_applies_overrides_and_rejects_unknown_keys(tmp_path: Path) -> None:
    base = tmp_path / "Base.SerumPreset"
    base.write_bytes(xfer.write_container(synthetic_container()))
    generated = loading.build_preset(
        base,
        {"Oscillator0/plainParams/kParamLevel": 0.875, "VoiceFilter0/kParamCutoff": 0.75, "Global0.plainParams.kParamMasterVolume": 0.25},
        name="Brighter",
        output_dir=tmp_path,
    )
    assert generated == tmp_path / "Brighter.SerumPreset"
    result = loading.read_preset(generated)
    params = loading.parameters(result)
    assert params["Oscillator0/plainParams/kParamLevel"] == 0.875
    assert params["VoiceFilter0/plainParams/kParamCutoff"] == 0.75
    assert params["Global0/plainParams/kParamMasterVolume"] == 0.25
    assert isinstance(result.state["Oscillator0"]["plainParams"]["kParamLevel"], cbor.Float32)
    assert result.metadata["presetName"] == "Brighter" and result.state["presetName"] == "Brighter"
    assert loading.read_preset(base).state["Oscillator0"]["plainParams"]["kParamLevel"] == 0.5
    with pytest.raises(KeyError):
        loading.build_preset(base, {"Oscillator9/plainParams/kParamLevel": 0.1}, output_dir=tmp_path)
    with pytest.raises(KeyError):
        loading.build_preset(base, {"Oscillator0/WTOsc0/numFrames/kParamNope": 0.1}, output_dir=tmp_path)
    with pytest.raises(KeyError):
        loading.build_preset(base, {"Oscillator0/plainParams/notAParam": 0.1}, output_dir=tmp_path)
    # Serum omits default-valued parameters, so a new kParam inside an existing plainParams map is allowed.
    added = loading.read_preset(loading.build_preset(base, {"Oscillator0/plainParams/kParamOctave": 1.0}, output_dir=tmp_path))
    assert isinstance(added.state["Oscillator0"]["plainParams"]["kParamOctave"], cbor.Float32)
    with pytest.raises(TypeError):
        loading.build_preset(base, {"Oscillator0/plainParams/kParamLevel": "loud"}, output_dir=tmp_path)


def test_normalize_processor_state_applies_the_live_verified_recipe() -> None:
    state = synthetic_state()
    state.update({"Osc": [{"kUIParamZoom": 0.0}], "WTOsc": [], "SerumGUI": {"kUIParamShowKeyboard": 1.0},
                  "ClipPlayer": {"kUIParamPreviewClip": 0.0}, "ClipPlayer0": {"plainParams": "default"},
                  "fileType": "SerumPreset", "presetAuthor": "x",
                  "arpBankDisplayName": "", "version": 4.0, "productVersion": "2.0.12", "product": "Serum2"})
    container = xfer.XferContainer(metadata={"fileType": "SerumPreset", "presetName": "Synthetic", "product": "Serum2",
                                             "productVersion": "2.0.12", "version": 4.0, "tags": ["Preview"]}, state=state)
    processor = loading.normalize_processor_state(container)
    for key in loading.PROCESSOR_DROPPED_SECTIONS:
        assert key not in processor.state
    assert "Oscillator0" in processor.state and "ClipPlayer0" in processor.state and "scalars" in processor.state
    for key, value in loading.PROCESSOR_IDENTITY.items():
        assert processor.state[key] == value and processor.metadata[key] == value
    assert processor.metadata["version"] == 10.0 and processor.metadata["productVersion"] == "2.1.4"
    assert not any(key in processor.metadata for key in ("fileType", "presetName", "tags"))
    assert container.state["version"] == 4.0  # source untouched
    custom = loading.normalize_processor_state(container, identity={"productVersion": "2.2.0"})
    assert custom.state["productVersion"] == "2.2.0" and custom.state["component"] == "processor"


@needs_zstd
def test_to_vstpreset_wraps_normalised_processor_state(tmp_path: Path) -> None:
    base = tmp_path / "Base.SerumPreset"
    base.write_bytes(xfer.write_container(synthetic_container()))
    output = loading.to_vstpreset(base, tmp_path / "Base.vstpreset")
    preset = vstpreset.read_vstpreset(output.read_bytes())
    assert preset.class_id == vstpreset.SERUM2_CLASS_ID == "56534558667350736572756D20320000" and preset.controller is None
    component = xfer.read_container(preset.component)
    assert component.metadata["component"] == "processor" and component.metadata["version"] == 10.0
    assert "presetName" not in component.metadata
    expected = synthetic_state()
    expected.pop("presetName")  # file identity is dropped from the processor state
    expected.update(loading.PROCESSOR_IDENTITY)
    assert component.state == expected
    raw = loading.to_vstpreset(base, tmp_path / "Raw.vstpreset", normalize=False)
    assert xfer.read_container(vstpreset.read_vstpreset(raw.read_bytes()).component).state == synthetic_state()


def test_vstpreset_layout_and_class_id_conversion() -> None:
    data = vstpreset.write_vstpreset(vstpreset.VstPreset(class_id="00" * 16, component=b"comp", controller=b"ctrl"))
    assert data[:4] == b"VST3" and struct.unpack_from("<i", data, 4)[0] == 1
    list_offset = struct.unpack_from("<q", data, 40)[0]
    assert data[list_offset : list_offset + 4] == b"List"
    parsed = vstpreset.read_vstpreset(data)
    assert (parsed.component, parsed.controller) == (b"comp", b"ctrl")
    # FL's wrapper accepted the GUID string order live; the FUID memory order was ignored.
    assert vstpreset.class_id_from_guid("{56534558-6673-5073-6572-756D20320000}") == vstpreset.SERUM2_CLASS_ID
    assert vstpreset.SERUM2_CLASS_ID == "56534558667350736572756D20320000"
    assert vstpreset.class_id_from_guid("56534558-6673-5073-6572-756d20320000") == vstpreset.SERUM2_CLASS_ID
    with pytest.raises(ValueError):
        vstpreset.class_id_from_guid("{5653-4558}")
    with pytest.raises(ValueError):
        vstpreset.write_vstpreset(vstpreset.VstPreset(class_id="nothex", component=b""))


def test_library_navigation_and_resolution(tmp_path: Path) -> None:
    root = tmp_path / "Serum 2 Presets"
    for relative in ("Factory/Lead/LD - Bright.SerumPreset", "Factory/Bass/BS - Deep.SerumPreset",
                     "User/Lead/LD - Bright.SerumPreset", "User/Misc/old.fxp"):
        target = root / "Presets" / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(b"opaque")
    assert loading.list_folders(root) == ("Factory/Bass", "Factory/Lead", "User/Lead", "User/Misc")
    found = loading.find_presets(root, folder="Factory", text="bright")
    assert [p.path.name for p in found] == ["LD - Bright.SerumPreset"]
    assert len(loading.find_presets(root, text="LD -")) == 2
    assert loading.resolve_preset("BS - Deep", root) == (root / "Presets/Factory/Bass/BS - Deep.SerumPreset").resolve()
    assert loading.resolve_preset("User/Lead/LD - Bright.SerumPreset", root).parent.name == "Lead"
    with pytest.raises(LookupError):
        loading.resolve_preset("LD - Bright", root)
    with pytest.raises(FileNotFoundError):
        loading.resolve_preset("missing", root)


class _Ops:
    def __init__(self) -> None:
        self.calls: list[tuple[str, dict[str, Any]]] = []

    def load_channel_plugin_state(self, *, channel: int, path: str, use_channel_loader: bool = False) -> str:
        self.calls.append(("channel", {"channel": channel, "path": path, "use_channel_loader": use_channel_loader}))
        return f"channel {channel}: loaded"

    def load_mixer_effect_state(self, *, track: int, slot: int, path: str) -> str:
        self.calls.append(("mixer", {"track": track, "slot": slot, "path": path}))
        return f"mixer {track}/{slot}: loaded"

    def get_channel_plugin_state(self, *, channel: int) -> str:
        self.calls.append(("get_channel", {"channel": channel}))
        return base64.b64encode(wrapper_state_blob()).decode("ascii")

    def get_mixer_effect_state(self, *, track: int, slot: int) -> str:
        self.calls.append(("get_mixer", {"track": track, "slot": slot}))
        return base64.b64encode(b"opaque-fabfilter-state").decode("ascii")


class _Studio:
    def __init__(self) -> None:
        self.ops = _Ops()


def test_load_preset_routes_files_to_the_host(tmp_path: Path) -> None:
    root = tmp_path / "root"
    fst = root / "Presets" / "User" / "Captured.fst"
    fst.parent.mkdir(parents=True)
    fst.write_bytes(b"FLhd")
    fl = _Studio()
    assert loading.load_preset(fl, 4, fst, root=root) == "channel 4: loaded"
    assert loading.load_preset(fl, 2, "Captured", root=root, slot=1) == "mixer 2/1: loaded"
    assert fl.ops.calls[0][1]["path"] == str(fst.resolve()) and fl.ops.calls[1][0] == "mixer"
    with pytest.raises(ValueError):
        loading.load_preset(fl, 1, fst, root=root, format="magic")


@needs_zstd
def test_load_preset_converts_serum_presets_by_default(tmp_path: Path) -> None:
    root = tmp_path / "root"
    source = root / "Presets" / "Factory" / "Synthetic.SerumPreset"
    source.parent.mkdir(parents=True)
    source.write_bytes(xfer.write_container(synthetic_container()))
    fl = _Studio()
    loading.load_preset(fl, 3, "Synthetic", root=root)
    sent = Path(fl.ops.calls[-1][1]["path"])
    assert sent.suffix == ".vstpreset" and vstpreset.read_vstpreset(sent.read_bytes()).class_id == vstpreset.SERUM2_CLASS_ID
    loading.load_preset(fl, 3, "Synthetic", root=root, format="direct")
    assert Path(fl.ops.calls[-1][1]["path"]) == source.resolve()


def wrapper_state_blob() -> bytes:
    """An FL wrapper plugin-data record: header noise, then kind 3 (processor) and kind 2 (controller) blocks."""
    processor = xfer.XferContainer(
        metadata={"component": "processor", "product": "Serum2", "productVersion": "2.1.4", "version": 10.0},
        state={**synthetic_state(), "component": "processor"},
    )
    controller = xfer.XferContainer(metadata={"component": "controller", "product": "Serum2"}, state={"ui": 1})
    blocks = b""
    for kind, container in ((3, processor), (2, controller)):
        block = xfer.write_container(container)
        blocks += struct.pack("<III", kind, len(block), 0) + block
    return b"\x00" * 40 + blocks


@needs_zstd
def test_read_state_returns_the_requested_component(tmp_path: Path) -> None:
    fl = _Studio()
    processor = loading.read_state(fl, 4)
    assert processor.state["Oscillator0"]["plainParams"]["kParamUnison"] == 3.0
    assert loading.parameters(processor)["Global0/plainParams/kParamMasterVolume"] == pytest.approx(0.3)
    controller = loading.read_state(fl, 4, component="controller")
    assert controller.state == {"ui": 1}
    assert fl.ops.calls == [("get_channel", {"channel": 4}), ("get_channel", {"channel": 4})]
    with pytest.raises(ValueError):
        loading.read_state(fl, 4, component="editor")
    with pytest.raises(LookupError):
        loading.read_state(fl, 1, slot=0)   # opaque non-Serum state carries no XferJson block


@needs_zstd
def test_snapshot_preset_writes_a_loadable_named_file(tmp_path: Path) -> None:
    fl = _Studio()
    path = loading.snapshot_preset(fl, 4, "Lantern: live", output_dir=tmp_path)
    assert path == tmp_path / "Lantern_ live.SerumPreset"
    saved = loading.read_preset(path)
    assert saved.metadata["fileType"] == "SerumPreset" and saved.metadata["presetName"] == "Lantern: live"
    assert "component" not in saved.metadata and "component" not in saved.state
    assert saved.state["Oscillator0"]["plainParams"]["kParamUnison"] == 3.0
    edited = loading.build_preset(path, {"Oscillator0/kParamUnison": 5.0}, output_dir=tmp_path)
    assert loading.parameters(loading.read_preset(edited))["Oscillator0/plainParams/kParamUnison"] == 5.0
    with pytest.raises(ValueError):
        loading.snapshot_preset(fl, 4, "   ")
    # live 2026-09-14: a full path passed as the name was flattened into a %TEMP% file name; now it is honoured
    requested = tmp_path / "live-check" / "velvet-ch1-snapshot.SerumPreset"
    assert loading.snapshot_preset(fl, 4, str(requested)) == requested
    assert loading.read_preset(requested).metadata["presetName"] == "velvet-ch1-snapshot"
    relative = loading.snapshot_preset(fl, 4, "sub/dir/take 2", output_dir=tmp_path)
    assert relative == tmp_path / "sub" / "dir" / "take 2.SerumPreset" and relative.is_file()
