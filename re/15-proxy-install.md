# 15 — Proxy-DLL Install Target (DLL sideloading) for FruityLink

**Goal:** replace runtime injection of `FlBridge.dll` with a *filesystem install* — drop a
proxy DLL that FL Studio loads at startup. The proxy forwards every original export to the
real DLL **and** runs our init (today: `LoadLibrary` the bridge; later: host the CLR — the
user already has a CLR loader, **out of scope here**).

**Scope note:** read-only RE of the DLL **load mechanism** only. No DRM/licensing analysis,
no edits, no build, FL not modified. This is the user's own interop extension of their own FL.

**Environment analyzed:** FL Studio 2025, `FL64.exe` / `FLEngine_x64.dll` v**25.2.5.5319**,
`C:\Program Files\Image-Line\FL Studio 2025\`. FL was running; data is from the live process
plus `dumpbin` on the on-disk PEs.

---

## TL;DR — Recommendation

**Proxy `version.dll`, placed in `C:\Program Files\Image-Line\FL Studio 2025\` (the FL64.exe /
FLEngine directory).**

Why it's the best of both worlds (easy *and* stable):

| Property | version.dll |
|---|---|
| Export surface | **15 exports**, all `GetFileVersionInfo*` / `VerQueryValue*` / `VerFindFile*` / `VerInstallFile*` |
| KnownDLL? | **No** → an app-dir copy is loaded in preference to `System32` |
| Load timing | **Statically imported by `FLEngine_x64.dll`** → loaded automatically the instant FL64 loads the engine, i.e. at startup, **every** session, unconditionally |
| Owner | Pure Microsoft Windows DLL → forward all 15 exports to `C:\Windows\System32\version.dll`. **No Image-Line file has to be renamed or replaced.** |
| FL self-verification risk | FL verifies its **own** modules (it imports WINTRUST/CRYPT32 to check `FLEngine`), not Windows system DLLs → our `version.dll` is essentially never subject to FL's Authenticode check |
| Update survival | `version.dll` is **not** an FL-shipped file, so in-place FL updates don't overwrite it. Re-drop only on a major-version folder change (e.g. "FL Studio 2026") |

Runner-up: **`msimg32.dll`** (5 exports, same class of target). Avoid `WINTRUST`/`CRYPT32`
(crypto/self-verify path) and `FLEngine_x64.dll` itself (verified + VMProtect'd).

---

## 1. Loaded-module truth (running FL64, ~200 modules)

### 1a. App-LOCAL modules (loaded from FL's own install tree) — the only legitimate proxy zone
| Module | Path | Owner / type |
|---|---|---|
| `FL64.exe` | `…\FL Studio 2025\FL64.exe` | IL shell/loader (25.2.5.5319) |
| `FLEngine_x64.dll` | `…\FL Studio 2025\FLEngine_x64.dll` | IL engine, 51 MB, VMProtect'd (25.2.5.5319) |
| `WebView2Loader.dll` | `…\FL Studio 2025\WebView2Loader.dll` | **Microsoft redist** (1.0.664.37) |
| `speaker_x64.dll`, `AudioRestore.dll`, `iZAudioRestore.dll`, `FLMManaged.dll` | `…\FL Studio 2025\` | IL |
| `Blend2D / dsp_ippv2_x64 / freetype_x64 / ildsp_x64 / ILRemoteServer_x64 / ILTools_x64 / oggio_x64 / QuickFontCache_x64 / ffmpeg\x64\*` | `…\FL Studio 2025\Shared\…` | IL + 3rd-party, in **subdirs** (not EXE dir) |
| plug-ins (`Control Surface_x64`, `Fruity Wrapper_x64`, …) | `…\Plugins\…` | loaded on demand |

Key point: only DLLs whose **unqualified name resolves through the application directory**
(= `FL64.exe`'s dir) are clean proxy targets. `Shared\…` DLLs live in a subdir and are pulled
in by full path / an added search dir, so they're not app-dir-name-hijackable; the EXE dir is.

### 1b. System modules (System32/SysWOW64 etc.)
~180 Windows DLLs, all loaded from `C:\Windows\System32` (e.g. `version.dll`, `winmm.dll`,
`dwmapi.dll`, `dbghelp.dll`, `profapi.dll`, `wintrust.dll`, `crypt32.dll`). They load from
System32 today **only because no copy exists in the app dir** — that does *not* by itself make
them KnownDLLs (see §3).

Also present (not relevant as targets): EdgeWebView `EmbeddedBrowserWebView.dll`, Defender
`MpOav.dll`/`amsi.dll`, NVIDIA driver DLLs, and **today's injected** `FlBridge_*.dll` from
`%LocalAppData%\Temp\fruitylink-bridge\`.

---

## 2. Static import tables (what loads, and when)

**`FL64.exe` imports only 5 DLLs** (it is a thin shell that then `LoadLibrary`s the engine):
`KERNEL32`, `USER32`, `ADVAPI32` (all KnownDLLs — not hijackable), **`WINTRUST`**, **`CRYPT32`**
(both non-KnownDLL, so technically app-dir-hijackable — but they are the **signature/crypto**
path FL uses to verify `FLEngine`; hijacking them is DRM-adjacent, high-risk, and CRYPT32 has a
huge export surface → **rejected**).

**`FLEngine_x64.dll` exports only 3 symbols** (`CreateFruityInstance`, `__dbk_fcall_wrapper`,
`dbkFCallWrapperAddr`) — so the engine could in principle be proxied with 3 forwarders, **but**
FL64 verifies it (WINTRUST) and it is VMProtect'd → replacing/renaming it breaks the self-check
→ **rejected**.

**`FLEngine_x64.dll` statically imports 31 DLLs** — this is where the good targets live. Because
FLEngine sits in the same directory as `FL64.exe`, its implicit imports are resolved with the
**application directory first** (after KnownDLLs), so a non-KnownDLL Windows import dropped into
the FL dir is hijacked. Classification of those imports:

| Imported by FLEngine | KnownDLL? | Exports | Proxy verdict |
|---|---|---|---|
| **version.dll** | No | **15** | **BEST** — tiny, pure-MS, forward to System32 |
| **msimg32.dll** | No | **5** (`AlphaBlend`,`GradientFill`,`TransparentBlt`,`vSetDdrawflag`,`DllInitialize`) | strong #2 |
| **SHFolder.dll** | No | **2** (`SHGetFolderPathA/W`) | tiny, but a shim that itself forwards to shell32 |
| **dwmapi.dll** | No | 44 | ok, larger surface |
| winmm.dll | No | 180 | works, big surface |
| wininet/propsys/windowscodecs/uxtheme/oleacc/msacm32/mpr/wtsapi32/dsound | No | medium–large | possible, no benefit over version |
| crypt32.dll / wintrust.dll | No | ~300 / ~20 | **avoid** (DRM/self-verify path) |
| advapi32, comdlg32, gdi32, imm32, kernel32, ole32, oleaut32, rpcrt4, Shcore, shell32, shlwapi, user32, **IMAGEHLP** | **Yes (KnownDLL)** | — | not hijackable from app dir |
| comctl32.dll | WinSxS/manifest | — | redirected via SxS; not a clean name hijack |
| api-ms-win-crt-string-l1-1-0.dll | API set | — | apiset-redirected; not a real file target |

KnownDLLs were confirmed authoritatively from
`HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs`. `version`, `winmm`, `dwmapi`,
`msimg32`, `dbghelp`, `profapi`, `wintrust`, `crypt32`, `SHFolder` are **not** on that list →
app-dir-hijackable. `advapi32`, `gdi32`, `imm32`, `ole32`, `oleaut32`, `rpcrt4`, `shell32`,
`shlwapi`, `shcore`, `comdlg32`, `IMAGEHLP`, `kernel32`, `user32` **are** KnownDLLs → not.

### Classic sideload targets — status here
- `version.dll`, `winmm.dll`, `dwmapi.dll`, `msimg32.dll` — **viable**: non-KnownDLL **and**
  statically imported by FLEngine, so dropping them in the FL dir hijacks the engine's load.
- `dbghelp.dll`, `profapi.dll` — non-KnownDLL but **not imported** by FL64/FLEngine statically,
  so they would never load from the app dir → **not** targets here.
- `IMAGEHLP.dll` — imported by FLEngine but it **is** a KnownDLL → not hijackable from app dir.

---

## 3. Candidate comparison (ranked)

| Rank | DLL | Place in | Exports | Forward to | Load timing | Renames an IL file? | FL update overwrites? | Notes |
|---|---|---|---|---|---|---|---|---|
| **1** | **version.dll** | FL EXE dir | **15** | `System32\version.dll` | startup (engine static import) | No | No (not an FL file) | textbook sideload target; smallest standard surface |
| 2 | msimg32.dll | FL EXE dir | 5 | `System32\msimg32.dll` | startup (engine static import) | No | No | even fewer exports; GDI blend API |
| 3 | dwmapi.dll | FL EXE dir | 44 | `System32\dwmapi.dll` | startup (engine static import) | No | No | larger forwarder |
| 4 | WebView2Loader.dll | already in FL EXE dir | 4 | renamed real copy | **lazy** (only when WebView2 UI opens) | yes (rename IL's copy) | **Yes** (FL ships it) | late + conditional + clobbered on update → weak |
| — | FLEngine_x64.dll | — | 3 | — | startup | yes | yes | **rejected**: verified + VMProtect'd |
| — | WINTRUST / CRYPT32 | FL EXE dir | ~20 / ~300 | System32 | earliest (FL64 import) | No | No | **rejected**: DRM/self-verify path, sensitive |

---

## 4. Recommended implementation — `version.dll` proxy

**Files to place in `C:\Program Files\Image-Line\FL Studio 2025\`:**
- `version.dll` — our proxy (exports the 15 names below).
- *(if using static forwarding)* `version_orig.dll` — a verbatim copy of
  `C:\Windows\System32\version.dll` that the forwarders point at.

**The 15 exports to expose / forward:**
```
GetFileVersionInfoA            GetFileVersionInfoExA          GetFileVersionInfoSizeA
GetFileVersionInfoByHandle     GetFileVersionInfoExW          GetFileVersionInfoSizeExA
GetFileVersionInfoW            GetFileVersionInfoSizeExW      GetFileVersionInfoSizeW
VerFindFileA   VerFindFileW    VerInstallFileA  VerInstallFileW
VerQueryValueA VerQueryValueW
```

**Forwarding approach — pick one:**

- **(A) Static export forwarding (.def), simplest, zero forward code.** In the `.def`:
  ```
  EXPORTS
    GetFileVersionInfoW=version_orig.GetFileVersionInfoW
    VerQueryValueW=version_orig.VerQueryValueW
    ... (all 15)
  ```
  Caveat: a forwarder string resolves its target module **through the normal search order**, so
  you must forward to a **differently named** module (`version_orig`), *not* back to `version`
  (that re-finds our proxy → infinite loop). Hence the renamed System32 copy. Tools like
  Spartacus / SharpDllProxy / DLLirant auto-generate this `.def` + stubs.

- **(B) Runtime forwarding (no extra file).** In `DllMain(DLL_PROCESS_ATTACH)`:
  `h = LoadLibraryW(L"C:\\Windows\\System32\\version.dll")` (absolute path → no loop), cache the
  15 `GetProcAddress` results, and have each exported stub jump through. More code, but only the
  one proxy file needs to ship.

**Where our init runs (and the loader-lock rule):**
`DllMain` runs under the **loader lock** — do **not** `LoadLibrary` the bridge or boot the CLR
inside it (deadlock risk). Instead, from `DLL_PROCESS_ATTACH` either (a) `CreateThread` a worker
(its body only runs after the lock releases) or (b) defer to the first call of a forwarded export
(e.g. first `GetFileVersionInfoW`). The worker then does the heavy lifting off the lock:
`LoadLibrary` our bridge → (future) host the CLR via the existing loader. Wrap it in try/catch so
a failure never takes FL down.

---

## 5. Risks / things to verify

1. **Search-order hardening (main thing to verify).** If `FL64.exe` loads `FLEngine` with
   `LoadLibraryEx(..., LOAD_LIBRARY_SEARCH_SYSTEM32)` or calls `SetDefaultDllDirectories`
   excluding the app dir, the engine's dependent `version.dll` could be forced from System32,
   defeating the hijack. The live module list (version.dll from System32) only reflects "no
   app-dir copy exists," not hardening. **Verify** by dropping a stub `version.dll` that just
   writes a marker file in `DllMain` and forwards — confirm it loads. (Couldn't test now;
   read-only.)
2. **KnownDLLs** — confirmed `version` is **not** a KnownDLL → app-dir wins. (No action.)
3. **FL self-verification / Authenticode.** FL imports WINTRUST/CRYPT32 and verifies its own
   modules (`FLEngine`). It almost certainly does **not** verify Windows system DLLs like
   `version.dll`, so our proxy isn't checked — this is the key advantage over proxying
   `FLEngine`/`wintrust`. Worth a quick confirm that FL has no "verify every DLL in my folder"
   sweep; if it ever did, an unsigned `version.dll` could be flagged.
4. **DllMain loader lock** — keep `DllMain` minimal; do bridge/CLR work on a worker thread (§4).
5. **FL update overwrite** — `version.dll` isn't an FL-shipped file, so in-place updates leave it
   alone. A new major-version install dir (e.g. `…\FL Studio 2026\`) means re-dropping the proxy.
   (`WebView2Loader.dll`, by contrast, *is* shipped by FL and would be clobbered — another reason
   it ranks lower.)
6. **Crash isolation** — any exception in our init must be swallowed so FL startup is unaffected.

---

## 6. Out of scope (future / separate)

CLR hosting and loading `FlBridge.dll` are deliberately **not** designed here — the user already
has a CLR loader. This note only selects the **DLL load vector** (the proxy target + placement +
forwarding + timing). Once `version.dll` is confirmed to load (risk #1), the proxy's worker
thread simply invokes the existing bridge/CLR bootstrap.
