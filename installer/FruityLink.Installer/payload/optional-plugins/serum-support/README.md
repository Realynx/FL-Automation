# FruityLink Serum support (optional)

This separate pure-Python wheel inventories Serum presets and describes audio that
the caller identifies as an isolated Serum audition. It reuses FruityLink's bounded,
dependency-free amplitude and spectral analysis. It does not identify oscillator
waveforms from a preset name or from the strongest FFT bin.

The package remains separate from the FruityLink SDK wheel and FL MCP plugin. The
normal framework install includes it by default as a removable extension.

## Build and use locally

From the SDK repository root, run the same complete validation used by CI:

```powershell
uv run --project python python extensions/serum-support/tools/check.py
```

The checker uses the local SDK and extension sources for imports and type checking,
then runs Ruff, strict mypy, pytest, and an isolated distribution build. Its build
artifacts go to a temporary directory and do not enter the core wheel or installer.
It also rejects unexpected wheel files, nested archives, and build directories in
the source distribution.

From this directory:

```powershell
python -m build
python -m pip install --no-index ..\..\python\dist\fruitylink_python-0.2.0-py3-none-any.whl dist\fruitylink_serum-0.1.0-py3-none-any.whl
```

```python
from fruitylink_serum import describe_wav

result = describe_wav(r"C:\auditions\preset-C4.wav", end_seconds=4,
                      preset_name="KY - Smart Future")
```

`describe_wav` reports instrument identity as unverified. It only echoes an
optional caller-supplied preset name; it does not establish that Serum produced
the audio. For comparable rankings, render the same MIDI note, velocity, duration,
sample rate, and effect policy for every preset.

## Use from FL MCP or the Python IDE

The installer places the wheel at
`FruityLink/python/extensions/serum-support/fruitylink_serum-0.1.0-py3-none-any.whl`.
The shared embedded runtime discovers installed extension wheels there, so FLMCP
requests and Python IDE scripts use a normal import:

```python
from fruitylink_serum import describe_wav

result = describe_wav(r"C:\auditions\preset-C4.wav", end_seconds=4)
```

The bundled `fruitylink-python` wheel remains on the interpreter's configured
path and satisfies this package's runtime dependency. Remove the extension wheel
and restart FL Studio to uninstall it. Embedded Python never searches global
`site-packages` or `PYTHONPATH`.

The Serum wheel declares its `fruitylink-python>=0.2.0` dependency without
bundling it. Install both local wheels together as above, or use `--no-deps` for
the extension when the matching core wheel is already available.

## Descriptor meaning

The compact JSON response contains source provenance, caller attribution, SDK
amplitude and spectral measurements, a 10 ms RMS envelope, stereo side-to-mid
energy, channel balance and correlation, and matched traits with their numeric threshold evidence. Trait names are
fixed rules for search and ranking, not learned classifications. The audition
note, velocity, effects, and render settings materially affect every result.

Each call accepts at most 15 seconds and two million scalar samples (frames times
channels), whichever is smaller. This keeps the extension below the shared SDK's
spectral work bound even for high-rate or multichannel files.
