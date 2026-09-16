# Note integrity and FL's invalid-data warning

The September 2026 audit found a concrete authoring defect: `AddNotesAsync` accepted arbitrary channel indices and packed them into a 16-bit note field. FL accepts those records in memory, but its project validator identifies them as invalid when loading or saving. A note must reference an existing channel in the current project. Checking only the 16-bit storage range is insufficient.

The SDK now validates a snapshot of the complete input batch before creating a note recorder. A missing channel, invalid key or velocity, negative start, nonpositive duration, or signed 32-bit end-time overflow rejects the batch before any note is appended. Keys are 0–131, velocities 0–127, and `startTick + lengthTick` must not exceed `Int32.MaxValue`. Invalid edits are rejected before writing the edited array. Cloning an orphan note is rejected before selecting a destination pattern.

Note authoring uses one complete-record append per note. The previous recording on/off pair first wrote a sentinel duration and depended on a second call to finish the record. Its note-off function also applies recording-loop behavior. Cancellation now stops between complete-record calls; an accepted prefix is compacted and refreshed before cancellation is reported. This is not an all-or-nothing transaction, and a transport failure must not be blindly retried.

Save operations separately inspect every allocated note recorder, including patterns outside the playlist and reserved slot zero. They refuse to start a save when a note references a missing channel or the inspection cannot complete reliably. The SDK does not silently delete notes. These checks are snapshots; concurrent editing outside the serialized scripting session remains a live-state limitation.

## Binary evidence

The following addresses are preferred virtual addresses, with PE image base `0x400000`. They were verified in the installed x64 engines, without modifying them:

| Build | SHA-256 | Validator | Complete-record append | Recording note-off |
|---|---|---|---|---|
| 25.2.5.5319 | `1b7e2381f2853ee590bff74429fb8aa5540d33a5b33c0c5e41ec01c03fe0c7ac` | `E609E0` | `F6D740` | `F6D880` |
| 26.1.3.5570 | `bcba500563d5b3c78879e6c25cf872ed3674999c0d694087f2665268e8d1aeab` | `F3D9E0` | `105DF30` | `105E070` |

Both validators visit 1,000 pattern slots with stride `0xC0`, starting at the pattern array's recorder field `+0x28`. For each recorder they inspect its active count at `+0x14` and 24-byte records from its data pointer at `+8`. The invalid-note predicate is exactly `note.channel >= ChannelList.count`: channel is an unsigned 16-bit value at note `+6`, and channel count is at list `+0x10`. It does not test note ordering, pitch, or duration. Flag bit 0 deletes failing records; flag bit 1 permits an early exit.

The historic scanner alias `FLpat_RecordNoteOn` resolves a general record initializer/appender in both engines. Its arguments are recorder, start, packed flags/channel, duration, key, packed fine-pitch/release, and packed pan/velocity/filter values. It initializes the remaining fields through recorder virtual slot `+0x10`, then copies the completed record through slot `+8`. Passing the actual duration and `0x00400078` for fine-pitch/release produces fine pitch 120 and release 64 immediately. No new function signature or raw count write is required.

The former two-call sequence used duration `0x400078`, then asked the note-off routine to replace that sentinel. That routine's recording/loop branch can change the resulting duration. The complete-record path avoids both the pending record and that recording-specific behavior.

Local audit artifacts are in `artifacts/invalid-notes-2026-09-13/`: `validate2025.txt`, `validate2026.txt`, `warnings2025.txt`, `warnings2026.txt`, and `load-warning-asm2026.txt`. The complete append functions were also decompiled in `artifacts/reanalysis/ghidra2025.txt` and `ghidra2026.txt`. These generated artifacts are excluded from distribution.

## Warning semantics

The load warning has title **Invalid data** and body:

> There are invalid notes in this project. Leaving them in the project may lead to unexpected behavior and crashes.
>
> Do you want to delete these notes?

The verified 2026 load branch calls the validator with flag 2, displays the warning, and calls it again with flag 1 only when the dialog returns modal result 6 (Yes). No leaves the records present. The separate save warning says that invalid notes will be deleted when the project is saved and asks “Are you sure you want to continue?” Its No branch aborts saving; Yes deletes orphan notes in memory and permits the pending save. This save warning can also appear during startup. Recovery must distinguish these dialogs and preserve a backup before accepting deletion or allowing the pending write.

The actual FL dialog is `TMsgForm`, with custom memo and focus-button controls. Its body and button labels need not appear in standard `WM_GETTEXT` or accessibility results. Win32 child control IDs are not Delphi modal results. A recovery adapter must verify the complete message, process ownership, supported layout, and the actual answer control rather than infer an answer from its position or title alone.

The SDK's read-only `FlDialogInspector` uses these verified fields:

| Field | 25.2.5.5319 | 26.1.3.5570 |
|---|---|---|
| `TMsgForm` size | `0x860` | `0x840` |
| Form's body-control pointer | `+0x7A0` | `+0x7C8` |
| `TQuickMemo` Unicode-string pointer | `+0x624` | `+0x634` |
| `TQuickFocusBtn` inner `TQuickBtn` pointer | `+0x510` | `+0x510` |
| Inner button answer tag | `+0x18` | `+0x18` |
| Inner button callback code / receiver | `+0x1E4` / `+0x1EC` | `+0x1E4` / `+0x1EC` |
| Expected callback RVA | `0x689E60` | `0x563960` |

Windowed controls expose the object through the `ControlOfs` window property, whose name has 16 hexadecimal suffix characters. Their cached HWND is at `+0x45C`, verified by handle getters `5DDF70` and `617920`. An inspector must validate the Delphi metaclass, exact control class, cached HWND, process/parent relationship, callback address and callback receiver. The inner graphic button has no independent HWND. Its tag is 6 for Yes and 7 for No; the verified callback writes that result into the form's modal-result field. The helper reads these values and never writes memory or answers a dialog.

The supporting local decompilations are `dialogmethods2025.txt`, `dialogmethods2026.txt`, `dialogbuttons2025.txt`, `dialogbuttons2026.txt`, `dialoghwnd2025.txt`, and `dialoghwnd2026.txt` in the audit artifact directory.

## Verification limits

Managed transport regressions exercise mixed valid/invalid batches, scalar boundaries, complete record bytes, cancellation after the first append, native failure without retry, edit preservation, and rejection of orphan clones. An older installed build successfully saved and reopened an unsorted chord/edit fixture, which rules out ordinary unsorted input alone as the warning's cause. A separate missing-channel fixture reproduced the old save hang.

The installed 0.1.23 framework was tested on FL 26.1.3.5570 using a disposable project: mixed batches with channel -1, the current channel count, and 65,536 were rejected without changing its 24 existing notes. An overflowing note end was also rejected. All 24 notes, including the edited note, survived saving and reopening with their properties unchanged; the project rendered a 1,536,184-byte WAV. Local receipts are `artifacts/note-recovery-20260913/live-verification.json` and `mcp-report.json`. FL 2025 has binary and automated coverage, but this change has not been exercised live on that version.

Historical Glass Satellites files contain 5,341 byte-identical serialized notes, all within their saved 20-channel range. The earlier 5,076-note live query discrepancy is not evidence that these files contain orphan notes, and is not attributed to this defect without further evidence. No historical project was rewritten as part of this audit.
