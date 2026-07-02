# 19 — Crash reporting: pipeline map + route-our-crashes-to-us override

**Status:** research + override design. **Telemetry hygiene, pro-Image-Line.** Goal: when *our*
system/plugins are loaded and a crash implicates *our* (sometimes-unstable third-party) code, route
the report to **us** instead of Image-Line — so we don't spam IL support with crashes we caused.
This is **NOT** crash-hiding: pure-FL crashes are left on FL's own path untouched, the override is
active **only while our system is loaded**, and we only re-route when attribution says the crash is
ours. We never make a genuine FL bug invisible to the user.

**Method:** static Ghidra only, `program="FLEngine_x64.dll"`, image base `0x400000`. No FL runtime,
no debugger, no edits to FL. (The harness's MCP analysis tools weren't reachable this run, so the
Ghidra instance was driven directly over its local HTTP API at `127.0.0.1:8089` — same static DB,
read-only queries.)

**Address convention** (as re/17): Ghidra addr uses base `0x400000`; `RVA = ghidra − 0x400000`;
`runtime = flEngineBase + RVA`. FL build analysed: **25.2.5.5319**, internal rev **r55571**
(`"$Rev: 55571 $"`). Segments: `.text 0x401000–0x12967ff`, VMProtect VM `.avm0–.avm3
0x1b5c000–0x384adff` — **the entire crash pipeline below is in plain `.text`, outside the VM.**

---

## 0. TL;DR

- **Submission is MANUAL-ONLY. There is NO automatic crash upload in FLEngine.** No
  `MiniDumpWriteDump`, no DbgHelp/StackWalk, no `.dmp`, no WER file/dump registration, no
  Sentry/Breakpad/Crashpad, and **no crash-report URL/host anywhere** — every `image-line.com`
  string is licensing/streaming/news/buy, none is a crash endpoint. The crash path **writes a local
  `.log`** under `…\Support\Logs\Crash\` and **shows a dialog** telling the user to `Ctrl+C` the text
  and report to support by hand. The dialog's "Report Crash"-titled button (result `0x100`) only
  `ShellExecuteW`s an external helper with `/log "<path>"` (user-initiated, out-of-process) — still
  not an in-proc network POST.
- **FL is Delphi/VCL; it does NOT use `SetUnhandledExceptionFilter`** (not imported). Crash capture =
  Delphi RTL SEH personality + `try/except` wrappers around the main loop and each `TThread.Execute`.
  `UnhandledExceptionFilter` is imported but only called as the RTL last resort. One CALL_FIRST VEH
  exists (`AddVectoredExceptionHandler`) but it is a tiny FL helper, **not** the crash reporter.
- **The whole user-facing crash unit is one function: `FUN_010ebaf0` (RVA `0xCEBAF0`)** — it
  assembles the text (version, OS, **Exception**, **Callstack**, **Undo**), writes the `.log`
  (`FUN_0116c090`), and shows the dialog (`FUN_00a88850`). Callers pass in the **exception message**
  and **callstack** already built.
- **Ideal override install = a no-patch pointer swap.** FL registers its app-level crash reporter as
  a swappable Delphi `TMethod`:
  `*(*PTR_DAT_014aa6e8 + 0x1b0) = FUN_010ec110` (code), `+0x1b8 = owner` (data). Overwriting that
  **one data slot** (heap field, not code) re-routes the main fatal-exception path to our shim —
  **zero `.text` modification**, trivially reversible, and idiomatic (it's exactly how FL installs
  it), so it cannot resemble DRM/anti-tamper tampering. Recommended primary hook.
- **Attribution** (ours vs FL) = faulting address / stack frames inside our module ranges
  (FlBridge/FlClrHost/FruityLink.* + a thread-local "currently dispatching plugin X" breadcrumb).
  Ours → capture + POST to our backend (queue-to-disk on failure) + relabel the dialog to "report to
  us"; FL → call the original reporter unchanged. Ambiguous → **fail toward FL** (never suppress).

---

## 1. The crash/exception pipeline

### 1a. Exception capture (top level) — Delphi SEH, not `SetUnhandledExceptionFilter`

`SetUnhandledExceptionFilter` is **not imported**. The relevant imports are:

| Import | EXTERNAL ord | Thunk (Ghidra) | Role |
|---|---|---|---|
| `AddVectoredExceptionHandler` | `0x13f` | `0x0084a520` (RVA `0x44A520`) | One install site only (§1b). |
| `UnhandledExceptionFilter` | `0x15c` | `0x00407000` (RVA `0x7000`) | Called only from the RTL personality (below). |
| `RaiseException` | `0x14b` | — | Delphi raises via code `0xeedfade`. |
| `SetErrorMode` | `0x1ac` | — | Suppresses OS fault boxes (§1b). |
| `TerminateProcess` | `0x158` | — | Hard-exit path. |
| WinINet (`InternetOpenW`, `InternetConnectW`, `HttpOpenRequestW`, `HttpSendRequestExW`, `InternetReadFile`, `InternetOpenUrlW`, `InternetGetConnectedState`) | `0x264–0x26d` | — | **Used by licensing/streaming/news, NOT the crash path** (see §3). |

**Delphi RTL SEH personality — `FUN_004112c0` (RVA `0x112C0`).** Signature
`(PEXCEPTION_RECORD, PVOID, PCONTEXT, longlong*)`. This is the language exception/unwind dispatcher:
it walks the scope table, handles the Delphi exception code `0xeedfade`, uses `RtlUnwindEx`, and as a
**last resort** calls `UnhandledExceptionFilter(&pointers)` (gated by `DAT_01297058/59`, the
debugger/JIT-present flags) before unwinding. Because FL wraps its main loop and threads in
`try/except`, control almost never reaches `UnhandledExceptionFilter`; instead a Delphi handler
catches and calls the crash reporter (§1c). The Delphi exception object getter callbacks are
`DAT_014ad020` / `DAT_014ad028`.

**The only VEH (`FUN_0084a530`, RVA `0x44A530`)** is a 6-line FL helper, *not* the crash reporter:

```c
undefined8 FUN_0084a530(EXCEPTION_POINTERS *p) {
  if (p->ExceptionRecord->ExceptionCode == 0xC0000005 /*ACCESS_VIOLATION*/ &&
      p->ExceptionRecord->ExceptionInformation[1] == 0 /*fault addr == NULL*/)
    ExitProcess(0);              // clean exit on a null-deref
  return 0;                      // EXCEPTION_CONTINUE_SEARCH for everything else
}
```

It returns `CONTINUE_SEARCH` for ordinary crashes, so it does **not** interfere with the SEH-based
reporter. (re/17 attributed `AddVectoredExceptionHandler` to VMProtect; the single import xref is
actually this FL helper. VMProtect's own VEH/anti-tamper, if registered, is set up inside the
`.avm` VM and is not visible as a normal import xref — treat it as present-but-unmapped, §5.)

### 1b. VEH + error-mode install — `FUN_0084a560` (RVA `0x44A560`)

```c
void FUN_0084a560(char enableVeh) {
  UINT m = SetErrorMode(0);
  SetErrorMode(m | 0x8003);     // SEM_FAILCRITICALERRORS|NOGPFAULTERRORBOX|NOOPENFILEERRORBOX
  if (enableVeh) AddVectoredExceptionHandler(1 /*CALL_FIRST*/, FUN_0084a530);
}
```

Called from `FUN_0084ca80` (RVA `0x44CA80`), reached via `TBaseAudioPlugin` setup. `SetErrorMode`
is also re-applied in main init (`SetErrorMode(4)` in `FUN_010b8240`). Net effect: FL suppresses the
Windows GPF error box and relies on its own dialog.

### 1c. The crash-reporter dispatch and its registration

**App-level canonical reporter — `FUN_010ec110` (RVA `0xCEC110`):**

```c
void FUN_010ec110(owner, _, exceptionObj) {
  FUN_0091d6e0(&cs, exceptionObj);                      // build Callstack text
  FUN_010ebaf0(owner, *(exceptionObj+8) /*msg*/, cs);   // -> write log + show dialog
}
```

**Registered as a swappable Delphi `TMethod`** in main-form init `FUN_010b8240` (RVA `0xCB8240`,
the "BeforeRunning" routine):

```c
lVar2 = *(longlong*)PTR_DAT_014aa6e8;     // global app-controller object
*(longlong*)(lVar2 + 0x1b8) = DAT_01581200;   // TMethod.Data = main form (owner)
*(code**)  (lVar2 + 0x1b0) = FUN_010ec110;    // TMethod.Code = the crash reporter  <<< swap target
```

`PTR_DAT_014aa6e8` is at Ghidra `0x014aa6e8` (RVA `0x10AA6E8`); it points to the live app-controller
object whose `+0x1b0/+0x1b8` slot is the crash-report callback. **This slot is the no-patch override
point** (§4). (Many other UI event handlers live in the same object, e.g. `FormHelp` at `+0x210`.)

**Subsystem handlers that call the dialog directly** (each builds the callstack via `FUN_0091d6e0`
then calls `FUN_010ebaf0`). These do **not** go through the `+0x1b0` pointer:

| Func | RVA | Context (from nearby strings) |
|---|---|---|
| `FUN_00b237a0` | `0x7237A0` | Browser cache thread ("Browser cache failed") |
| `FUN_00b244f0` | `0x7244F0` | thread/worker |
| `FUN_00b24640` | `0x724640` | thread/worker |
| `FUN_00b25750` | `0x725750` | thread/worker |
| `FUN_00c4f9c0` | `0x84F9C0` | worker |
| `FUN_00daa490` | `0x9AA490` | "WaveformCacheProcessor crashed" |
| `FUN_00fc1690` | `0xBC1690` | worker |
| `FUN_0107dc50` | `0xC7DC50` | worker |
| `FUN_010ec110` | `0xCEC110` | the registered app-level one |
| `FUN_01218480` | `0xE18480` | worker |

**Per-thread crash handler — `FUN_00b12690` (RVA `0x712690`):** logs `"Thread crashed: " + message`
via FL's logger and **terminates the thread** (sets state, fires callbacks). It does **not** raise
the full crash dialog — background-thread crashes are logged and the thread is disabled, not escalated
to the user dialog. (Audio-thread plugin faults follow a similar graceful path —
`"The plugin |%s| (%s) crashed during processing and was disabled"` @ `0xc2f5c4`.)

### 1d. The callstack builder (reusable input) — `FUN_0091d6e0` (RVA `0x51D6E0`)

```c
FUN_0091d6e0(out, exceptionObj) {
  (*vtbl_0x90)(DAT_0151e120);       // flush/finalize capture state
  FUN_00448f50(exceptionObj, out);  // copy/format the callstack text held on the exception object
}
```

The callstack is **already captured on the Delphi exception object** (custom Delphi stack-walk; no
DbgHelp). So at the reporter entry we get the **formatted callstack string for free** — directly
reusable by our override.

---

## 2. The crash-log writer + dialog (`FUN_010ebaf0`, RVA `0xCEBAF0`)

This single function is the universal "fatal crash → tell the user + save log" unit. Decompiled
essentials:

- **Format string @ `0x010ebe6c` (RVA `0xCEBE6C`):**
  `"FL Studio (%s) [%s] r%s has encountered an error and needs to restart.\r\n\r\nCrash log saved
  to %s folder.\r\n\r\nPlease use %s to copy this message and report to support.\r\n\r\nOS
  Version:\r\n%s\r\n\r\nException:\r\n%s\r\n\r\nCallstack:\r\n%s\r\n\r\nUndo:\r\n%s\r\n\r\n"`
  — **byte-for-byte the real dialog this session.**
- **Inputs / fields gathered** (`%s` order): app version (`PTR_DAT_014abca8`), edition `[%s]`,
  build `r%s` from `"$Rev: 55571 $"`, crash folder (`FUN_0116bfc0`), copy-hotkey "Ctrl+C"
  (`FUN_008194b0(0x43,4)` → `0x43`='C'), OS version (`PTR_DAT_014abc38+8`), **Exception** =
  `param_2` (caller-supplied), **Callstack** = `param_3` (caller-supplied), **Undo** history
  (`FUN_010eb9c0`).
- **Writes the `.log`** via `FUN_0116c090(&msgOut, fullText)` at site `0x010ebd2c` (its *only*
  caller). Then formats `"Error|%s"`, sets dialog title `"Report Crash"` (`0x010ec0cc`).
- **Shows the dialog** `FUN_00a88850(body, 0x10, &title, 0)` (RVA `0x688850`, FL's themed message
  box, not `MessageBoxW`). If it returns `0x100` → `FUN_00dc37a0(1, "/log \"%s\"", 0)`.

**Crash-log file writer — `FUN_0116c090` (RVA `0xD6C090`):**

```c
FUN_0116c090(out, text) {
  if (DAT_015819c8 == 0) {                 // write-once-per-session guard
    DAT_015819c8 = 1;
    FUN_0116bfc0(&folder);                 // ...\Support\Logs\Crash\
    FUN_00443f90(&ts, L"yyyy-mm-dd_hhmmss", now, ...);   // timestamp
    ... build "<folder>\Crash <ts>.log"; FLproj_AutoIncrementFileName(out, name);
    obj = FUN_00510130(&PTR_FUN_004cd480, 1);  // a writer object
    (*obj.vtbl+0x60)(obj, text);               // set contents = the crash text
    (*obj.vtbl+0x108)(obj, out, enc);          // write file to disk
  }
}
```

Purely a local file write. **No network.**

**Crash-folder path builder — `FUN_0116bfc0` (RVA `0xD6BFC0`):** takes FL's support/data dir
(`*(*PTR_DAT_014abca8 + 0x30)`), appends `"Crash\\"` (`0x0116c074`), creates it if missing →
`…\Image-Line\FL Studio\Support\Logs\Crash\` (matches the session dialog exactly; `"Support\\Logs"`
base @ `0x0091f5e8`).

**The "Report Crash" button — `FUN_00dc37a0` (RVA `0x9C37A0`):** a generic launcher. For the crash
case (`param3 & 1 == 0`) it resolves program entry #1 and does
`ShellExecuteW(NULL,"open",program,"/log \"<logpath>\"",…)` — i.e., **launches an external helper
out-of-process with the log path**, user-initiated. This is the closest thing to "submission," and
it is still not an in-proc silent upload.

---

## 3. Auto-upload? — **No (manual-only).** Evidence

| Signal | Result |
|---|---|
| `MiniDumpWriteDump` / DbgHelp / `StackWalk` / `SymFromAddr` imports | **none** (custom Delphi stack-walk instead) |
| `.dmp` minidump strings | none (only `.DMPATCH` section name, unrelated) |
| WER (`WerRegisterFile`/`WerRegisterMemoryBlock`/`ReportFault`) | not imported |
| Sentry / Breakpad / Crashpad / "telemetry" / "crashreport" / "submit" strings | **none** |
| Crash-report URL or host | **none** — all `image-line.com` strings are licensing (`regfile_unlock.php`, `action/license/serial`, `fl-auth`, `regcode.php`), streaming (`streamer.image-line.com`), news/version XML, content lib, buy-now |
| "upload" strings | all SoundCloud/YouTube **export**, not crash |
| Network in the crash functions (`FUN_010ebaf0`/`FUN_010ec110`/cluster) | none — they call only string-format, file-write, dialog, ShellExecute |

**Conclusion:** within FLEngine the crash report is **written locally and shown to the user**; the
user manually copies it (Ctrl+C) and reports to IL support, or clicks the "Report Crash" button which
shells an external helper with the log file. **There is no automatic in-process submission to
Image-Line to intercept.** (Caveat: a *separate* IL process/updater invoked via the `/log` helper
could upload — that is out of FLEngine's scope and unconfirmed; FL64.exe was not analysed here.)

This reframes "suppress Image-Line submission" for our design: there is **no silent upload to
block**. "Routing to us" means (a) sending the report to *our* backend, and (b) changing the
**user-facing messaging** so the user is directed to report *our* crash to *us*, not IL — without
altering FL's local logging or its restart/cleanup.

---

## 4. Override design — route our crashes to us

### 4.1 Where to install (recommended: no-patch pointer swap)

**Primary hook — swap the registered crash-reporter `TMethod`** (§1c):

- At our bootstrap, after our modules are loaded and FL's main form is up, read
  `obj = *(void**)resolve(0x10AA6E8)` (`PTR_DAT_014aa6e8`). Save the original pair
  `code = *(void**)(obj+0x1b0)`, `data = *(void**)(obj+0x1b8)`. Write `obj+0x1b0 = &OurReporterShim`,
  `obj+0x1b8 = <our context>` (or keep FL's `data` and just wrap `code`).
- **Why this is the right primitive:** it modifies a single **heap/data field**, not code — so it
  **cannot trip code-integrity/Authenticode** checks (re/17 §1f: the signature self-check is in
  FL64.exe and may sweep `.text`; a data-slot write is invisible to it). It is **idiomatic** (FL
  itself sets this slot at runtime), so it does not resemble DRM tampering. It is **trivially
  reversible** (restore the saved pair) for clean teardown. It catches the **app/UI-thread fatal
  path**, where plugin-induced crashes overwhelmingly surface.

**Secondary (only if full coverage is needed) — intercept `FUN_010ebaf0` (RVA `0xCEBAF0`).** Every
path (including the subsystem direct-callers in §1c) funnels through this one function, but it is
reached by direct `CALL`s, so catching it requires either an **inline prologue detour** (the
technique `tools/bridge` already uses for the chat-tab hooks) or trapping its callers. Prefer to
**avoid** patching `.text` here because FL64.exe may checksum FLEngine `.text`. If used, keep it
minimal, attribution-gated, and far-from-DRM (it is in `.text` at `0xCEBAF0`, nowhere near the DRM
clusters at `0x48xxxx`/`0x5Exxxx` or the `.avm` VM, so it won't be confused with DRM tampering — the
only real risk is a generic `.text` checksum).

**Capture aid — our own VEH (capture only, never decide).** Optionally
`AddVectoredExceptionHandler(0 /*CALL_LAST*/, OurVeh)` to snapshot the faulting `ContextRecord` +
`ExceptionAddress` + a quick module-attribution result into a **thread-local "last fatal context"**
buffer. `OurVeh` **always returns `EXCEPTION_CONTINUE_SEARCH`** and never writes the record/context,
so it is transparent. Register **CALL_LAST** so we sit behind FL's/VMProtect's CALL_FIRST handlers
and don't reorder them. Do **not** use a VEH to *decide* suppression: VEH only ever sees
**first-chance** exceptions, and Delphi raises `0xeedfade` for *every* handled exception — acting in
the VEH would fire on benign, caught exceptions. The decision belongs at the reporter shim (§4.3),
which only runs once FL has already classified the exception as fatal.

### 4.2 Attribution — is this crash ours?

We must distinguish crashes implicating **our** modules from pure-FL crashes. Inputs available at the
shim: `exceptionObj` (→ message at `+8`, callstack via `FUN_0091d6e0`/already-captured frames) and our
VEH-captured `ContextRecord`.

1. **Native module ranges.** Enumerate `[base, base+size)` for our native modules — `FlBridge.dll`,
   `FlClrHost.dll`, and (best-effort) `coreclr.dll`/`clrjit.dll` — via `GetModuleHandleExW` +
   `GetModuleInformation` at load (the bridge `info` command already reports module base/size; extend
   it to our set). A faulting address or any return-address frame inside these ranges ⇒ **ours**.
2. **Managed/JIT frames.** JITed managed code is **not** in a named module range, so pure-managed
   plugin faults won't match (1). Backstop: a **thread-local breadcrumb** — our dispatcher sets
   `tls.activePlugin = <id>` (and a depth counter) around *every* call into plugin/managed code and
   clears it on return. If a fatal exception occurs while the breadcrumb is set on the faulting
   thread ⇒ **ours** (and we know *which* plugin). This is the most reliable and crash-safe signal;
   it needs no stack walk and survives heap corruption.
3. **Corroboration / default.** Treat as ours if (1) OR (2). If neither matches and the callstack
   shows only FL frames ⇒ **pure-FL**. **Ambiguous ⇒ treat as FL** (do not suppress).

### 4.3 Policy + control flow at the shim

`OurReporterShim(owner, p2, exceptionObj)`:

```
guard reentrancy (tls.inOurCrashHandler); if set -> tail-call original, return
attribution = classify(exceptionObj, tls.activePlugin, ourModuleRanges)   // crash-safe, no alloc
if attribution == OURS (high confidence):
    report = { fl_msg = *(exceptionObj+8),
               fl_callstack = FUN_0091d6e0(exceptionObj),   // reuse FL's text
               plugin_id = tls.activePlugin, our_module_bases, fl_build="25.2.5.5319/r55571" }
    enqueue_to_disk(report)          // crash-safe; see 4.4
    try_async_post(report)           // best-effort; never block/raise here
    show_our_dialog(report)          // "<plugin> crashed; report sent to us", NOT "report to IL"
    // still let FL keep its own local log + restart cleanly:
    // either call original reporter with a flag to skip its dialog, or just let FL log and
    // replace only the user-facing text. Do NOT alter FL's post-dialog cleanup/restart flow.
else:   // pure-FL or ambiguous
    call original FUN_010ec110(owner, p2, exceptionObj)      // FL's normal path, untouched
```

**On the "suppress Image-Line submission" requirement:** since there is no auto-upload (§3), there is
no network call to cancel. "Suppression" = we **do not present FL's "…report to support" dialog** for
*our* crashes; we present our own. The safe, minimal MVP: always **capture+POST when ours**, and only
**replace the dialog text** (relabel "report to support" → "report to us / handled") when attribution
is high-confidence. We keep FL's local `.log` write and its restart path intact, so even if our
re-route is wrong, FL behaves exactly as before and the user still sees that a crash occurred.

**Return-value / unwind discipline.** We are *not* on the `SetUnhandledExceptionFilter` or
`EXCEPTION_EXECUTE_HANDLER` vs `CONTINUE_SEARCH` path here — the shim runs **after** FL's `try/except`
has already caught and decided "fatal," on a normal (non-unwinding) call stack. So our shim is just a
normal function call: it must return/continue exactly like `FUN_010ec110` would (let FL finish
restart/cleanup). The only place return codes matter is our optional capture-VEH, which **always
returns `EXCEPTION_CONTINUE_SEARCH`** (never `EXECUTE_HANDLER`, never `CONTINUE_EXECUTION`) so it can
neither swallow a crash nor resume into a corrupt state.

### 4.4 Capture + send (crash-safe)

- **Reuse FL's text** (message + callstack) — free at the shim — plus our context (active plugin id,
  our module bases, FL build). Optionally also read the just-written FL `.log`.
- **Do NOT run the CLR / `HttpClient` / managed code at crash time** (the runtime may be the thing
  that's broken; allocation may fault on a corrupt heap). Instead:
  1. **Queue-to-disk first**: write the report to `%LOCALAPPDATA%\FruityLink\crash-queue\*.json` using
     pre-allocated buffers and raw Win32 file APIs. This survives a process death mid-handler.
  2. **Upload out-of-band**: a separate normal-priority uploader (our host watchdog, or next launch /
     a tiny native WinHTTP POST) drains the queue to `marketing/api` (the backend already accepts
     crash POSTs). Retry/backoff lives there, not in the handler.
- Keep the in-handler work to: classify, copy a few strings defensively, write one queue file, show
  one dialog. No locks that could be held by the crashed thread.

### 4.5 Active only while loaded (install / teardown)

- **Install** the pointer swap (+ optional capture-VEH + breadcrumb instrumentation) from our
  proxy/CLR bootstrap (re/15, integration-pending-proxy), after our modules + FL's main form exist.
  Gate on the same conditions as the rest of our stack (proxy present, `FRUITYLINK_DISABLE` honored).
- **Teardown**: restore the saved `+0x1b0/+0x1b8` pair, `RemoveVectoredExceptionHandler(our)`, clear
  the breadcrumb hooks. CoreCLR can't be unloaded (integration-pending-proxy §d), but the **override
  must be fully removable** so that when our plugin system is disabled FL's crash path is byte-for-byte
  the original. Re-validate the slot still holds our shim before restoring (don't clobber if FL or
  another actor changed it).

---

## 5. Risks / interactions

1. **VMProtect anti-tamper / not looking like tampering.** Our **primary** hook writes a **data
   field** (`obj+0x1b0`), not code — invisible to `.text`/Authenticode checksums (the signature check
   is in FL64.exe per re/17 §1f and was not analysed here). The **capture-VEH** is read-only and
   `CONTINUE_SEARCH`-only, registered **CALL_LAST** so it never reorders FL's CALL_FIRST VEH
   (`FUN_0084a530`) or any VMProtect VEH. **Avoid the secondary inline `.text` detour on
   `FUN_010ebaf0` unless necessary**, because a generic FLEngine `.text` checksum (if FL64 does one)
   would see it; if used, it is at `0xCEBAF0`, far from the DRM clusters/`.avm`, so it won't be
   mistaken for DRM tampering specifically — the risk is integrity, not DRM. Never touch the DRM/VEH
   regions in re/17.
2. **Reentrancy / secondary crashes.** The handler must be minimal and crash-safe: thread-local
   `inOurCrashHandler` guard (re-entry ⇒ fall straight to FL's original), pre-allocated buffers,
   defensive string copies (heap may be corrupt), wrap our own body in SEH so that if *we* fault we
   fall through to FL's path. No CLR/HTTP/locks in-handler (§4.4).
3. **64-bit SEH/VEH specifics.** x64 uses table-based (`.pdata`) unwinding; VEHs run via
   `RtlpCallVectoredHandlers` before the SEH search. Our shim runs on a **normal** call stack (post-
   catch), which is the safe place for non-trivial work; our capture-VEH must be leaf-simple. Don't
   add `.pdata` entries to FL; our trampoline (if any) must be unwind-correct or non-calling.
4. **Never hide a real FL bug.** Default to FL's path; only re-route/relabel on **high-confidence
   "ours"** (module-range hit or active-plugin breadcrumb). Always keep FL's local `.log` and its
   restart, and always show the user *something* indicating a crash. Pure-FL crashes: **zero change**.
   Off (our system not loaded / disabled): the slot is FL's original, behaviour is stock.
5. **Coverage gaps (honest).** The data-slot swap covers the app-level reporter, not the subsystem
   direct-callers (browser/waveform/some worker threads, §1c) nor the thread-crash logger
   (`FUN_00b12690`, which doesn't dialog anyway). Most plugin crashes surface on the main/UI thread
   (plugin UI/param/processing-on-main) → covered. Audio-thread plugin faults are already handled by
   FL's "plugin crashed during processing and was disabled" path (we can additionally observe those
   via the breadcrumb). If we later need the worker-thread dialogs too, add the `FUN_010ebaf0`
   interception (§4.1 secondary) behind the same attribution gate.

---

## 6. Key addresses (quick reference)

| Item | Ghidra | RVA |
|---|---|---|
| Crash UI+log unit (build text, write log, show dialog) `FUN_010ebaf0` | `0x010ebaf0` | `0xCEBAF0` |
| Crash dialog format string | `0x010ebe6c` | `0xCEBE6C` |
| App-level reporter `FUN_010ec110` (callstack+dialog) | `0x010ec110` | `0xCEC110` |
| **Crash-reporter TMethod registration** (swap target) in `FUN_010b8240` | code `*(*0x14aa6e8+0x1b0)`, data `+0x1b8` | global ptr RVA `0x10AA6E8` |
| Callstack text builder `FUN_0091d6e0` | `0x0091d6e0` | `0x51D6E0` |
| Crash-log file writer `FUN_0116c090` (write-once `DAT_015819c8`) | `0x0116c090` | `0xD6C090` |
| Crash folder builder `FUN_0116bfc0` (`…\Logs\Crash\`) | `0x0116bfc0` | `0xD6BFC0` |
| Themed crash dialog `FUN_00a88850` | `0x00a88850` | `0x688850` |
| "Report Crash" launcher `FUN_00dc37a0` (ShellExecute `/log`) | `0x00dc37a0` | `0x9C37A0` |
| FL VEH callback `FUN_0084a530` (null-deref→ExitProcess) | `0x0084a530` | `0x44A530` |
| VEH installer `FUN_0084a560` (`SetErrorMode 0x8003`+`AddVectoredExceptionHandler`) | `0x0084a560` | `0x44A560` |
| Delphi RTL SEH personality `FUN_004112c0` (calls `UnhandledExceptionFilter`) | `0x004112c0` | `0x112C0` |
| Thread-crash logger `FUN_00b12690` ("Thread crashed:") | `0x00b12690` | `0x712690` |
| `AddVectoredExceptionHandler` thunk / `UnhandledExceptionFilter` thunk | `0x0084a520` / `0x00407000` | `0x44A520` / `0x7000` |

---

## 7. Confidence & follow-ups

1. **Manual-only / no auto-upload (high).** Corroborated by absence of dump/WER/Sentry imports &
   strings, absence of any crash URL, and the decompiled crash path doing only format+file+dialog+
   ShellExecute. Residual unknown: an external IL helper launched via `/log` could upload — lives
   outside FLEngine (FL64.exe not analysed).
2. **Pipeline shape (high).** Format string, log writer, folder builder, dialog, and the
   reporter-registration `TMethod` slot are all directly decompiled and cross-referenced.
3. **No `SetUnhandledExceptionFilter`; Delphi SEH (high).** Import table + the RTL personality
   (`FUN_004112c0`) confirm it. The lone VEH is the null-deref helper.
4. **Override install via data-slot swap (high feasibility, medium runtime-unverified).** The slot
   write is unambiguous in `FUN_010b8240`; runtime validation (resolving `*0x10AA6E8` live and
   confirming the swap takes a synthetic crash to us) is owned by the runtime sibling — do that before
   shipping.
5. **Attribution for pure-managed plugin frames (medium).** Module-range matching misses JITed
   frames; the thread-local active-plugin breadcrumb is the dependable backstop and should be the
   primary signal for managed plugins.
6. **FL64.exe follow-up.** To fully rule out an external auto-uploader and to know whether a `.text`
   checksum exists (governs whether the secondary inline hook is safe), FL64.exe needs a separate
   static pass (same TODO as re/17 §8).
