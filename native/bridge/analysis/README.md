# Read-only FL binary analysis

`inspect_fl_profiles.py` reads PE sections without loading the engine, counts every match for
nonempty signatures in `sigscan.cpp`, and reports Delphi VMT identity evidence. Python's
standard library is sufficient. The script reports preferred VAs and RVAs; it is a diagnostic,
not a substitute for production resolver validation.

From the SDK directory:

```powershell
python native/bridge/analysis/inspect_fl_profiles.py `
  'C:/Program Files/Image-Line/FL Studio 2026/FLEngine_x64.dll' `
  --signatures native/bridge/sigscan.cpp `
  --output artifacts/reanalysis/fl2026.json
```

`InspectFlFunctions.java` takes a UTF-8 TSV of logical names and preferred VAs, followed by
an output text path. It disassembles and decompiles only those selected entry points. Run
Ghidra against a **new project under `artifacts`**, with `-noanalysis` and `-max-cpu 2`; there
is no need to open or run FL Studio. The input engine file is read only. Ghidra database
changes occur in the new project, not the installed engine or an existing analyst project.

Example target file:

```text
FLpr_WriteFlpFile	0x11d9090
FLpat_GetNoteRecorder	0x12d31d0
FLmx_SetRouteActiveCore	0x12a5460
```

The separator above is a literal tab. Those target addresses apply only to the 26.1.3.5570
binary identified in the report; derive targets again for another binary.

```powershell
$analysisRoot = Join-Path (Get-Location) 'artifacts/reanalysis'
$scriptRoot = Join-Path (Get-Location) 'native/bridge/analysis'
New-Item -ItemType Directory -Force -Path $analysisRoot | Out-Null
& "$env:GHIDRA_HOME/support/analyzeHeadless.bat" $analysisRoot 'FL2026Focused' `
  -import 'C:/Program Files/Image-Line/FL Studio 2026/FLEngine_x64.dll' `
  -noanalysis -max-cpu 2 -scriptPath $scriptRoot `
  -postScript InspectFlFunctions.java "$analysisRoot/targets.tsv" "$analysisRoot/ghidra.txt"
```

Set `GHIDRA_HOME` to the local Ghidra installation first. Use a different project name for
another pass, or deliberately use `-process FLEngine_x64.dll` instead of `-import` when
extending the disposable project. Store outputs under `artifacts`, which Git ignores.

The small `verified-symbols-2026-09-12.json` file records the six missing managed symbols
recovered during this pass, including full match patterns and exact build addresses.
See [the dated evidence report](../../../docs/fl-version-analysis-2026-09-12.md) for hashes,
semantic checks, changed layouts, and limitations.

`inspect_window_classes.py ENGINE OUTPUT.json --signatures native/bridge/sigscan.cpp` records
published fields, class ancestry, VMT overrides, dynamic paint-message handlers and symbol targets
for the native window analysis.
The sibling OUTPUT.tsv can be passed to InspectFlFunctions. It includes TFLBaseVectorForm,
TVectorForm and the older rejected script/bridged-editor candidates. Run it against each exact
binary; matching class names alone do not approve a new build's native layout.
