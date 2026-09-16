// FlBridge.dll — FruityLink injected dev bridge (v2: read + main-thread call).
//
// v1 was read-only (pipe + SEH peek). v2 adds the ability to CALL functions on FL's
// main/UI thread, which is required because most engine routines are not thread-safe.
//
// Main-thread dispatch: we subclass FL's main top-level window and SendMessage a private
// message; SendMessage runs the window proc on the owning (main) thread synchronously, so
// our handler executes there. The subclass is reverted on BridgeStop()/DETACH, preserving
// the clean-unload contract (re/03-roadmap Phase 2.5). Every call is SEH-guarded.
//
// Pipe \\.\pipe\FruityLinkBridge — UTF-8, one message in / one out:
//   ping | info
//   peek <ghidraHex> <len> | peekabs <hex> <len>
//   tid                         -> {"main":<tid>,"worker":<tid>}
//   apctest                     -> {"ret":<tid>,"main":<tid>}  (GetCurrentThreadId on main thread)
//   call    <ghidraHex> [hexArg ...]   call FLEngine+(addr-0x400000) on the MAIN thread (<=4 int args) -> RAX hex
//   callabs <hex> [hexArg ...]         call absolute addr on the MAIN thread
//   callhere <hex> [hexArg ...]        call on the WORKER thread (only for thread-safe fns)
//   callf  <ghidraHex> [argBits ...]   like call, but loads the first 4 args into BOTH GP and XMM regs
//   callfabs <hex> [argBits ...]       like callabs, XMM-capable -> {"ret":RAX,"xmm0":<8-byte XMM0>} (for
//                                      float-arg / float-return engine fns; args are raw hex bit patterns)
//   shutdown                    -> bye

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <psapi.h>
#include <stdio.h>
#include <stdarg.h>
#include <string>
#include <vector>
#include <atomic>
#include <cstdlib>
#include <climits>
#include <memory>

#include "sigscan.h"
#include "fl_window_factory.h"
#include "mixer_track_insertion.h"
#include "automation_clip.h"
#include "delphi_string.h"
#include "version_scanner.h"   // runtime signature-scanning subsystem (FL version portability; additive)

#pragma comment(lib, "psapi.lib")
#pragma comment(lib, "user32.lib")

// ---- DEBUG-pipe gating ----
// The EXTERNAL named-pipe server (\\.\pipe\FruityLinkBridge) and the flprobe debug plugin commands
// (plugins_dir / plugin_enable / plugin_disable / plugin_reload) are compiled in ONLY for debug
// builds, so a Release/production install exposes NO debug pipe. The IN-PROCESS command surface
// (FlBridge_Command, used by the app/host + the Tools menu) is always present. Enable via a Debug
// CMake config (_DEBUG) or by passing -DFRUITYLINK_DEBUG=ON (adds FRUITYLINK_DEBUG) to the bridge.
#if defined(_DEBUG) || defined(FRUITYLINK_DEBUG)
#  define FRUITYLINK_DEBUG_PIPE 1
#endif

static HANDLE g_thread = NULL, g_stop = NULL;
static const unsigned long long GHIDRA_BASE = 0x400000ULL;

// Scratch buffer for engine calls that take out-pointer params (e.g. resolve-by-index).
static unsigned char g_scratch[1024];

// ---- main-thread dispatch state ----
static const UINT WM_BRIDGE_CALL      = WM_APP + 0x1BD;
static const UINT WM_BRIDGE_CHATOPEN  = WM_APP + 0x1BE;
static const UINT WM_BRIDGE_CHATCLOSE = WM_APP + 0x1BF;
static const UINT WM_BRIDGE_CHATSAY   = WM_APP + 0x1C0;
static const UINT WM_BRIDGE_CHATPOLL  = WM_APP + 0x1C1;
static const UINT WM_BRIDGE_PLUGINSINSTALL = WM_APP + 0x1C2;
static const UINT WM_BRIDGE_PLUGINSREMOVE  = WM_APP + 0x1C3;
static const UINT WM_BRIDGE_MENUCONTRIBINSTALL = WM_APP + 0x1C4; // (re)materialize plugin menu contributions
static const UINT WM_BRIDGE_MENUCONTRIBREMOVE  = WM_APP + 0x1C5; // eject-safe removal of contributed items
static const UINT WM_BRIDGE_WINHOST_EMBED      = WM_APP + 0x1C6; // reparent our child window into an FL host form
static const UINT WM_BRIDGE_WINHOST_SHOW       = WM_APP + 0x1C7; // show/hide the FL host form (View toggle)
static const UINT WM_BRIDGE_WINHOST_CLOSE      = WM_APP + 0x1C8; // detach our child + hide the host form
static const UINT WM_BRIDGE_WINHOST_MIN        = WM_APP + 0x1C9; // minimize the FL host form (like a plugin window)
static const UINT WM_BRIDGE_WINHOST_MAX        = WM_APP + 0x1CA; // toggle maximize/restore the FL host form
static const UINT WM_BRIDGE_WINHOST_DOCK       = WM_APP + 0x1CB; // toggle dock/float the FL host form
static const UINT WM_BRIDGE_TOOLBARINSTALL     = WM_APP + 0x1CC; // (re)materialize plugin toolbar toggle buttons
static const UINT WM_BRIDGE_TOOLBARREMOVE      = WM_APP + 0x1CD; // eject-safe removal of contributed toolbar buttons
static const UINT WM_BRIDGE_MIXERADD           = WM_APP + 0x1CE; // atomic native mixer insertion
static const UINT WM_BRIDGE_AUTOMATION         = WM_APP + 0x1CF;
static std::atomic<HWND>    g_mainWnd{NULL};
static std::atomic<WNDPROC> g_origProc{NULL};
static SRWLOCK  g_subclassLock = SRWLOCK_INIT;

// ---- in-FL chat tab (re/14): native browser tab + content-switch vtbl[0xd0] hook ----
static void*  g_browser          = NULL; // *DAT_0157ffb8 (main TVirtualDataBrowser)
static void*  g_ourTab           = NULL; // our cloned tab slot (identity by POINTER; ids reshuffle)
static int    g_ourTabId         = 0;
static void*  g_inputCtrl        = NULL; // TQuickEdit (multi-line input; Enter inserts \n → poll auto-submits)
static void*  g_displayCtrl      = NULL; // TQuickEdit (multi-line responses)
static void*  g_origContentSwitch= NULL; // saved browser vtbl[0xd0] (FLbrz_ContentSwitch)
static void** g_vtblSlot         = NULL; // &browser vtbl[0xd0]
static bool   g_chatOpenOk       = false;
// ---- chat comms (Stage C) ---- (all main-thread access via WM_BRIDGE_CHATSAY/CHATPOLL; no lock needed —
// SendMessage is synchronous so the worker reads g_pollResult only after the main-thread handler returns)
static std::string g_sayText;     // utf8 payload for chat_say (worker → main)
static std::string g_pollResult;  // utf8 message the main-thread poll handler produced (main → worker)
static bool        g_pollForce = false; // chat_submit forces submit of the whole input (ignores newline gate)
static std::string g_chatIn;      // pending submitted message (set by takeInput on the main thread; drained by chat_poll)
static void*       g_sendBtn = NULL;          // native "Send" button (submits the input on click)
static void*       g_sendBtnOrigClick = NULL; // saved button onClick TMethod code (restored on teardown)
// ---- space fix: TFruityLoopsMainForm+0x4c5 = suppress-global-shortcuts flag (FormShortCut@0x114de10 gate) ----
static void*  g_mainForm   = NULL;            // captured (guaranteed-correct) from FormShortCut's param_1
static void*  g_shortcutFn = NULL;            // rebased FormShortCut@0x114de10
static unsigned char g_scSaved[12];           // its saved prologue bytes (for the one-shot un-hook)
static bool   g_scHooked   = false;
// FormKeyDown@0x10c9920 = TFruityLoopsMainForm.FormKeyDown — the REAL space->play handler (KeyPreview).
// We replace it (return without consuming) while our chat tab is active so SPACE falls through to our edit.
static void*  g_keyDownFn  = NULL;
static unsigned char g_kdSaved[12];
static bool   g_kdHooked   = false;

// ---- Plugins menu (re/16) ----
// A "Plugins" SUBMENU added inside FL's existing "Tools" top-level dropdown (it sits among Tools'
// own items). This uses FL's native submenu rendering — no toolbar width/clipping and no custom
// popup — so it sidesteps the bar-layout + popup-render hazards. Its children are (re)built live
// from the managed plugin manager (PluginManagerLocator, via the CLR host's FlClr_GetPluginFns
// export); selecting one toggles it. Eject-safe: on teardown we clear our child onClick thunks and
// remove the "Plugins" item from Tools, on FL's main thread, before unload.
static void*  g_pluginsItem         = NULL;          // our "Plugins" submenu item (child of Tools)
static void*  g_pluginsTools        = NULL;          // the stock "Tools" menu item (our parent)
static void*  g_pluginsCtx          = (void*)0x504C5547ULL; // 'PLUG' — non-null TMethod.data for children
static bool   g_pluginsInstallOk    = false;         // result of the last main-thread install
struct PluginEntry { std::string id; std::wstring name; bool enabled; };
static std::vector<PluginEntry> g_pluginCache;       // current dropdown contents (index == item tag)
typedef int (*PluginListFn)(char*, int);             // managed PluginGlue.ListJson
typedef int (*PluginToggleFn)(const char*, int, int);// managed PluginGlue.Toggle
static PluginListFn   g_pluginListFn   = NULL;
static PluginToggleFn g_pluginToggleFn = NULL;
static std::atomic<bool> g_bridgeStopping{false};
static std::atomic<bool> g_pluginTogglePending{false};

// ---- Settings submenu (task #61) ----
// A nested "Settings" submenu inside "FL Plugins" (above the plugin list, after a separator). Its
// items toggle managed settings resolved from the CLR host via FlClr_GetSettingsFns. Currently one
// row: a checkable "Show Debug Output" that drives PluginGlue.SetDebugVisible (the diagnostic window
// is hidden by default). Adding more settings is a single row in g_settings (below).
static void* g_settingsItem = NULL;                  // our "Settings" submenu item (child of FL Plugins)
typedef int  (*DebugGetFn)(void);                    // managed PluginGlue.GetDebugVisible -> 0/1
typedef int  (*DebugSetFn)(int);                     // managed PluginGlue.SetDebugVisible
static DebugGetFn g_getDebugVisibleFn = NULL;
static DebugSetFn g_setDebugVisibleFn = NULL;

// XMM-capable call thunk (callthunk.asm): loads arg[0..3] into GP + XMM0-3, returns RAX, writes XMM0.
extern "C" ULONG_PTR fl_call_xmm(void* fn, ULONG_PTR* args, double* xmm0out);

struct PendingCall {
    void*       fn;
    ULONG_PTR   arg[8];
    int         argc;
    ULONG_PTR   ret;
    bool        ok;     // false if SEH-faulted
    bool        useXmm; // route through fl_call_xmm and capture XMM0
    double      xmm0;   // XMM0 return (raw bits) when useXmm
};

static void logline(const char* s)
{
    char path[MAX_PATH]; DWORD n = GetTempPathA(MAX_PATH, path);
    if (n == 0 || n > MAX_PATH - 32) return;
    strcat_s(path, MAX_PATH, "fruitylink-bridge.log");
    FILE* f = NULL; if (fopen_s(&f, path, "a") == 0 && f) { fprintf(f, "%s\n", s); fclose(f); }
}

static bool safeRead(const unsigned char* src, unsigned char* dst, size_t len)
{ __try { memcpy(dst, src, len); return true; } __except (EXCEPTION_EXECUTE_HANDLER) { return false; } }

static int hexNib(char c)
{ if (c >= '0' && c <= '9') return c - '0'; if (c >= 'a' && c <= 'f') return c - 'a' + 10; if (c >= 'A' && c <= 'F') return c - 'A' + 10; return -1; }

// SEH + VirtualProtect guarded write (for direct struct edits where no FL command exists).
static bool safeWrite(void* dst, const unsigned char* src, size_t len)
{
    DWORD oldP = 0; bool changed = VirtualProtect(dst, (SIZE_T)len, PAGE_READWRITE, &oldP) != 0;
    bool ok; __try { memcpy(dst, src, len); ok = true; } __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; }
    if (changed) { DWORD t; VirtualProtect(dst, (SIZE_T)len, oldP, &t); }
    return ok;
}

static HMODULE selfModule()
{ HMODULE h = NULL; GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCSTR)&selfModule, &h); return h; }

// SEH-guarded invoke of a function pointer with up to 4 integer args (Win64 fastcall ABI).
static ULONG_PTR invokeGuarded(void* fn, ULONG_PTR* a, int argc, bool* ok)
{
    typedef ULONG_PTR(*F0)();
    typedef ULONG_PTR(*F1)(ULONG_PTR);
    typedef ULONG_PTR(*F2)(ULONG_PTR, ULONG_PTR);
    typedef ULONG_PTR(*F3)(ULONG_PTR, ULONG_PTR, ULONG_PTR);
    typedef ULONG_PTR(*F4)(ULONG_PTR, ULONG_PTR, ULONG_PTR, ULONG_PTR);
    typedef ULONG_PTR(*F5)(ULONG_PTR, ULONG_PTR, ULONG_PTR, ULONG_PTR, ULONG_PTR);
    typedef ULONG_PTR(*F6)(ULONG_PTR, ULONG_PTR, ULONG_PTR, ULONG_PTR, ULONG_PTR, ULONG_PTR);
    typedef ULONG_PTR(*F7)(ULONG_PTR, ULONG_PTR, ULONG_PTR, ULONG_PTR, ULONG_PTR, ULONG_PTR, ULONG_PTR);
    typedef ULONG_PTR(*F8)(ULONG_PTR, ULONG_PTR, ULONG_PTR, ULONG_PTR, ULONG_PTR, ULONG_PTR, ULONG_PTR, ULONG_PTR);
    ULONG_PTR r = 0; *ok = true;
    __try {
        switch (argc) {
            case 0: r = ((F0)fn)(); break;
            case 1: r = ((F1)fn)(a[0]); break;
            case 2: r = ((F2)fn)(a[0], a[1]); break;
            case 3: r = ((F3)fn)(a[0], a[1], a[2]); break;
            case 4: r = ((F4)fn)(a[0], a[1], a[2], a[3]); break;
            case 5: r = ((F5)fn)(a[0], a[1], a[2], a[3], a[4]); break;
            case 6: r = ((F6)fn)(a[0], a[1], a[2], a[3], a[4], a[5]); break;
            case 7: r = ((F7)fn)(a[0], a[1], a[2], a[3], a[4], a[5], a[6]); break;
            default: r = ((F8)fn)(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7]); break;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { *ok = false; }
    return r;
}

// SEH-guarded XMM-capable invoke (args -> GP + XMM0-3); returns RAX, writes XMM0 (raw bits).
static ULONG_PTR invokeGuardedXmm(void* fn, ULONG_PTR* a, double* xmm0, bool* ok)
{
    ULONG_PTR r = 0; *ok = true; *xmm0 = 0.0;
    __try { r = fl_call_xmm(fn, a, xmm0); }
    __except (EXCEPTION_EXECUTE_HANDLER) { *ok = false; }
    return r;
}

static BOOL CALLBACK enumProc(HWND h, LPARAM lp)
{
    DWORD pid = 0; GetWindowThreadProcessId(h, &pid);
    if (pid != GetCurrentProcessId()) return TRUE;
    // Splash screens, dialogs and our WPF/Avalonia windows are not FL's dispatch
    // thread. Hidden/minimized FL windows remain valid for headless automation.
    wchar_t className[128]{};
    if (!GetClassNameW(h, className, _countof(className)) ||
        wcscmp(className, L"TFruityLoopsMainForm") != 0) return TRUE;
    *(HWND*)lp = h; return FALSE;
}
static HWND findMainWindow()
{ HWND r = NULL; EnumWindows(enumProc, (LPARAM)&r); return r; }

// ===================== in-FL chat tab (re/14 §1-§9) =====================
// Internal legacy addresses obey the same version and symbol gates as wire commands.
static void* rb(unsigned long long g)
{
    sig_resolveAll();
    return (void*)(uintptr_t)sig_legacyAddr(GetModuleHandleA("FLEngine_x64.dll"), sig_version(), g);
}

// Version-portable resolver for the window-host slice. Mirrors rb()'s void* shape but resolves NAME
// through the runtime signature scanner (sigscan.*) instead of the hardcoded 0x400000 rebase, so the AI
// window works across FL versions. Returns NULL when the symbol is UNRESOLVED (unknown FL version /
// signature miss / not in the table) — every call site must therefore fail-safe on NULL exactly like
// rb()==NULL (invokeGuarded's __try catches a NULL call; direct data derefs are already SEH-guarded).
// sig_resolveAll() is idempotent + a no-op until FLEngine is loaded, so this is safe (and cheap: a single
// atomic check after the first resolve) to call from any window-host path regardless of ordering.
static void* symAddr(const char* name)
{ sig_resolveAll(); return (void*)(uintptr_t)sig_addr(name); }

// True if p points inside the loaded FLEngine_x64 image. Cheap sanity gate for a RESOLVED data pointer
// (e.g. the host-form classRef) before it is handed to an FL function: a shifted/garbage classRef would
// otherwise AV deep inside CreateFormFromClassRef. Best-effort — false when the module/range is
// unavailable, which is the fail-safe answer (caller refuses).
static bool inFlEngine(const void* p)
{
    if (!p) return false;
    HMODULE m = GetModuleHandleA("FLEngine_x64.dll");
    if (!m) return false;
    MODULEINFO mi{};
    if (!GetModuleInformation(GetCurrentProcess(), m, &mi, sizeof(mi))) return false;
    const unsigned char* b = (const unsigned char*)m;
    return (const unsigned char*)p >= b && (const unsigned char*)p < b + mi.SizeOfImage;
}

// Build a Delphi UnicodeString const (refcnt -1 so Delphi_UStrAsg deep-copies it). Returns ptr-to-chars.
static unsigned char g_ustrBuf[256];
static void* makeUStr(const wchar_t* s)
{
    int len = (int)wcslen(s);
    if (12 + (len + 1) * 2 > (int)sizeof(g_ustrBuf)) return NULL;
    *(unsigned short*)(g_ustrBuf + 0) = 0x04B0; // code page 1200
    *(unsigned short*)(g_ustrBuf + 2) = 0x0002; // element size
    *(int*)(g_ustrBuf + 4) = -1;                // refcount (const)
    *(int*)(g_ustrBuf + 8) = len;               // length
    wchar_t* chars = (wchar_t*)(g_ustrBuf + 12);
    for (int i = 0; i < len; i++) chars[i] = s[i];
    chars[len] = 0;
    return chars;
}

// WP control ops (must run on FL's main thread — callers ensure that).
// vtbl[0x200] is NOT SetVisible (B1: the widget renders with no [0x200] call), so we gate visibility by
// PARENTING via vtbl[0x138]: attach to the content panel to show, SetParent(NULL) to hide/detach.
static void wpSetParent(void* ctrl, void* parent)
{
    if (!ctrl) return; bool ok; void** vt = *(void***)ctrl;
    ULONG_PTR a[2] = { (ULONG_PTR)ctrl, (ULONG_PTR)parent };
    invokeGuarded(*(void**)((char*)vt + 0x138), a, 2, &ok);
}
static void wpReparentIf(void* ctrl, void* panel)
{ if (ctrl && *(void**)((char*)ctrl + 0x78) != panel) wpSetParent(ctrl, panel); }
static void wpRender(void* ctrl)
{ if (!ctrl) return; bool ok; ULONG_PTR a[1] = { (ULONG_PTR)ctrl }; invokeGuarded(symAddr("FLwp_Render"), a, 1, &ok); }
static void wpBounds(void* ctrl, int x, int y, int w, int h)
{
    if (!ctrl) return; bool ok; void** vt = *(void***)ctrl;
    ULONG_PTR a[5] = { (ULONG_PTR)ctrl, (ULONG_PTR)(unsigned)x, (ULONG_PTR)(unsigned)y, (ULONG_PTR)(unsigned)w, (ULONG_PTR)(unsigned)h };
    invokeGuarded(*(void**)((char*)vt + 0x188), a, 5, &ok);
}

// Create a TQuickEdit on `panel` per the B1 live-proven recipe. Returns the control or NULL.
static void* makeEdit(void* panel, bool multiline)
{
    bool ok;
    ULONG_PTR ca[3] = { (ULONG_PTR)symAddr("QuickEditVMT"), 1, 0 };
    void* ctrl = (void*)invokeGuarded(symAddr("TQuickEdit_ctor"), ca, 3, &ok);     // FUN_0074c400 (TQuickEdit ctor)
    if (!ok || !ctrl) return NULL;
    void** vt = *(void***)ctrl;
    ULONG_PTR pa[2] = { (ULONG_PTR)ctrl, (ULONG_PTR)panel };
    invokeGuarded(*(void**)((char*)vt + 0x138), pa, 2, &ok);          // SetParent
    wpBounds(ctrl, 13, 200, 234, 24);                                 // placeholder; thunk re-sets to tree rect
    ULONG_PTR s1[2] = { (ULONG_PTR)ctrl, 7 }; invokeGuarded(symAddr("FLui_WP_SetAlign"), s1, 2, &ok);
    ULONG_PTR s2[2] = { (ULONG_PTR)ctrl, 0 }; invokeGuarded(symAddr("FLwp_SetterA"), s2, 2, &ok);
    ULONG_PTR s3[2] = { (ULONG_PTR)ctrl, 0 }; invokeGuarded(symAddr("FLwp_SetterB"), s3, 2, &ok);
    if (multiline) { __try { *(unsigned short*)((char*)ctrl + 0x682) |= 1; } __except (EXCEPTION_EXECUTE_HANDLER) {} }
    ULONG_PTR r1[1] = { (ULONG_PTR)ctrl }; invokeGuarded(symAddr("FLwp_Render"), r1, 1, &ok);  // render
    return ctrl;
}

static void* makeButton(void* panel);   // fwd-decl (defined with the comms helpers, below)

// True when OUR chat tab is the browser's currently-selected tab.
static bool ourTabActive()
{
    if (!g_browser || !g_ourTab) return false;
    void* tabs = *(void**)((char*)g_browser + 0x158);
    if (!tabs) return false;
    int idx = *(int*)((char*)tabs + 0x18);
    void** arr = *(void***)((char*)tabs + 0x10);
    return arr && idx >= 0 && arr[idx] == g_ourTab;
}
// Set/clear the main-form "suppress global shortcuts" flag (so space goes to our edit, not transport).
static void setShortcutSuppress(int on)
{
    if (!g_mainForm) return;
    __try { *(char*)((char*)g_mainForm + 0x4c5) = (char)(on ? 1 : 0); } __except (EXCEPTION_EXECUTE_HANDLER) {}
}
// Object-free (C2712-safe) read of the suppress flag for status reporting. -1 = no main form, -2 = fault.
static int readMainFormFlag()
{
    if (!g_mainForm) return -1;
    __try { return *(unsigned char*)((char*)g_mainForm + 0x4c5); } __except (EXCEPTION_EXECUTE_HANDLER) { return -2; }
}

static void __fastcall ShortcutCapture(void* form, void* key, void* handled);  // fwd
// Restore FormShortCut's original prologue (un-hook). Memory write — safe from any thread.
static void removeShortcutHook()
{
    if (!g_scHooked || !g_shortcutFn) return;
    DWORD oldP;
    if (VirtualProtect(g_shortcutFn, 12, PAGE_EXECUTE_READWRITE, &oldP)) {
        memcpy(g_shortcutFn, g_scSaved, 12);
        DWORD t; VirtualProtect(g_shortcutFn, 12, oldP, &t);
        FlushInstructionCache(GetCurrentProcess(), g_shortcutFn, 12);
    }
    g_scHooked = false;
}
// Inline hook on FormShortCut — installed while OUR chat tab is the active content, so we can force
// Handled=0 (FL won't consume the key as a shortcut) → space/etc. fall through to our focused edit.
static void installShortcutHook()
{
    if (g_scHooked) return;                          // already hooked
    g_shortcutFn = symAddr("FormShortCut");
    if (!g_shortcutFn) return;
    DWORD oldP;
    if (VirtualProtect(g_shortcutFn, 12, PAGE_EXECUTE_READWRITE, &oldP)) {
        memcpy(g_scSaved, g_shortcutFn, 12);        // save original prologue
        unsigned char* p = (unsigned char*)g_shortcutFn;
        void* cap = (void*)&ShortcutCapture;
        p[0] = 0x48; p[1] = 0xB8; memcpy(p + 2, &cap, 8); p[10] = 0xFF; p[11] = 0xE0;  // mov rax,cap ; jmp rax
        DWORD t; VirtualProtect(g_shortcutFn, 12, oldP, &t);
        FlushInstructionCache(GetCurrentProcess(), g_shortcutFn, 12);
        g_scHooked = true;
    }
}
// Replaces FormShortCut while OUR chat tab is the active content (a jmp from its prologue lands here on the
// SAME stack with the same args). We return WITHOUT dispatching any shortcut: *handled is 0 on entry (VCL),
// so FL treats the key as not-a-shortcut and it falls through to our focused edit — so space (and every key)
// types instead of triggering transport. This covers ALL of FormShortCut's dispatch blocks; the earlier
// mainForm+0x4c5 flag only gated block 1, so space (a keydown handled by block 3) still played. The hook is
// installed only while our tab is active (showOurChat) and removed otherwise (hideOurChat/teardown), so FL's
// real shortcuts work normally elsewhere. UI-thread only.
static void __fastcall ShortcutCapture(void* form, void* key, void* handled)
{
    __try { if (!g_mainForm) g_mainForm = form; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    (void)key; (void)handled;   // suppress: do nothing, leave *handled == 0 (not a shortcut)
}

static void __fastcall KeyDownCapture(void*, void*, void*, unsigned short);  // fwd
static void removeKeyDownHook()
{
    if (!g_kdHooked || !g_keyDownFn) return;
    DWORD oldP;
    if (VirtualProtect(g_keyDownFn, 12, PAGE_EXECUTE_READWRITE, &oldP)) {
        memcpy(g_keyDownFn, g_kdSaved, 12);
        DWORD t; VirtualProtect(g_keyDownFn, 12, oldP, &t);
        FlushInstructionCache(GetCurrentProcess(), g_keyDownFn, 12);
    }
    g_kdHooked = false;
}
static void installKeyDownHook()
{
    if (g_kdHooked) return;
    g_keyDownFn = symAddr("FormKeyDown");
    if (!g_keyDownFn) return;
    DWORD oldP;
    if (VirtualProtect(g_keyDownFn, 12, PAGE_EXECUTE_READWRITE, &oldP)) {
        memcpy(g_kdSaved, g_keyDownFn, 12);
        unsigned char* p = (unsigned char*)g_keyDownFn;
        void* cap = (void*)&KeyDownCapture;
        p[0] = 0x48; p[1] = 0xB8; memcpy(p + 2, &cap, 8); p[10] = 0xFF; p[11] = 0xE0;  // mov rax,cap ; jmp rax
        DWORD t; VirtualProtect(g_keyDownFn, 12, oldP, &t);
        FlushInstructionCache(GetCurrentProcess(), g_keyDownFn, 12);
        g_kdHooked = true;
    }
}
// Replaces TFruityLoopsMainForm.FormKeyDown (KeyPreview) while our chat tab is active. Returns WITHOUT
// processing: the Key (param_3) is left untouched (not set to 0), so VCL does NOT treat it as consumed →
// it falls through to our focused edit (TranslateMessage → WM_CHAR). This is where space=play lives
// (FUN_010c3c30(1,0) at key 0x20); suppressing it here lets space TYPE. Installed only while our tab is
// active (showOurChat) and removed otherwise (hideOurChat/teardown). UI-thread only.
static void __fastcall KeyDownCapture(void* form, void* sender, void* key, unsigned short shift)
{
    __try { if (!g_mainForm) g_mainForm = form; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    (void)sender; (void)key; (void)shift;
}

// Read FL's is-playing flag (*(int*)PTR_DAT_014a81c0): 0 = stopped, non-0 = playing. -1/-2 = not readable.
static int readPlaying()
{
    __try { void* pp = *(void**)symAddr("PlayStatePtr"); return pp ? *(int*)pp : -1; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return -2; }
}

// Parent our widgets onto the content panel + size them to FULLY cover the tree (display fills the top,
// input the bottom-left, Send button bottom-right, no gaps), render LAST so they draw on top. Called from
// the thunk AND from open (open can't rely on its SelectTabById firing the thunk — clone made us current).
static void showOurChat()
{
    if (!g_browser || !g_ourTab) return;
    void* panel = *(void**)((char*)g_browser + 0x34c);
    void* tree = *(void**)((char*)g_browser + 0x190);
    int x = *(int*)((char*)tree + 0x90), y = *(int*)((char*)tree + 0x94);
    int w = *(int*)((char*)tree + 0x98), h = *(int*)((char*)tree + 0x9c);
    const int inH = 26, btnW = 56;
    int dispH = (h > inH) ? h - inH : h;
    int inW = (w > btnW) ? w - btnW : w;
    wpReparentIf(g_displayCtrl, panel);
    wpReparentIf(g_inputCtrl, panel);
    wpReparentIf(g_sendBtn, panel);
    wpBounds(g_displayCtrl, x, y, w, dispH);
    wpBounds(g_inputCtrl, x, y + dispH, inW, inH);
    wpBounds(g_sendBtn, x + inW, y + dispH, btnW, inH);
    wpRender(g_displayCtrl); wpRender(g_inputCtrl); wpRender(g_sendBtn);
    installKeyDownHook();            // our tab active → suppress FormKeyDown (the real space->play path)
    installShortcutHook();           // also suppress FormShortCut (Ctrl-shortcuts) so they don't fire while typing
    setShortcutSuppress(1);          // backup (covers FormShortCut block 1)
}
static void hideOurChat()
{
    wpBounds(g_inputCtrl, -30000, -30000, 0, 0);   wpSetParent(g_inputCtrl, NULL);
    wpBounds(g_displayCtrl, -30000, -30000, 0, 0); wpSetParent(g_displayCtrl, NULL);
    wpBounds(g_sendBtn, -30000, -30000, 0, 0);     wpSetParent(g_sendBtn, NULL);
    removeKeyDownHook();             // off our tab → restore FormKeyDown (space=play) entirely
    removeShortcutHook();
    setShortcutSuppress(0);
}

// Patched browser vtbl[0xd0] (FLbrz_ContentSwitch). FL calls this on the UI thread when a tab is selected.
static void __fastcall ContentSwitchThunk(void* browser, int contentId, char p3, char p4)
{
    bool handled = false;
    __try {
        if (browser == g_browser && g_ourTab) {
            void* tabs = *(void**)((char*)browser + 0x158);
            int idx = tabs ? *(int*)((char*)tabs + 0x18) : -1;
            void** arr = tabs ? *(void***)((char*)tabs + 0x10) : NULL;
            void* cur = (arr && idx >= 0) ? arr[idx] : NULL;
            if (cur == g_ourTab) { showOurChat(); handled = true; }  // cover the tree, skip populate
            else                 { hideOurChat(); }                  // detach so the original's repaint is clean
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { handled = false; }
    if (!handled && g_origContentSwitch)
        ((void(__fastcall*)(void*, int, char, char))g_origContentSwitch)(browser, contentId, p3, p4);
}

// Whole open sequence, run on the MAIN thread (via WM_BRIDGE_CHATOPEN). re/14 §4-§6.
// IDEMPOTENT: reuse our existing tab/widgets/hook if present; only create what's missing; just re-select
// on repeat. (Prevents the duplicate-tab accumulation from repeated opens.)
static bool DoChatTabOpen()
{
    // QuickEdit fields move in 2026 independently of constructor signatures. Refuse before cloning
    // tabs, creating controls, installing callbacks, or writing any unverified widget field.
    if (!sig_legacyBrowserSupported()) return false;
    __try {
        void* gb = *(void**)symAddr("MainBrowserPtr");                 // *DAT_0157ffb8 (main browser)
        if (!gb) return false;
        g_browser = gb;
        void* tabs = *(void**)((char*)gb + 0x158);
        if (!tabs) return false;
        bool ok;

        // Is our tab still present in the array? (identity by pointer)
        bool haveTab = false;
        {
            void** arr = *(void***)((char*)tabs + 0x10);
            int cnt = arr ? *(int*)((char*)arr - 8) : 0;
            for (int i = 0; g_ourTab && i < cnt; i++) if (arr[i] == g_ourTab) { haveTab = true; break; }
            if (!haveTab) g_ourTab = NULL;                  // vanished (FL closed it) → recreate
        }

        if (!haveTab) {                                     // clone a fresh tab
            void** arr = *(void***)((char*)tabs + 0x10);
            int n = arr ? *(int*)((char*)arr - 8) : 0;
            if (!arr || n <= 0) return false;
            int srcId = *(int*)((char*)arr[0] + 0xf4);
            ULONG_PTR cl[2] = { (ULONG_PTR)gb, (ULONG_PTR)(unsigned)srcId };
            invokeGuarded(symAddr("FLbrz_AddTabClone"), cl, 2, &ok);        // FLbrz_AddTabCloneOfSource
            arr = *(void***)((char*)tabs + 0x10);
            int n2 = *(int*)((char*)arr - 8);
            if (n2 <= n) return false;
            g_ourTab = arr[n2 - 1];
            g_ourTabId = *(int*)((char*)g_ourTab + 0xf4);
            void* us = makeUStr(L"FruityLink AI");
            ULONG_PTR a1[2] = { (ULONG_PTR)((char*)g_ourTab + 0x20), (ULONG_PTR)us }; invokeGuarded(symAddr("Delphi_UStrAsg"), a1, 2, &ok);
            ULONG_PTR a2[2] = { (ULONG_PTR)((char*)g_ourTab + 0x6c), (ULONG_PTR)us }; invokeGuarded(symAddr("Delphi_UStrAsg"), a2, 2, &ok);
        }

        void* panel = *(void**)((char*)gb + 0x34c);
        if (!panel) return false;
        if (!g_inputCtrl)   g_inputCtrl   = makeEdit(panel, true);   // multi-line: Enter inserts \n → poll auto-submits
        if (!g_displayCtrl) g_displayCtrl = makeEdit(panel, true);
        if (!g_sendBtn)     g_sendBtn     = makeButton(panel);       // "Send" button (unambiguous submit)
        // SPACE FIX: mark the input as a keyboard-capturing text field — FUN_00802820(ctrl,1) sets flag bit 0x8
        // (vtbl[0x270]), exactly what FL's own search box does; without it FL's spacebar=transport shortcut
        // eats spaces before our edit sees them.
        if (g_inputCtrl) { ULONG_PTR sf[2] = { (ULONG_PTR)g_inputCtrl, 1 }; invokeGuarded(rb(0x802820), sf, 2, &ok); }
        wpSetParent(g_inputCtrl, NULL);                     // detached until our tab is selected (thunk parents)
        wpSetParent(g_displayCtrl, NULL);
        wpSetParent(g_sendBtn, NULL);

        if (!g_vtblSlot) {                                  // install the hook ONCE (never re-save the thunk)
            void** vt = *(void***)gb;
            g_vtblSlot = (void**)((char*)vt + 0xd0);
            g_origContentSwitch = *g_vtblSlot;
            DWORD oldP;
            if (VirtualProtect(g_vtblSlot, 8, PAGE_READWRITE, &oldP)) {
                *g_vtblSlot = (void*)&ContentSwitchThunk;
                DWORD t; VirtualProtect(g_vtblSlot, 8, oldP, &t);
            }
        }

        installShortcutHook();                              // capture the main form on the next keypress → space fix

        ULONG_PTR se[3] = { (ULONG_PTR)gb, (ULONG_PTR)(unsigned)g_ourTabId, 0 };
        invokeGuarded(rb(0x9ac590), se, 3, &ok);            // FLbrz_SelectTabById (tab-bar state)
        showOurChat();                                      // force-show (select is a no-op if our tab is already current)
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

// Restore the hook + hide our widgets, run on the MAIN thread (via WM_BRIDGE_CHATCLOSE). re/14 §8.
static bool DoChatTabClose()
{
    __try {
        if (g_vtblSlot && g_origContentSwitch) {            // restore vtbl FIRST (no thunk entry after this)
            DWORD oldP;
            if (VirtualProtect(g_vtblSlot, 8, PAGE_READWRITE, &oldP)) {
                *g_vtblSlot = g_origContentSwitch;
                DWORD t; VirtualProtect(g_vtblSlot, 8, oldP, &t);
            }
        }
        g_vtblSlot = NULL; g_origContentSwitch = NULL;
        setShortcutSuppress(0);                             // restore normal transport shortcuts
        removeShortcutHook();                               // un-hook FormShortCut
        removeKeyDownHook();                                // un-hook FormKeyDown (must restore before unmap)
        // restore the button's onClick TMethod BEFORE detach/unmap so FL can't call our (soon-freed) handler
        if (g_sendBtn) *(void**)((char*)g_sendBtn + 0x1e4) = g_sendBtnOrigClick;
        wpSetParent(g_inputCtrl, NULL); wpSetParent(g_displayCtrl, NULL); wpSetParent(g_sendBtn, NULL); // detach
        if (g_browser) {
            // select tab 0 — the (now-restored) original content-switch repaints the tree cleanly
            void* tabs = *(void**)((char*)g_browser + 0x158);
            void** arr = tabs ? *(void***)((char*)tabs + 0x10) : NULL;
            if (arr) { int id0 = *(int*)((char*)arr[0] + 0xf4); bool ok;
                ULONG_PTR se[3] = { (ULONG_PTR)g_browser, (ULONG_PTR)(unsigned)id0, 0 }; invokeGuarded(rb(0x9ac590), se, 3, &ok); }
        }
        g_inputCtrl = NULL; g_displayCtrl = NULL; g_sendBtn = NULL; g_sendBtnOrigClick = NULL;
        g_ourTab = NULL; g_browser = NULL; g_ourTabId = 0;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

// ---- chat comms (Stage C): read/append TQuickEdit text + submit (all on the MAIN thread) ----
static std::wstring utf8ToW(const std::string& s)
{
    if (s.empty()) return std::wstring();
    int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), NULL, 0);
    std::wstring w(n, 0); MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), &w[0], n); return w;
}
static std::string wToUtf8(const std::wstring& w)
{
    if (w.empty()) return std::string();
    int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), NULL, 0, NULL, NULL);
    std::string s(n, 0); WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), &s[0], n, NULL, NULL); return s;
}
// Read a TQuickEdit's text (Delphi UStr @ +0x624; len at ptr-4). The SEH part copies into a static buffer
// (no C++ objects in the __try frame — MSVC C2712); readEditW builds the std::wstring outside __try.
static wchar_t g_readBuf[8192];
static int readEditRaw(void* ctrl)
{
    if (!ctrl) return 0;
    __try {
        wchar_t* p = *(wchar_t**)((char*)ctrl + 0x624);
        if (!p) return 0;
        int len = *(int*)((char*)p - 4);
        if (len <= 0) return 0;
        if (len > 8191) len = 8191;
        memcpy(g_readBuf, p, (size_t)len * 2);
        g_readBuf[len] = 0;
        return len;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
static std::wstring readEditW(void* ctrl)
{
    int n = readEditRaw(ctrl);
    return n > 0 ? std::wstring(g_readBuf, n) : std::wstring();
}
// Set a TQuickEdit's text (FUN_0074c260 deep-copies the const → safe to free after) + caret to end + refresh.
static void setEditText(void* ctrl, const std::wstring& s)
{
    if (!ctrl) return;
    int len = (int)s.size();
    size_t bytes = 12 + (size_t)(len + 1) * 2;
    unsigned char* buf = (unsigned char*)malloc(bytes); if (!buf) return;
    *(unsigned short*)(buf + 0) = 0x04B0; *(unsigned short*)(buf + 2) = 0x0002;
    *(int*)(buf + 4) = -1; *(int*)(buf + 8) = len;
    wchar_t* chars = (wchar_t*)(buf + 12);
    for (int i = 0; i < len; i++) chars[i] = s[i]; chars[len] = 0;
    bool ok;
    ULONG_PTR a[2] = { (ULONG_PTR)ctrl, (ULONG_PTR)chars }; invokeGuarded(rb(0x74c260), a, 2, &ok); // set text
    __try { *(int*)((char*)ctrl + 0x62c) = len; } __except (EXCEPTION_EXECUTE_HANDLER) {}             // caret to end → scrolls
    ULONG_PTR r[1] = { (ULONG_PTR)ctrl }; invokeGuarded(rb(0x74bb70), r, 1, &ok);                     // refresh
    free(buf);
}
// MAIN-thread: append "<text>\n" to the display, scrolled to the newest line.
static void sayMain()
{
    std::wstring cur = readEditW(g_displayCtrl);
    cur += utf8ToW(g_sayText);
    cur += L"\n";
    setEditText(g_displayCtrl, cur);
}
// MAIN-thread: pull a submitted message out of the input into g_chatIn (chat_poll drains it). force=true →
// take the whole input (Send button / chat_submit); force=false → only if a newline is present. Clears input.
static void takeInput(bool force)
{
    std::wstring t = readEditW(g_inputCtrl);
    size_t nl = t.find_first_of(L"\r\n");
    if (!force && nl == std::wstring::npos) return;                    // nothing submitted yet
    std::wstring msg = (nl == std::wstring::npos) ? t : t.substr(0, nl);
    setEditText(g_inputCtrl, L"");                                     // clear the input
    std::string u = wToUtf8(msg);
    if (!u.empty()) g_chatIn = u;
}
// Send-button onClick handler (Delphi TMethod: FL calls code(Data, Sender) on the UI thread). Submits the input.
static void __fastcall SendButtonClick(void* data, void* sender)
{
    (void)data; (void)sender;
    __try { takeInput(true); } __except (EXCEPTION_EXECUTE_HANDLER) {}
}
static bool setControlCaption(void* control, const wchar_t* text)
{
    DelphiString caption(text);
    bool ok = false;
    ULONG_PTR args[2] = { reinterpret_cast<ULONG_PTR>(control), reinterpret_cast<ULONG_PTR>(caption.data()) };
    invokeGuarded(symAddr("FLwp_SetButtonCaption"), args, 2, &ok);
    return ok;
}
// Create a native "Send" TQuickBtn on the panel + hook its onClick TMethod (+0x144 code / +0x14c data).
static void* makeButton(void* panel)
{
    bool ok;
    void* btn = (void*)invokeGuarded(symAddr("FLwp_CreateButtonControl"), NULL, 0, &ok);     // FLwp_CreateButtonControl()
    if (!ok || !btn) return NULL;
    void** vt = *(void***)btn;
    ULONG_PTR pa[2] = { (ULONG_PTR)btn, (ULONG_PTR)panel }; invokeGuarded(*(void**)((char*)vt + 0x138), pa, 2, &ok); // SetParent
    ULONG_PTR c1[2] = { (ULONG_PTR)btn, 0 }; invokeGuarded(symAddr("FLwp_SetterB"), c1, 2, &ok);                                 // (match FLbrz button setup)
    ULONG_PTR c2[2] = { (ULONG_PTR)btn, 0 }; invokeGuarded(symAddr("FLwp_SetterA"), c2, 2, &ok);
    ok = setControlCaption(btn, L"Send");               // caption text
    ULONG_PTR ce[2] = { (ULONG_PTR)btn, 6 }; invokeGuarded(symAddr("FLui_WP_SetAlign"), ce, 2, &ok);  // role 6 = clickable button — THIS lays it out/renders (was missing → invisible)
    ULONG_PTR r[1] = { (ULONG_PTR)btn }; invokeGuarded(symAddr("FLwp_Render"), r, 1, &ok);        // render
    __try {                                                            // hook button onClick @+0x1e4 code / +0x1ec data
        g_sendBtnOrigClick = *(void**)((char*)btn + 0x1e4);            // (FLbrz buttons fire +0x1e4, NOT +0x144)
        *(void**)((char*)btn + 0x1ec) = btn;
        *(void**)((char*)btn + 0x1e4) = (void*)&SendButtonClick;
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    return btn;
}

// ===================== Plugins toolbar dropdown (re/16) =====================
// Resolve the managed plugin functions from the CLR host (FlClrHost.dll). Returns false when the
// host isn't loaded (e.g. flprobe injected only FlBridge.dll into stock FL) → graceful degrade.
static bool resolvePluginFns()
{
    if (g_pluginListFn && g_pluginToggleFn) return true;
    HMODULE h = GetModuleHandleA("FlClrHost.dll");
    if (!h) return false;
    typedef void (*GetFns)(void**, void**);
    GetFns g = (GetFns)GetProcAddress(h, "FlClr_GetPluginFns");
    if (!g) return false;
    void* l = NULL; void* t = NULL;
    __try { g(&l, &t); } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    g_pluginListFn = (PluginListFn)l; g_pluginToggleFn = (PluginToggleFn)t;
    return g_pluginListFn && g_pluginToggleFn;
}
// SEH-guarded managed invokes (no C++ objects in the __try frame — MSVC C2712).
static int callListRaw(char* b, int len)
{ __try { return g_pluginListFn(b, len); } __except (EXCEPTION_EXECUTE_HANDLER) { return -1000; } }
static int callToggleRaw(const char* id, int idlen, int en)
{ __try { return g_pluginToggleFn(id, idlen, en); } __except (EXCEPTION_EXECUTE_HANDLER) { return -1000; } }

// Returns the managed plugin list as JSON (or "" if the host/glue is unavailable, "null" if the
// manager isn't registered yet).
static std::string callPluginList()
{
    if (!resolvePluginFns()) return "";
    std::string buf; buf.resize(65536);
    int n = callListRaw(&buf[0], (int)buf.size());
    if (n == -1000 || n < 0) return "";
    if (n > (int)buf.size()) { buf.resize(n); n = callListRaw(&buf[0], (int)buf.size()); if (n == -1000 || n < 0) return ""; }
    buf.resize(n < (int)buf.size() ? n : (int)buf.size());
    return buf;
}
// Toggle a plugin by id; returns 1 ok / 0 fail / -1 no-manager / -1000 fault / -2 no-host.
static int callPluginToggle(const std::string& id, bool enable)
{
    if (!resolvePluginFns()) return -2;
    return callToggleRaw(id.c_str(), (int)id.size(), enable ? 1 : 0);
}

struct PluginToggleRequest {
    std::string id;
    bool enable;
    HWND refreshWindow;
    HMODULE bridgeModule;
};

static void CALLBACK pluginToggleWorker(PTP_CALLBACK_INSTANCE instance, void* context)
{
    std::unique_ptr<PluginToggleRequest> request(static_cast<PluginToggleRequest*>(context));
    // A temporary module reference protects the callback during unload, and is released by Windows
    // only after the callback has returned. No permanent pin or teardown wait on FL's thread.
    FreeLibraryWhenCallbackReturns(instance, request->bridgeModule);
    try {
        if (!g_bridgeStopping.load()) {
            callPluginToggle(request->id, request->enable);
            if (!g_bridgeStopping.load() && request->refreshWindow)
                PostMessageW(request->refreshWindow, WM_BRIDGE_PLUGINSINSTALL, 0, 0);
        }
    } catch (...) { logline("plugin menu toggle failed"); }
    g_pluginTogglePending.store(false);
}

static void queuePluginToggle(const std::string& id, bool enable)
{
    if (g_bridgeStopping.load() || g_pluginTogglePending.exchange(true)) return;
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                           reinterpret_cast<LPCWSTR>(&pluginToggleWorker), &module)) {
        g_pluginTogglePending.store(false);
        return;
    }
    try {
        auto request = std::make_unique<PluginToggleRequest>(PluginToggleRequest{id, enable, g_mainWnd, module});
        if (TrySubmitThreadpoolCallback(pluginToggleWorker, request.get(), nullptr)) {
            request.release();
            return;
        }
    } catch (...) { logline("could not queue plugin menu toggle"); }
    FreeLibrary(module);
    g_pluginTogglePending.store(false);
}

// ---- Settings glue (task #61): debug-output window visibility via FlClr_GetSettingsFns ----
// Resolve the managed get/set functions from the CLR host. Same graceful degrade as resolvePluginFns:
// false when FlClrHost.dll isn't loaded (e.g. flprobe injected only FlBridge.dll) → Settings shows but
// toggling no-ops.
static bool resolveSettingsFns()
{
    if (g_getDebugVisibleFn && g_setDebugVisibleFn) return true;
    HMODULE h = GetModuleHandleA("FlClrHost.dll");
    if (!h) return false;
    typedef void (*GetFns)(void**, void**);
    GetFns g = (GetFns)GetProcAddress(h, "FlClr_GetSettingsFns");
    if (!g) return false;
    void* s = NULL; void* gt = NULL;
    __try { g(&s, &gt); } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    g_setDebugVisibleFn = (DebugSetFn)s; g_getDebugVisibleFn = (DebugGetFn)gt;
    return g_getDebugVisibleFn && g_setDebugVisibleFn;
}
// SEH-guarded managed invokes (no C++ objects in the __try frame — MSVC C2712).
static int  callGetDebugRaw(void)  { __try { return g_getDebugVisibleFn(); } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; } }
static int  callSetDebugRaw(int v) { __try { return g_setDebugVisibleFn(v); } __except (EXCEPTION_EXECUTE_HANDLER) { return -1000; } }
// Current debug-window visibility (0 when the host/glue is unavailable, so the ✓ defaults to off).
static int  callDebugVisibleGet(void) { if (!resolveSettingsFns()) return 0; return callGetDebugRaw(); }
// Apply debug-window visibility (no-op when the host/glue is unavailable). Managed side marshals to its
// own UI thread, so this is safe to call from any thread.
static void callDebugVisibleSet(int v) { if (resolveSettingsFns()) (void)callSetDebugRaw(v); }

// ---- Menu-contribution glue: generic plugin menu entries via FlClr_GetMenuFns ----
// Resolves the managed MenuGlue exports from the CLR host (FlClrHost.dll). Same graceful degrade as
// resolvePluginFns: false when FlClrHost.dll isn't loaded (e.g. flprobe injected only FlBridge.dll).
typedef int (*MenuListFn)(char*, int);            // managed MenuGlue.ContributionsJson(buf,len)
typedef int (*MenuInvokeFn)(const char*, int);    // managed MenuGlue.Invoke(idUtf8,idLen)
typedef int (*MenuCheckedFn)(const char*, int);   // managed MenuGlue.Checked(idUtf8,idLen)
static MenuListFn    g_menuListFn    = NULL;
static MenuInvokeFn  g_menuInvokeFn  = NULL;
static MenuCheckedFn g_menuCheckedFn = NULL;
static bool resolveMenuFns()
{
    if (g_menuListFn && g_menuInvokeFn && g_menuCheckedFn) return true;
    HMODULE h = GetModuleHandleA("FlClrHost.dll");
    if (!h) return false;
    typedef void (*GetFns)(void**, void**, void**);
    GetFns g = (GetFns)GetProcAddress(h, "FlClr_GetMenuFns");
    if (!g) return false;
    void* l = NULL; void* iv = NULL; void* ck = NULL;
    __try { g(&l, &iv, &ck); } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    g_menuListFn = (MenuListFn)l; g_menuInvokeFn = (MenuInvokeFn)iv; g_menuCheckedFn = (MenuCheckedFn)ck;
    return g_menuListFn && g_menuInvokeFn && g_menuCheckedFn;
}
// SEH-guarded managed invokes (no C++ objects in the __try frame — MSVC C2712).
static int callMenuListRaw(char* b, int len)            { __try { return g_menuListFn(b, len); }    __except (EXCEPTION_EXECUTE_HANDLER) { return -1000; } }
static int callMenuInvokeRaw(const char* id, int idlen) { __try { return g_menuInvokeFn(id, idlen); } __except (EXCEPTION_EXECUTE_HANDLER) { return -1000; } }
// Returns the managed contribution list as JSON ("" if the host/glue is unavailable).
static std::string callMenuList()
{
    if (!resolveMenuFns()) return "";
    std::string buf; buf.resize(65536);
    int n = callMenuListRaw(&buf[0], (int)buf.size());
    if (n == -1000 || n < 0) return "";
    if (n > (int)buf.size()) { buf.resize(n); n = callMenuListRaw(&buf[0], (int)buf.size()); if (n == -1000 || n < 0) return ""; }
    buf.resize(n < (int)buf.size() ? n : (int)buf.size());
    return buf;
}
// Fire a contribution's handler by id. Returns 1 ok / 0 unknown / -1 no-manager / -2 no-host / -1000 fault.
static int callMenuInvoke(const std::string& id)
{
    if (!resolveMenuFns()) return -2;
    return callMenuInvokeRaw(id.c_str(), (int)id.size());
}

#ifdef FRUITYLINK_DEBUG_PIPE
// ---- debug-pipe extra glue (flprobe plugin loop): Reload + PluginsDir via FlClr_GetPluginExtraFns ----
typedef int (*PluginReloadFn)(const char*, int);   // managed PluginGlue.Reload(id,len)
typedef int (*PluginDirFn)(char*, int);            // managed PluginGlue.PluginsDir(buf,len)
static PluginReloadFn g_pluginReloadFn = NULL;
static PluginDirFn    g_pluginDirFn    = NULL;
static bool resolvePluginExtraFns()
{
    if (g_pluginReloadFn && g_pluginDirFn) return true;
    HMODULE h = GetModuleHandleA("FlClrHost.dll");
    if (!h) return false;
    typedef void (*GetExtra)(void**, void**);
    GetExtra g = (GetExtra)GetProcAddress(h, "FlClr_GetPluginExtraFns");
    if (!g) return false;
    void* r = NULL; void* d = NULL;
    __try { g(&r, &d); } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    g_pluginReloadFn = (PluginReloadFn)r; g_pluginDirFn = (PluginDirFn)d;
    return g_pluginReloadFn && g_pluginDirFn;
}
// SEH-guarded managed invokes (no C++ objects in the __try frame — MSVC C2712).
static int callReloadRaw(const char* id, int idlen)
{ __try { return g_pluginReloadFn(id, idlen); } __except (EXCEPTION_EXECUTE_HANDLER) { return -1000; } }
static int callDirRaw(char* b, int len)
{ __try { return g_pluginDirFn(b, len); } __except (EXCEPTION_EXECUTE_HANDLER) { return -1000; } }
// Reload a plugin by id; returns 1 ok / 0 fail / -1 no-manager / -2 no-host-or-unsupported / -1000 fault.
static int callPluginReload(const std::string& id)
{
    if (!resolvePluginExtraFns()) return -2;
    return callReloadRaw(id.c_str(), (int)id.size());
}
// Absolute plugins directory the host watches ("" when the host/glue is unavailable).
static std::string callPluginsDir()
{
    if (!resolvePluginExtraFns()) return "";
    std::string buf; buf.resize(4096);
    int n = callDirRaw(&buf[0], (int)buf.size());
    if (n == -1000 || n <= 0) return "";
    if (n > (int)buf.size()) { buf.resize(n); n = callDirRaw(&buf[0], (int)buf.size()); if (n <= 0) return ""; }
    buf.resize(n < (int)buf.size() ? n : (int)buf.size());
    return buf;
}
// Trim ASCII spaces + CR/LF from both ends (for "plugin_enable <id>" arg parsing).
static std::string trimArg(const std::string& s)
{
    size_t a = 0, b = s.size();
    while (a < b && (s[a] == ' ' || s[a] == '\t')) a++;
    while (b > a && (s[b - 1] == ' ' || s[b - 1] == '\t' || s[b - 1] == '\r' || s[b - 1] == '\n')) b--;
    return s.substr(a, b - a);
}
#endif // FRUITYLINK_DEBUG_PIPE

// --- minimal JSON scan (matches PluginGlue's fixed field order; only \\ and \" are escaped) ---
static bool jsonReadStr(const std::string& j, size_t& i, std::string& out)
{
    if (i >= j.size() || j[i] != '"') return false;
    i++; out.clear();
    while (i < j.size()) {
        char c = j[i++];
        if (c == '\\') { if (i < j.size()) out.push_back(j[i++]); }
        else if (c == '"') return true;
        else out.push_back(c);
    }
    return false;
}
static bool jsonFindKey(const std::string& j, size_t& i, const char* key)
{
    std::string pat = std::string("\"") + key + "\"";
    size_t p = j.find(pat, i);
    if (p == std::string::npos) return false;
    i = p + pat.size();
    while (i < j.size() && (j[i] == ':' || j[i] == ' ')) i++;
    return true;
}
static void parsePluginsJson(const std::string& j)
{
    g_pluginCache.clear();
    size_t i = 0;
    while (true) {
        if (!jsonFindKey(j, i, "id")) break;
        std::string id, name, ver;
        if (!jsonReadStr(j, i, id)) break;
        if (!jsonFindKey(j, i, "name") || !jsonReadStr(j, i, name)) break;
        if (!jsonFindKey(j, i, "version") || !jsonReadStr(j, i, ver)) break;
        if (!jsonFindKey(j, i, "enabled")) break;
        bool en = (i < j.size() && j[i] == 't');
        PluginEntry e; e.id = id; e.name = utf8ToW(name); e.enabled = en;
        g_pluginCache.push_back(e);
    }
}

static void __fastcall PluginItemClick(void* data, void* item);   // fwd

// --- POD-only SEH helpers (kept separate so the callers may use C++ objects; MSVC C2712) ---
static int readIntAt(void* base, int off, int def)
{ __try { return *(int*)((char*)base + off); } __except (EXCEPTION_EXECUTE_HANDLER) { return def; } }
// Only set the standard tag field (item+0x18), exactly like FL's own dynamic menus (FUN_0108fd30).
// We deliberately do NOT poke the checkable/enabled fields (+0x140/+0x80/+0x81) directly: FL's popup
// builds a render-control per item and reading those half-initialized fields faults (+0x45c null
// deref). The enabled-state checkmark is carried in the caption glyph instead — bare, safe items.
static void pluginItemSetFields(void* item, int tag, bool /*checked*/, bool /*disabled*/)
{
    __try { *(long long*)((char*)item + 0x18) = (long long)tag; } __except (EXCEPTION_EXECUTE_HANDLER) {}
}
// NOTE: PTR_DAT_* globals in this FLEngine build hold the ADDRESS of the real .data global (double
// indirection) — same pattern as the proven readPlaying() path. So: slot = *(void**)rb(ghidra);
// object = *(void**)slot.
static void* getActionListPtr()
{
    const FlMenuLayout* layout = sig_menuLayout();
    if (!layout) return NULL;
    __try {
        void* slot = *(void**)symAddr("MainFormPtr");                // PTR_DAT_014a8750 -> &mainForm
        void* mainForm = slot ? *(void**)slot : NULL;                // the main form
        return mainForm ? *(void**)((char*)mainForm + layout->mainMenuOffset) : NULL;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return NULL; }
}
static void readToolbarPtrs(void** tf, void** bar)
{
    *tf = NULL; *bar = NULL;
    const FlMenuLayout* layout = sig_menuLayout();
    if (!layout) return;
    __try {
        void* slot = *(void**)symAddr("ToolbarFormPtr");                         // PTR_DAT_014aa4c8 -> &toolbarForm
        void* t = slot ? *(void**)slot : NULL;                       // the toolbar form
        *tf = t; if (t) *bar = *(void**)((char*)t + layout->toolbarMenuOffset);
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}

// ===================== FL readiness gate (task #60, re/20) =====================
// Our bootstrap wakes ~1s into FL startup — before the song/project object + UI forms are allocated —
// so FL-state WRITES fault (null song obj) and ListChannels is empty. These four core globals are all
// non-null only once the main window + transport UI are built AND the default project/song is loaded.
// Each is a DOUBLE deref of a .data indirection slot (slot -> .bss real global -> live object); check
// every level before the next so a half-built chain never faults. (Reads are 8-byte aligned/atomic on
// x64; none of these slots are in the VMProtect .avm* range — see re/20 §3.)
//
// VERSION-PORTABLE: the four slot addresses are resolved through the signature scanner (symAddr) rather
// than the hardcoded 0x400000 rebase, so the gate works across FL versions. On FL 2025 every symbol
// resolves (signature or 2025 fallback) to exactly the old hardcoded slot, so behavior is unchanged; on
// a version where a symbol can't be resolved, flIsReady reports NOT ready (and logs which one), instead
// of silently AV'ing on a wrong rebased address or hanging forever.
static void* derefReady(void* slotAddr)
{
    __try {
        if (!slotAddr) return NULL;               // symbol unresolved (unknown FL version / FLEngine not loaded)
        void* real = *(void**)slotAddr;           // slot content = real (.bss) global's address
        if (!real) return NULL;
        return *(void**)real;                     // real global -> live object (NULL pre-init)
    } __except (EXCEPTION_EXECUTE_HANDLER) { return NULL; }
}
// TRUE only when all four readiness globals are non-null. Same double-deref shape as getActionListPtr.
static bool flIsReady(void)
{
    void* mainFormSlot = symAddr("MainFormPtr");     // main window slot
    void* toolbarSlot  = symAddr("ToolbarFormPtr");  // transport/tempo UI slot (tempo-WRITE target B)
    void* songSlot     = symAddr("ReadySongObj");    // song/transport object slot (tempo-WRITE target A)
    void* chanSlot     = symAddr("ChannelList");     // channel rack / song doc slot (ListChannels)

    // If any of the four is UNRESOLVED on this FL version we cannot evaluate the gate: report NOT ready
    // and log the missing symbol(s) ONCE, so an unmapped build is diagnosable instead of a silent hang.
    if (!mainFormSlot || !toolbarSlot || !songSlot || !chanSlot) {
        static bool logged = false;
        if (!logged) {
            logged = true;
            char buf[256];
            _snprintf_s(buf, sizeof(buf), _TRUNCATE,
                "flIsReady: unresolved readiness symbol(s): %s%s%s%s(FL version not fully mapped) — gate reports NOT ready",
                mainFormSlot ? "" : "MainFormPtr ",
                toolbarSlot  ? "" : "ToolbarFormPtr ",
                songSlot     ? "" : "ReadySongObj ",
                chanSlot     ? "" : "ChannelList ");
            logline(buf);
        }
        return false;
    }

    void* mainForm    = derefReady(mainFormSlot);
    void* toolbarForm = derefReady(toolbarSlot);
    void* songObj     = derefReady(songSlot);
    void* chanList    = derefReady(chanSlot);
    return mainForm && toolbarForm && songObj && chanList && findMainWindow();
}

// Append one item to the popup root. tag>=0 = a real plugin (checkable, clickable); tag<0 with
// disabled=true = an inert status line ("Plugin host not ready" / "No plugins installed").
static void addPluginMenuItem(void* root, const wchar_t* name, int tag, bool checked, bool disabled)
{
    bool ok;
    std::wstring cap;
    if (tag >= 0) cap = std::wstring(checked ? L"\x2713  " : L"     ") + name;  // check glyph + name
    else          cap = name;                                                   // status line (tag<0)
    void* us = makeUStr(cap.c_str());
    if (!us) return;
    // Every item gets the same (non-null) onClick — PluginItemClick no-ops for tag<0 (status lines),
    // so status items are safe to click. Keeping a uniform, fully-FL-initialized item avoids the
    // popup-render crash from custom/disabled flag pokes.
    (void)disabled;
    void* tm[2]; tm[0] = (void*)&PluginItemClick; tm[1] = g_pluginsCtx;
    ULONG_PTR ia[4] = { (ULONG_PTR)root, (ULONG_PTR)0xFFFFFFFFu /*append*/, (ULONG_PTR)us, (ULONG_PTR)tm };
    void* item = (void*)invokeGuarded(symAddr("FLmenu_CreateItem"), ia, 4, &ok);   // FLmenu_CreateItem_CaptionClick
    if (!ok || !item) return;
    pluginItemSetFields(item, tag, checked, false);
}

// Leaf onClick: FL fires code(data=TMethod.data, item). Read item+0x18 (tag) -> plugin -> toggle.
static void __fastcall PluginItemClick(void* data, void* item)
{
    (void)data;
    __try {
        long long tag = *(long long*)((char*)item + 0x18);
        if (tag >= 0 && (size_t)tag < g_pluginCache.size()) {
            PluginEntry& e = g_pluginCache[(size_t)tag];
            // Plugin enable/disable can call back into FL's main thread. Keep the UI callback free
            // while managed code waits for completion; the worker posts the eventual menu refresh.
            queuePluginToggle(e.id, !e.enabled);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}

// --- more POD-only SEH helpers + FL-list wrappers ---
static void* readPtrAt(void* base, int off)
{ __try { return *(void**)((char*)base + off); } __except (EXCEPTION_EXECUTE_HANDLER) { return NULL; } }
static void writePtrAt(void* base, int off, void* v)
{ __try { *(void**)((char*)base + off) = v; } __except (EXCEPTION_EXECUTE_HANDLER) {} }
static void writeByteAt(void* base, int off, unsigned char v)
{ __try { *(unsigned char*)((char*)base + off) = v; } __except (EXCEPTION_EXECUTE_HANDLER) {} }
static bool itemCaptionIs(void* item, const wchar_t* want)   // item+0x78 = Delphi UStr (len @ ptr-4)
{
    __try {
        wchar_t* p = *(wchar_t**)((char*)item + 0x78);
        if (!p) return false;
        int len = *(int*)((char*)p - 4), wl = (int)wcslen(want);
        if (len != wl) return false;
        for (int i = 0; i < wl; i++) if (p[i] != want[i]) return false;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
static int   flChildCount(void* item) { bool ok; ULONG_PTR a[1] = { (ULONG_PTR)item }; return (int)invokeGuarded(symAddr("FL_ChildCount"), a, 1, &ok); }
static void* flChildAt(void* item, int i) { bool ok; ULONG_PTR a[2] = { (ULONG_PTR)item, (ULONG_PTR)(unsigned)i }; return (void*)invokeGuarded(symAddr("FL_ChildAt"), a, 2, &ok); }
static void  flFreeObj(void* o) { bool ok; ULONG_PTR a[1] = { (ULONG_PTR)o }; invokeGuarded(symAddr("FL_FreeObj"), a, 1, &ok); }
// Remove element `idx` from a raw FL TList (count@+0x10, array@+0x8): FUN_0064f4d0 decrements the count
// and back-fills the slot. This is the step flFreeObj (the object destructor) does NOT do — it frees the
// item but leaves its pointer in the parent's child TList, so re-populating a menu would APPEND dupes.
static void  flListRemoveAt(void* listObj, int idx) { bool ok; ULONG_PTR a[2] = { (ULONG_PTR)listObj, (ULONG_PTR)(unsigned)idx }; invokeGuarded(symAddr("FL_ListRemoveAt"), a, 2, &ok); }

// Detach + free EVERY child of a menu `item`. A menu item's children live in the TList at item+0xb0
// (re/16). We must (1) list-remove each child so item's child-count actually drops, THEN (2) free the
// detached object — otherwise a repeated populate STACKS duplicates (the "two Settings groups / two
// plugin rows" bug, 2026-07-01: install is retried each 1.5s until the manager is ready, so populate ran
// twice). onClick thunk (+0x100/+0x108) and the parent backref (+0xbc) are cleared first so the
// destructor is inert and cannot re-touch the list we are managing. MAIN thread.
static void  clearMenuChildren(void* item)
{
    void* listObj = readPtrAt(item, 0xb0);                 // the child TList (NULL until first child added)
    if (!listObj) return;
    for (int g = 0; g < 4096; g++) {
        int c = flChildCount(item);
        if (c <= 0) break;
        void* ch = flChildAt(item, c - 1);                // last child
        if (ch) { writePtrAt(ch, 0x100, NULL); writePtrAt(ch, 0x108, NULL); writePtrAt(ch, 0xbc, NULL); }
        flListRemoveAt(listObj, c - 1);                   // the MISSING step — TList count--
        if (ch) flFreeObj(ch);                            // free the now-detached object (+ its own subtree)
    }
}
static void  flSetBounds(void* c, int x, int y, int w, int h)
{ bool ok; void** vt = *(void***)c; ULONG_PTR a[5] = { (ULONG_PTR)c, (ULONG_PTR)(unsigned)x, (ULONG_PTR)(unsigned)y, (ULONG_PTR)(unsigned)w, (ULONG_PTR)(unsigned)h }; invokeGuarded(*(void**)((char*)vt + 0x188), a, 5, &ok); }
static void  flRender(void* c) { bool ok; ULONG_PTR a[1] = { (ULONG_PTR)c }; invokeGuarded(symAddr("FLwp_Render"), a, 1, &ok); }

// Find a direct child of `parent` whose caption matches (dedup across reloads / FL rebuilds).
static void* findChildByCaption(void* parent, const wchar_t* cap)
{
    int c = flChildCount(parent);
    for (int i = 0; i < c; i++) { void* ch = flChildAt(parent, i); if (ch && itemCaptionIs(ch, cap)) return ch; }
    return NULL;
}
// ===================== Settings submenu (task #61) =====================
// One row per toggleable setting; the Settings submenu is built by looping this table, so adding a new
// boolean setting later is a single line here (caption + its get/set glue). cur() returns the current
// state for the ✓ glyph; apply(v) sets it. Both degrade to no-op/0 when the CLR host isn't loaded.
typedef int  (*SettingCurFn)(void);
typedef void (*SettingApplyFn)(int);
struct SettingDef { const wchar_t* caption; SettingCurFn cur; SettingApplyFn apply; };
static int  setShowDebug_get(void)    { return callDebugVisibleGet(); }
static void setShowDebug_apply(int v) { callDebugVisibleSet(v); }
static const SettingDef g_settings[] = {
    { L"Show Debug Output", setShowDebug_get, setShowDebug_apply },
    // Add future settings here, e.g. { L"Some Toggle", someGet, someSet },
};
static const int g_settingsCount = (int)(sizeof(g_settings) / sizeof(g_settings[0]));

// Leaf onClick for a Settings item: read item+0x18 (tag = index into g_settings) -> toggle it ->
// refresh the ✓ after the popup closes. Distinct from PluginItemClick (different tag table).
static void __fastcall SettingItemClick(void* data, void* item)
{
    (void)data;
    __try {
        long long tag = *(long long*)((char*)item + 0x18);
        if (tag >= 0 && tag < g_settingsCount) {
            const SettingDef& s = g_settings[(int)tag];
            int cur = s.cur ? s.cur() : 0;
            if (s.apply) s.apply(cur ? 0 : 1);                        // toggle
            if (g_mainWnd) PostMessageW(g_mainWnd, WM_BRIDGE_PLUGINSINSTALL, 0, 0); // rebuild -> refresh ✓
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}

// Create a child SUBMENU item (no onClick — FL expands it as a ▸ submenu once it has children). Used
// for the nested "Settings" entry, the same recipe as the "FL Plugins" parent. Returns the item.
static void* addSubmenuItem(void* parent, const wchar_t* caption)
{
    bool ok;
    void* us = makeUStr(caption);
    if (!us) return NULL;
    void* tm[2] = { 0, 0 };
    ULONG_PTR ia[4] = { (ULONG_PTR)parent, (ULONG_PTR)0xFFFFFFFFu /*append*/, (ULONG_PTR)us, (ULONG_PTR)tm };
    void* item = (void*)invokeGuarded(symAddr("FLmenu_CreateItem"), ia, 4, &ok);     // FLmenu_CreateItem_CaptionClick
    return (ok && item) ? item : NULL;
}

// Append one checkable setting row (✓ glyph carried in the caption — same safe, fully-FL-initialized
// item the plugin rows use, so no popup-render crash from custom flag pokes). tag = g_settings index.
static void addSettingMenuItem(void* root, const wchar_t* name, int tag, bool checked)
{
    bool ok;
    std::wstring cap = std::wstring(checked ? L"\x2713  " : L"     ") + name;
    void* us = makeUStr(cap.c_str());
    if (!us) return;
    void* tm[2]; tm[0] = (void*)&SettingItemClick; tm[1] = g_pluginsCtx;
    ULONG_PTR ia[4] = { (ULONG_PTR)root, (ULONG_PTR)0xFFFFFFFFu /*append*/, (ULONG_PTR)us, (ULONG_PTR)tm };
    void* item = (void*)invokeGuarded(symAddr("FLmenu_CreateItem"), ia, 4, &ok);     // FLmenu_CreateItem_CaptionClick
    if (!ok || !item) return;
    pluginItemSetFields(item, tag, checked, false);
}

// (Re)build the Settings submenu's children from g_settings. MAIN thread.
static void populateSettingsChildren(void* settingsItem)
{
    clearMenuChildren(settingsItem);
    for (int k = 0; k < g_settingsCount; k++) {
        int cur = g_settings[k].cur ? g_settings[k].cur() : 0;
        addSettingMenuItem(settingsItem, g_settings[k].caption, k, cur != 0);
    }
}

// (Re)build the "Plugins" item's submenu (children @ item+0xb0): a nested "Settings ▸" submenu at the
// TOP, a separator, then the LIVE plugin list. MAIN thread.
static void populatePluginChildren(void* item)
{
    clearMenuChildren(item);

    // 1) Settings ▸ (nested submenu) at the top, then a separator, visually splitting it from plugins.
    g_settingsItem = addSubmenuItem(item, L"Settings");
    if (g_settingsItem) populateSettingsChildren(g_settingsItem);
    addPluginMenuItem(item, L"-", -1, false, true);     // separator row (inert; same safe tag<0 path)

    // 2) the plugin list (unchanged).
    std::string js = callPluginList();
    bool hostReady = !(js.empty() || js == "null");
    if (hostReady) parsePluginsJson(js); else g_pluginCache.clear();
    if (!hostReady)                addPluginMenuItem(item, L"Plugin host not ready", -1, false, true);
    else if (g_pluginCache.empty())addPluginMenuItem(item, L"No plugins installed", -1, false, true);
    else for (size_t k = 0; k < g_pluginCache.size(); k++)
        addPluginMenuItem(item, g_pluginCache[k].name.c_str(), (int)k, g_pluginCache[k].enabled, false);
}
// Recursively clear our onClick thunks across a menu subtree (FL Plugins -> Settings -> items, plus
// the plugin rows), so no menu interaction can enter the DLL after it unmaps. Memory-only, any thread,
// depth-limited. (The Settings sub-children carry SettingItemClick thunks one level deeper than the
// plugin rows, hence the recursion.)
static void clearThunksRec(void* item, int depth)
{
    if (!item || depth > 4) return;
    __try {
        void* list = *(void**)((char*)item + 0xb0);
        if (list) {
            int cnt = *(int*)((char*)list + 0x10);
            void** arr = *(void***)((char*)list + 0x8);
            if (arr) for (int i = 0; i < cnt && i < 512; i++) {
                void* ch = arr[i];
                if (ch) {
                    *(void**)((char*)ch + 0x100) = 0;
                    *(void**)((char*)ch + 0x108) = 0;
                    clearThunksRec(ch, depth + 1);   // clear grandchildren (Settings items) too
                }
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}
// Memory-only (any-thread) emergency: clear our child onClick thunks (recursively) + hide the item, so
// an unmapped DLL can never be entered. No FL calls / no rebuild (that needs the main thread).
static void clearChildrenThunksMem(void* item)
{
    if (!item) return;
    clearThunksRec(item, 0);
    __try { *(unsigned char*)((char*)item + 0x86) = 0; } __except (EXCEPTION_EXECUTE_HANDLER) {}   // hide from bar
}

// Locate the stock "Tools" top-level menu item (caption "&Tools", fallback "Tools").
static void* findToolsItem(void* masterRoot)
{
    void* t = findChildByCaption(masterRoot, L"&Tools");
    if (!t) t = findChildByCaption(masterRoot, L"Tools");
    return t;
}

// Add (or re-assert) the "Plugins" submenu inside the Tools dropdown + (re)build its plugin children.
// No bar rebuild / no width change — it's a dropdown item, so FL expands it natively on open. MAIN thread.
static bool DoPluginsInstall()
{
    bool ok;
    void* actionList = getActionListPtr();
    if (!actionList) return false;
    void* masterRoot = readPtrAt(actionList, 0x7c);     // *(actionList+0x7c) = top-menu container
    if (!masterRoot) return false;
    void* tools = findToolsItem(masterRoot);
    if (!tools) return false;
    g_pluginsTools = tools;

    void* item = findChildByCaption(tools, L"FL Plugins"); // dedup across reloads
    if (!item) {                                        // PREPEND "FL Plugins" as the FIRST Tools item
        void* us = makeUStr(L"FL Plugins");             // "FL " = our agentic plugins, not VST/instruments
        if (!us) return false;
        void* tm[2] = { 0, 0 };                         // no onClick on the submenu parent (FL expands it)
        ULONG_PTR ia[4] = { (ULONG_PTR)tools, (ULONG_PTR)0 /*index 0 = first in Tools*/, (ULONG_PTR)us, (ULONG_PTR)tm };
        item = (void*)invokeGuarded(symAddr("FLmenu_CreateItem"), ia, 4, &ok);   // FLmenu_CreateItem_CaptionClick
        if (!ok || !item) return false;
    }
    g_pluginsItem = item;
    populatePluginChildren(item);                       // children make it a submenu (▸) in FL
    g_pluginsInstallOk = true;
    return true;
}

// Eject-safe removal (MAIN thread): clear our child onClick thunks, then free + detach our "Plugins"
// item from the Tools submenu (FUN_0040faa0 removes it from the parent list and frees the subtree).
static bool DoPluginsRemove()
{
    if (g_pluginsItem) {
        clearThunksRec(g_pluginsItem, 0);               // neutralize ALL descendant onClick thunks (incl. Settings items) first
        clearMenuChildren(g_pluginsItem);               // detach + free every child (list-remove, not just free)
        // Detach "FL Plugins" itself from the Tools child TList before freeing it — flFreeObj alone leaves
        // a dangling pointer in Tools+0xb0 → AV the next time the Tools menu opens.
        if (g_pluginsTools) {
            void* toolsList = readPtrAt(g_pluginsTools, 0xb0);
            int tc = flChildCount(g_pluginsTools);
            for (int i = 0; i < tc; i++) { if (flChildAt(g_pluginsTools, i) == g_pluginsItem) { if (toolsList) flListRemoveAt(toolsList, i); break; } }
        }
        writePtrAt(g_pluginsItem, 0x100, NULL); writePtrAt(g_pluginsItem, 0x108, NULL); writePtrAt(g_pluginsItem, 0xbc, NULL);
        flFreeObj(g_pluginsItem);                       // free the now-detached "FL Plugins" item
    }
    g_pluginsItem = NULL; g_pluginsTools = NULL; g_settingsItem = NULL;
    return true;
}

// ===================== Generic plugin menu contributions (re/16) =====================
// Materializes plugin-contributed entries (from the managed MenuContributionRegistry, via the CLR
// host's FlClr_GetMenuFns export) into FL's native top-level dropdowns (File/Edit/Add/Patterns/View/
// Options/Tools/Help). Each contribution is appended as a leaf item to its target menu's child list,
// so FL renders it natively among that menu's own items (e.g. the FL Agent toggle in View). Toggles
// carry a ✓ glyph in the caption reflecting their live checked-state; clicking fires the managed
// handler by id, then posts a rebuild so the ✓ refreshes. Eject-safe: every item we create is tracked
// so we can clear its onClick thunk + free it on teardown. This is ADDITIVE to (and independent of)
// the Tools > FL Plugins submenu above.
struct MenuContrib { std::string id; std::wstring caption; std::string menu; bool toggle; bool checked; };
static std::vector<MenuContrib> g_menuContribs;   // current set (index == item tag)
static std::vector<void*>       g_menuItems;      // native items we created (for rebuild + teardown)
static void* g_menuCtx = (void*)0x554E454DULL;    // 'MENU' — non-null TMethod.data for our items

// Map a FlNativeMenu name to FL's top-level menu caption (with '&' accelerator) + a bare fallback.
static void* findTopMenu(void* masterRoot, const std::string& m)
{
    const wchar_t* amp =
        (m == "File")     ? L"&File"     : (m == "Edit")    ? L"&Edit"    :
        (m == "Add")      ? L"&Add"      : (m == "Patterns")? L"&Patterns":
        (m == "View")     ? L"&View"     : (m == "Options") ? L"&Options" :
        (m == "Tools")    ? L"&Tools"    : (m == "Help")    ? L"&Help"    : NULL;
    if (!amp) return NULL;
    void* it = findChildByCaption(masterRoot, amp);
    if (!it) { std::wstring bare = utf8ToW(m); it = findChildByCaption(masterRoot, bare.c_str()); }
    return it;
}

// Parse the contributions JSON (matches MenuGlue.ListJson's fixed field order: id,menu,caption,kind,
// checked; only \\ and \" escaped). Mirrors parsePluginsJson.
static void parseMenuJson(const std::string& j)
{
    g_menuContribs.clear();
    size_t i = 0;
    while (true) {
        if (!jsonFindKey(j, i, "id")) break;
        std::string id, menu, caption, kind;
        if (!jsonReadStr(j, i, id)) break;
        if (!jsonFindKey(j, i, "menu") || !jsonReadStr(j, i, menu)) break;
        if (!jsonFindKey(j, i, "caption") || !jsonReadStr(j, i, caption)) break;
        if (!jsonFindKey(j, i, "kind") || !jsonReadStr(j, i, kind)) break;
        if (!jsonFindKey(j, i, "checked")) break;
        bool checked = (i < j.size() && j[i] == 't');
        MenuContrib c; c.id = id; c.caption = utf8ToW(caption); c.menu = menu; c.toggle = (kind == "toggle"); c.checked = checked;
        g_menuContribs.push_back(c);
    }
}

static void __fastcall MenuContribClick(void* data, void* item);   // fwd

// Clear our contributed items' onClick thunks then free + detach them (flFreeObj removes from the
// parent menu's child list and frees). Reverse order so list-index shifts don't matter. MAIN thread
// for the free; the thunk-clear is a plain memory write (safe any thread).
static void removeMenuContribItems()
{
    for (size_t k = g_menuItems.size(); k-- > 0; ) {
        void* it = g_menuItems[k];
        if (it) { writePtrAt(it, 0x100, NULL); writePtrAt(it, 0x108, NULL); flFreeObj(it); }
    }
    g_menuItems.clear();
}

// Read item+0x78 (Delphi UnicodeString) into `out` with '&' accelerators stripped and ASCII letters
// lower-cased, so a live caption like "&Playlist" compares as "playlist". Returns the written length
// (>=0) or -1 on fault/none. POD-only — no C++ objects in the __try frame (MSVC C2712).
static int readItemCaptionDeaccel(void* item, wchar_t* out, int cap)
{
    __try {
        wchar_t* p = *(wchar_t**)((char*)item + 0x78);
        if (!p) return -1;
        int len = *(int*)((char*)p - 4);                          // Delphi UStr length (chars) at ptr-4
        if (len < 0) return -1;
        int n = 0;
        for (int i = 0; i < len && n < cap - 1; i++) {
            wchar_t ch = p[i];
            if (ch == L'&') continue;                             // drop the accelerator marker
            if (ch >= L'A' && ch <= L'Z') ch = (wchar_t)(ch - L'A' + L'a');
            out[n++] = ch;
        }
        out[n] = L'\0';
        return n;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
}

// FL's View dropdown opens with the window show/hide toggles — Playlist / Piano roll / Channel rack /
// Mixer / Browser — as its first group (what we call the "Windows" category; see re/23-menu-categories
// .md). Return the child index of the FIRST such item so a View contribution can be inserted at the TOP
// of that group (BEFORE it) instead of appended to the bottom of View. Returns -1 if none is found (the
// caller then appends, preserving the previous behavior). Matching is de-accelerated + case-insensitive
// (live FL items carry an '&', e.g. "&Playlist"); the set is order-independent, so the first child that
// matches any window name is the group's top regardless of FL's internal ordering.
static int viewWindowsTopIndex(void* viewMenu)
{
    static const wchar_t* const kWin[] = { L"playlist", L"piano roll", L"channel rack", L"mixer", L"browser" };
    int n = flChildCount(viewMenu);
    for (int i = 0; i < n; i++) {
        void* ch = flChildAt(viewMenu, i);
        if (!ch) continue;
        wchar_t cap[64];
        if (readItemCaptionDeaccel(ch, cap, 64) <= 0) continue;
        for (int k = 0; k < (int)(sizeof(kWin) / sizeof(kWin[0])); k++)
            if (wcscmp(cap, kWin[k]) == 0) return i;
    }
    return -1;
}

// Append (index<0) or INSERT-AT-index one contribution as a leaf item under its target top-level menu.
// tag = index into g_menuContribs (read on click). Same safe, fully-FL-initialized item the plugin rows
// use (✓ glyph in the caption, no custom flag pokes that crash FL's popup renderer). `index` is passed
// straight to FLmenu_CreateItem_CaptionClick -> FLmenu_InsertChild (index<0 = append, 0 = first child).
static void addMenuContribItem(void* topMenu, const MenuContrib& c, int tag, int index)
{
    bool ok;
    std::wstring cap = c.toggle ? (std::wstring(c.checked ? L"\x2713  " : L"     ") + c.caption) : c.caption;
    void* us = makeUStr(cap.c_str());
    if (!us) return;
    void* tm[2]; tm[0] = (void*)&MenuContribClick; tm[1] = g_menuCtx;
    ULONG_PTR ia[4] = { (ULONG_PTR)topMenu, (ULONG_PTR)(unsigned)index, (ULONG_PTR)us, (ULONG_PTR)tm };
    void* item = (void*)invokeGuarded(symAddr("FLmenu_CreateItem"), ia, 4, &ok);   // FLmenu_CreateItem_CaptionClick
    if (!ok || !item) return;
    pluginItemSetFields(item, tag, c.checked, false);              // item+0x18 = tag (index)
    g_menuItems.push_back(item);
}

// Leaf onClick: FL fires code(data, item) on the UI thread. Read item+0x18 (tag) -> contribution id
// -> invoke the managed handler -> post a rebuild so the ✓ refreshes after the popup closes. No
// std::string locals in the __try frame (MSVC C2712); the id is passed by const ref from the cache.
static void __fastcall MenuContribClick(void* data, void* item)
{
    (void)data;
    long long tag = -1;
    __try { tag = *(long long*)((char*)item + 0x18); } __except (EXCEPTION_EXECUTE_HANDLER) { tag = -1; }
    if (tag >= 0 && (size_t)tag < g_menuContribs.size()) {
        callMenuInvoke(g_menuContribs[(size_t)tag].id);
        if (g_mainWnd) PostMessageW(g_mainWnd, WM_BRIDGE_MENUCONTRIBINSTALL, 0, 0);
    }
}

// (Re)materialize all plugin menu contributions into FL's native menus. Idempotent: removes our prior
// items first, then re-adds from the current managed list. MAIN thread.
static bool DoMenuContribInstall()
{
    removeMenuContribItems();                       // clear our previous items (rebuild)
    void* actionList = getActionListPtr();
    if (!actionList) return false;
    void* masterRoot = readPtrAt(actionList, 0x7c);
    if (!masterRoot) return false;

    std::string js = callMenuList();
    if (js.empty() || js == "null") { g_menuContribs.clear(); return true; } // host not ready: nothing to add (not an error)
    parseMenuJson(js);
    for (size_t k = 0; k < g_menuContribs.size(); k++) {
        void* top = findTopMenu(masterRoot, g_menuContribs[k].menu);
        if (!top) continue;                         // unknown/absent target menu — skip this one
        int idx = -1;                               // default: append (bottom of the dropdown)
        if (g_menuContribs[k].menu == "View") {     // View entries (our FL Agent toggle) go at the TOP
            int wt = viewWindowsTopIndex(top);      // of the window-toggle ("Windows") group, not the end
            if (wt >= 0) idx = wt;                   // insert BEFORE the first window toggle (Playlist/…)
        }
        addMenuContribItem(top, g_menuContribs[k], (int)k, idx);
    }
    return true;
}

// Eject-safe removal (MAIN thread): clear onClick thunks + free/detach every contributed item.
static bool DoMenuContribRemove() { removeMenuContribItems(); return true; }

// Memory-only (any-thread) backstop: clear our contributed items' onClick thunks so no View/etc. menu
// click can enter the unmapping DLL even if the main-thread free path couldn't run.
static void clearMenuContribThunksMem()
{
    for (size_t k = 0; k < g_menuItems.size(); k++) {
        void* it = g_menuItems[k];
        if (it) { writePtrAt(it, 0x100, NULL); writePtrAt(it, 0x108, NULL); }
    }
}

// ===================== Plugin toolbar TOGGLE buttons (task #87, re/24-toolbar-buttons.md) =====================
// Clone of the menu-contribution pipeline above, swapping "native menu item" for a real TQuickBtn square
// TOGGLE button materialized onto FL's main toolbar. Buttons are (re)built from the managed
// FruityLink.Host.ToolbarGlue list (via the CLR host's FlClr_GetToolbarFns export). Clicks fire the
// managed handler by id; after each click we post a rebuild so the lit state re-syncs from the fresh list.
// Everything that touches FL widgets runs on FL's MAIN thread (WM_BRIDGE_TOOLBARINSTALL/REMOVE); the JSON
// pull + thunk-clear are memory-only and safe from any thread. Eject-safe teardown mirrors the menu path.

// ---- Toolbar glue: managed ToolbarGlue exports via FlClr_GetToolbarFns (clone of resolveMenuFns) ----
typedef int (*ToolbarListFn)(char*, int);          // managed ToolbarGlue.ContributionsJson(buf,len)
typedef int (*ToolbarInvokeFn)(const char*, int);  // managed ToolbarGlue.Invoke(idUtf8,idLen)
typedef int (*ToolbarActiveFn)(const char*, int);  // managed ToolbarGlue.Active(idUtf8,idLen)
static ToolbarListFn    g_toolbarListFn   = NULL;
static ToolbarInvokeFn  g_toolbarInvokeFn = NULL;
static ToolbarActiveFn  g_toolbarActiveFn = NULL;   // ABI-exposed for a WM_TIMER backstop; lit state
                                                    // currently syncs via the rebuild-from-list path.
static bool resolveToolbarFns()
{
    if (g_toolbarListFn && g_toolbarInvokeFn && g_toolbarActiveFn) return true;
    HMODULE h = GetModuleHandleA("FlClrHost.dll");
    if (!h) return false;
    typedef void (*GetFns)(void**, void**, void**);
    GetFns g = (GetFns)GetProcAddress(h, "FlClr_GetToolbarFns");
    if (!g) return false;
    void* l = NULL; void* iv = NULL; void* ac = NULL;
    __try { g(&l, &iv, &ac); } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    g_toolbarListFn = (ToolbarListFn)l; g_toolbarInvokeFn = (ToolbarInvokeFn)iv; g_toolbarActiveFn = (ToolbarActiveFn)ac;
    return g_toolbarListFn && g_toolbarInvokeFn && g_toolbarActiveFn;
}
// SEH-guarded managed invokes (no C++ objects in the __try frame — MSVC C2712).
static int callToolbarListRaw(char* b, int len)            { __try { return g_toolbarListFn(b, len); }    __except (EXCEPTION_EXECUTE_HANDLER) { return -1000; } }
static int callToolbarInvokeRaw(const char* id, int idlen) { __try { return g_toolbarInvokeFn(id, idlen); } __except (EXCEPTION_EXECUTE_HANDLER) { return -1000; } }
// Returns the managed toolbar-button list as JSON ("" if the host/glue is unavailable). Same buffer-resize
// protocol as callMenuList.
static std::string callToolbarList()
{
    if (!resolveToolbarFns()) return "";
    std::string buf; buf.resize(65536);
    int n = callToolbarListRaw(&buf[0], (int)buf.size());
    if (n == -1000 || n < 0) return "";
    if (n > (int)buf.size()) { buf.resize(n); n = callToolbarListRaw(&buf[0], (int)buf.size()); if (n == -1000 || n < 0) return ""; }
    buf.resize(n < (int)buf.size() ? n : (int)buf.size());
    return buf;
}
// Fire a button's handler by id. Returns 1 ok / 0 unknown / -1 no-manager / -2 no-host / -1000 fault.
static int callToolbarInvoke(const std::string& id)
{
    if (!resolveToolbarFns()) return -2;
    return callToolbarInvokeRaw(id.c_str(), (int)id.size());
}

// One materialized toolbar button. `btn` is the live TQuickBtn (NULL until created). We identify the
// clicked button by POINTER (the OnChange sender == our btn), so a single shared static ToolbarBtnClick
// suffices — no per-button RWX thunk needed (mirrors MenuContribClick's shared-handler + tag pattern).
struct ToolbarBtn { std::string id; std::wstring caption; bool toggle; bool active; void* btn; };
static std::vector<ToolbarBtn> g_toolbarBtns;          // current set (rebuilt from the managed list)
static void* g_toolbarCtx = (void*)0x524C4254ULL;      // 'TBLR' — non-null TMethod.data for our buttons

// Parse the toolbar JSON (matches ToolbarGlue.ContributionsJson's fixed field order: id,caption,kind,
// active,order; only \\ and \" escaped). Mirrors parseMenuJson.
static void parseToolbarJson(const std::string& j)
{
    g_toolbarBtns.clear();
    size_t i = 0;
    while (true) {
        if (!jsonFindKey(j, i, "id")) break;
        std::string id, caption, kind;
        if (!jsonReadStr(j, i, id)) break;
        if (!jsonFindKey(j, i, "caption") || !jsonReadStr(j, i, caption)) break;
        if (!jsonFindKey(j, i, "kind") || !jsonReadStr(j, i, kind)) break;
        if (!jsonFindKey(j, i, "active")) break;
        bool active = (i < j.size() && j[i] == 't');
        ToolbarBtn c; c.id = id; c.caption = utf8ToW(caption); c.toggle = (kind == "toggle"); c.active = active; c.btn = NULL;
        g_toolbarBtns.push_back(c);
    }
}

static void __fastcall ToolbarBtnClick(void* data, void* item);   // fwd

// EJECT-SAFE ordered teardown of every materialized button (MAIN thread for the vtbl calls; the thunk
// clears are plain memory writes, safe any thread). Clone of removeMenuContribItems + the re/24 §4b order:
//   1) zero our TMethods (onChange +0x1e4/+0x1ec, paint +0x49c/+0x4a4) so FL can never call into us,
//   2) hide vtbl[0x200](btn,0),  3) unparent vtbl[0x138](btn,0).
// Hide-and-leave: we do NOT free the control (no confirmed TQuickBtn destructor) — it stays inert and FL
// frees it when the toolbar form is destroyed. No RWX thunks to free (shared static click handler).
static void removeToolbarButtons()
{
    for (size_t k = g_toolbarBtns.size(); k-- > 0; ) {
        void* btn = g_toolbarBtns[k].btn;
        if (!btn) continue;
        writePtrAt(btn, 0x1e4, NULL); writePtrAt(btn, 0x1ec, NULL);   // onChange TMethod (code/data)
        writePtrAt(btn, 0x49c, NULL); writePtrAt(btn, 0x4a4, NULL);   // paint TMethod (code/data), if ever set
        bool ok; void** vt = NULL;
        __try { vt = *(void***)btn; } __except (EXCEPTION_EXECUTE_HANDLER) { vt = NULL; }
        if (vt) {
            ULONG_PTR h[2] = { (ULONG_PTR)btn, 0 }; invokeGuarded(*(void**)((char*)vt + 0x200), h, 2, &ok);  // hide
            ULONG_PTR p[2] = { (ULONG_PTR)btn, 0 }; invokeGuarded(*(void**)((char*)vt + 0x138), p, 2, &ok);  // unparent
        }
    }
    g_toolbarBtns.clear();
}

// Memory-only (any-thread) backstop: clear our buttons' TMethods so no toolbar click can enter the
// unmapping DLL even if the main-thread teardown couldn't run. Clone of clearMenuContribThunksMem.
static void clearToolbarThunksMem()
{
    for (size_t k = 0; k < g_toolbarBtns.size(); k++) {
        void* btn = g_toolbarBtns[k].btn;
        if (btn) { writePtrAt(btn, 0x1e4, NULL); writePtrAt(btn, 0x1ec, NULL); writePtrAt(btn, 0x49c, NULL); writePtrAt(btn, 0x4a4, NULL); }
    }
}

// Create one square TQuickBtn toggle on `panel` per the re/24 LIVE-VERIFIED recipe. Laid out right-to-left
// (idx 0 = rightmost) near the sys buttons; `s`=square size, `y`=top, `pw`=panel width (for the right edge).
static void* makeToolbarButton(void* panel, const ToolbarBtn& c, int idx, int s, int y, int pw)
{
    bool ok;
    void* btn = (void*)invokeGuarded(symAddr("FLwp_CreateButtonControl"), NULL, 0, &ok);     // FLwp_CreateButtonControl() -> TQuickBtn
    if (!ok || !btn) return NULL;
    void** vt = *(void***)btn;
    // Make it a 2-state TOGGLE (momentary "button" kind skips this so it fires once, no latched look).
    if (c.toggle) {
        ULONG_PTR t2[2] = { (ULONG_PTR)btn, 2 }; invokeGuarded(*(void**)((char*)vt + 0x1e8), t2, 2, &ok); // vtbl[0x1e8](btn,2)
        __try { *(int*)((char*)btn + 0x48a) |= 0x4001; } __except (EXCEPTION_EXECUTE_HANDLER) {}          // toggle/latch flags
    }
    // Caption/glyph — FUN_005d0ae0 takes a raw PWideChar (proven by makeButton's L"Send"); c_str() matches.
    ok = setControlCaption(btn, c.caption.c_str());
    // Parent onto the ALWAYS-present primary top panel (NOT the .tpr customizable area).
    { ULONG_PTR pa[2] = { (ULONG_PTR)btn, (ULONG_PTR)panel }; invokeGuarded(*(void**)((char*)vt + 0x138), pa, 2, &ok); }
    // Square bounds; anchor fix-right so it stays pinned as the toolbar reflows.
    int gap = 2, rightReserve = 108;                                  // leave room for the min/max/close sys btns
    int x = pw - rightReserve - idx * (s + gap) - s; if (x < 0) x = 0;
    { ULONG_PTR bd[5] = { (ULONG_PTR)btn, (ULONG_PTR)(unsigned)x, (ULONG_PTR)(unsigned)y, (ULONG_PTR)(unsigned)s, (ULONG_PTR)(unsigned)s };
      invokeGuarded(*(void**)((char*)vt + 0x188), bd, 5, &ok); }      // SetBounds
    { ULONG_PTR al[2] = { (ULONG_PTR)btn, 4 }; invokeGuarded(symAddr("FLui_WP_SetAlign"), al, 2, &ok); }                 // FLui_WP_SetAlign(btn,4)
    // Initial lit state (toggles only): drive the value setter + write the live state byte.
    if (c.toggle) {
        ULONG_PTR sv[2] = { (ULONG_PTR)btn, (ULONG_PTR)(unsigned)(c.active ? 1 : 0) };
        invokeGuarded(symAddr("FLwp_SetControlValue"), sv, 2, &ok);                      // FLwp_SetControlValue(btn,0|1)
        __try { *(unsigned char*)((char*)btn + 0x492) = (unsigned char)(c.active ? 1 : 0); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    { ULONG_PTR sh[2] = { (ULONG_PTR)btn, 1 }; invokeGuarded(*(void**)((char*)vt + 0x200), sh, 2, &ok); } // show
    { ULONG_PTR rp[1] = { (ULONG_PTR)btn };    invokeGuarded(*(void**)((char*)vt + 0x178), rp, 1, &ok); } // repaint
    // onChange TMethod: FL fires code(data, sender) on the UI thread — data FIRST, then code.
    __try {
        *(void**)((char*)btn + 0x1ec) = g_toolbarCtx;
        *(void**)((char*)btn + 0x1e4) = (void*)&ToolbarBtnClick;
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    return btn;
}

// OnChange handler: FL fires code(data, sender) on the UI thread; `item` == the btn that changed. Match it
// by pointer -> contribution id -> invoke the managed handler -> post a rebuild so the lit state re-syncs
// from the fresh list. Clone of MenuContribClick (pointer identity instead of the item+0x18 tag).
static void __fastcall ToolbarBtnClick(void* data, void* item)
{
    (void)data;
    int found = -1;
    for (size_t k = 0; k < g_toolbarBtns.size(); k++) {
        if (g_toolbarBtns[k].btn == item) { found = (int)k; break; }
    }
    if (found < 0) return;
    callToolbarInvoke(g_toolbarBtns[(size_t)found].id);
    if (g_mainWnd) PostMessageW(g_mainWnd, WM_BRIDGE_TOOLBARINSTALL, 0, 0);
}

// (Re)materialize all plugin toolbar buttons. Idempotent: tears down our prior buttons first, then
// re-creates from the current managed list. MAIN thread. Clone of DoMenuContribInstall.
static bool DoToolbarInstall()
{
    removeToolbarButtons();                          // clear our previous buttons (rebuild)
    // Toolbar form = *(*(PTR_DAT_014aa4c8)) (DOUBLE deref, re/24 LIVE-VERIFIED); fixed panel = *(form+0x760).
    // readToolbarPtrs does the POD-only guarded double-deref (no __try here — MSVC C2712 with the std::string below).
    void* form = NULL; void* bar = NULL;
    readToolbarPtrs(&form, &bar);
    if (!form) return false;
    void* panel = readPtrAt(form, 0x760);
    if (!panel) return false;

    std::string js = callToolbarList();
    if (js.empty() || js == "null") { g_toolbarBtns.clear(); return true; }  // host not ready: nothing to add
    parseToolbarJson(js);

    // Read a sibling toggle (MetronomeBtn @ form+0x9f8) for a pixel-matched square size + y; else fall back.
    int s = 23, y = 2;
    void* sib = readPtrAt(form, 0x9f8);
    if (sib) {
        int sh = readIntAt(sib, 0x9c, 0);   // sibling height @+0x9c
        int sy = readIntAt(sib, 0x94, -1);  // sibling y      @+0x94
        if (sh >= 12 && sh <= 64) s = sh;
        if (sy >= 0)              y = sy;
    }
    int pw = readIntAt(panel, 0x98, 800);   // panel width @+0x98 (right-edge anchor reference)

    for (size_t k = 0; k < g_toolbarBtns.size(); k++)
        g_toolbarBtns[k].btn = makeToolbarButton(panel, g_toolbarBtns[k], (int)k, s, y, pw);
    return true;
}

// Eject-safe removal (MAIN thread): teardown every materialized button. Clone of DoMenuContribRemove.
static bool DoToolbarRemove() { removeToolbarButtons(); return true; }

// Native forms are owned by FL's main thread; the registry and callbacks live for the process.
// Each command carries isolated request/response state through synchronous main-thread dispatch.
static HWND windowHostMainWindow() { return g_mainWnd.load(); }
static NativeWindowRegistry& windowHosts()
{
    static auto* factory = new FlWindowFactory(symAddr, invokeGuarded, windowHostMainWindow);
    static auto* registry = new NativeWindowRegistry(*factory);
    return *registry;
}
struct PendingWindowCommand { std::string request; std::string response = "{\"ok\":0,\"reason\":\"main-window-unavailable\"}"; };

static LRESULT CALLBACK subProc(HWND h, UINT msg, WPARAM w, LPARAM l)
{
    if (msg == WM_BRIDGE_AUTOMATION) {
        auto pending = reinterpret_cast<PendingWindowCommand*>(l);
        if (pending) {
            FlAutomationBackend backend(symAddr, invokeGuarded, sig_automationLayout);
            pending->response = executeAutomationCommand(pending->request, backend);
        }
        return 0;
    }
    if (msg == WM_BRIDGE_MIXERADD) {
        auto pending = reinterpret_cast<PendingWindowCommand*>(l);
        if (pending) {
            FlMixerTrackInsertionBackend backend(symAddr, invokeGuarded, sig_mixerLayout);
            pending->response = executeMixerTrackInsertion(pending->request, backend);
        }
        return 0;
    }
    if (msg == WM_BRIDGE_CHATOPEN)  { g_chatOpenOk = DoChatTabOpen();  return 0; }
    if (msg == WM_BRIDGE_CHATCLOSE) { DoChatTabClose();                return 0; }
    if (msg == WM_BRIDGE_CHATSAY)   { sayMain();                       return 0; }
    if (msg == WM_BRIDGE_CHATPOLL)  { takeInput(g_pollForce); g_pollResult = g_chatIn; g_chatIn.clear(); return 0; }
    if (msg == WM_BRIDGE_PLUGINSINSTALL) { g_pluginsInstallOk = DoPluginsInstall(); return 0; }
    if (msg == WM_BRIDGE_PLUGINSREMOVE)  { DoPluginsRemove();                       return 0; }
    if (msg == WM_BRIDGE_MENUCONTRIBINSTALL) { DoMenuContribInstall(); return 0; }
    if (msg == WM_BRIDGE_MENUCONTRIBREMOVE)  { DoMenuContribRemove();  return 0; }
    if (msg == WM_BRIDGE_TOOLBARINSTALL) { DoToolbarInstall(); return 0; }
    if (msg == WM_BRIDGE_TOOLBARREMOVE)  { DoToolbarRemove();  return 0; }
    if (msg == WM_BRIDGE_WINHOST_EMBED) {
        auto pending = reinterpret_cast<PendingWindowCommand*>(l);
        if (pending) pending->response = windowHosts().execute(pending->request);
        return 0;
    }
    if (msg == WM_BRIDGE_WINHOST_CLOSE) { windowHosts().closeDetached(); return 0; }
    if (msg == WM_BRIDGE_CALL) {
        auto* pending = reinterpret_cast<PendingCall*>(l);
        if (!pending) return 0;
        if (pending->useXmm)
            pending->ret = invokeGuardedXmm(pending->fn, pending->arg, &pending->xmm0, &pending->ok);
        else
            pending->ret = invokeGuarded(pending->fn, pending->arg, pending->argc, &pending->ok);
        return 0;
    }
    WNDPROC original = g_origProc;
    if (msg == WM_NCDESTROY) {
        AcquireSRWLockExclusive(&g_subclassLock);
        if (g_mainWnd == h) { g_mainWnd = NULL; g_origProc = NULL; }
        ReleaseSRWLockExclusive(&g_subclassLock);
    }
    // A corrupt or duplicate hook must never recurse through itself.
    return original && original != subProc ? CallWindowProcW(original, h, msg, w, l) : DefWindowProcW(h, msg, w, l);
}

static bool ensureSubclassed()
{
    AcquireSRWLockExclusive(&g_subclassLock);
    bool installed = false;
    if (g_mainWnd && IsWindow(g_mainWnd) && g_origProc && g_origProc != subProc) installed = true;
    else {
        g_mainWnd = NULL; g_origProc = NULL;
        HWND w = findMainWindow();
        WNDPROC original = w ? reinterpret_cast<WNDPROC>(GetWindowLongPtrW(w, GWLP_WNDPROC)) : NULL;
        if (original && original != subProc) {
            // Publish the forwarding target before the replacement can receive a message.
            g_mainWnd = w; g_origProc = original;
            WNDPROC previous = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(w, GWLP_WNDPROC, (LONG_PTR)subProc));
            installed = previous && previous != subProc;
            if (installed) g_origProc = previous;
            else { g_mainWnd = NULL; g_origProc = NULL; }
        }
    }
    ReleaseSRWLockExclusive(&g_subclassLock);
    return installed;
}

static void revertSubclass(bool waitForOwner = true)
{
    if (waitForOwner) AcquireSRWLockExclusive(&g_subclassLock);
    else if (!TryAcquireSRWLockExclusive(&g_subclassLock)) return;
    if (g_mainWnd && g_origProc) {
        if (IsWindow(g_mainWnd) && GetWindowLongPtrW(g_mainWnd, GWLP_WNDPROC) == (LONG_PTR)subProc)
            SetWindowLongPtrW(g_mainWnd, GWLP_WNDPROC, (LONG_PTR)g_origProc.load());
        g_origProc = NULL; g_mainWnd = NULL;
    }
    ReleaseSRWLockExclusive(&g_subclassLock);
}

// Run fn on the main thread via the subclass + SendMessage. Returns false if no main window.
static bool callOnMain(void* fn, ULONG_PTR* a, int argc, ULONG_PTR* ret, bool* ok,
                       bool useXmm = false, double* xmm0 = NULL)
{
    if (!ensureSubclassed()) return false;
    HWND target = g_mainWnd.load();
    if (!target || !IsWindow(target)) return false;
    PendingCall pending{};
    pending.fn = fn; pending.argc = argc;
    pending.useXmm = useXmm;
    // XMM path always loads 4 register slots; pad with zero.
    for (int i = 0; i < 8; i++) pending.arg[i] = (i < argc) ? a[i] : 0;
    SendMessageW(target, WM_BRIDGE_CALL, 0, (LPARAM)&pending); // stack state remains alive until completion
    *ret = pending.ret; *ok = pending.ok;
    if (xmm0) *xmm0 = pending.xmm0;
    return true;
}

static DWORD mainThreadId()
{ HWND w = g_mainWnd.load(); if (!w) w = findMainWindow(); if (!w) return 0; DWORD pid; return GetWindowThreadProcessId(w, &pid); }

static int parseArgs(const char* p, ULONG_PTR* out) // space-separated hex; returns count (<=8)
{
    int c = 0; while (*p && c < 8) {
        while (*p == ' ') p++;
        if (!*p) break;
        unsigned long long v = 0; int n = 0;
        if (sscanf_s(p, "%llx%n", &v, &n) != 1 || n == 0) break;
        out[c++] = (ULONG_PTR)v; p += n;
    }
    return c;
}

static std::string hexDump(unsigned long long addr, size_t len)
{
    if (len == 0 || len > 4096) return "err:bad-len";
    std::string out; unsigned char* buf = (unsigned char*)malloc(len); if (!buf) return "err:oom";
    bool ok = safeRead((const unsigned char*)addr, buf, len);
    if (!ok) { free(buf); return "err:unreadable"; }
    char t[4]; for (size_t i = 0; i < len; i++) { sprintf_s(t, sizeof(t), "%02x", buf[i]); out += t; }
    free(buf); return out;
}

// True if request `s` begins with command prefix `p`.
static bool starts(const std::string& s, const char* p) { return s.rfind(p, 0) == 0; }

// Resolve a command address: absolute as-is, otherwise use the shared version-safe legacy resolver.
static unsigned long long resolveAddr(bool absolute, unsigned long long a, bool* ok)
{
    if (absolute) { *ok = true; return a; }
    sig_resolveAll();
    unsigned long long resolved = sig_legacyAddr(GetModuleHandleA("FLEngine_x64.dll"), sig_version(), a);
    *ok = resolved != 0;
    return resolved;
}

// Runtime signature-scanning wire support. If the address
// token at *pp begins with "sym:NAME", resolve NAME via the sigscan table to an ABSOLUTE address, advance
// *pp past the token, and return 1 (ok, *real set) or -1 (error, *err set: unknown-sym / unresolved). A
// `sym:` token is refused loudly rather than guessing — a wrong address is an uncatchable AV inside FL.
// Returns 0 when the token is NOT a sym token, so the caller falls through to the legacy hex path verbatim.
static int trySymToken(const char** pp, unsigned long long* real, std::string* err)
{
    const char* p = *pp;
    while (*p == ' ' || *p == '\t') p++;
    if (strncmp(p, "sym:", 4) != 0) return 0;
    p += 4;
    const char* nameStart = p;
    while (*p && *p != ' ' && *p != '\t') ++p;
    std::string name(nameStart, p);
    sig_resolveAll();                                   // idempotent; resolves-at-init if not done yet
    const SymEntry* e = sig_findSym(name.c_str());
    if (!e)                     { *err = std::string("err:unknown-sym:") + name; return -1; }
    if (e->status != RS_Ok)     { *err = std::string("err:unresolved:") + name; return -1; }
    *real = (unsigned long long)e->addr;
    *pp = p;
    return 1;
}

// SEH-guarded raw reads for handleCmd's status/loadstate commands. These MUST live OUTSIDE handleCmd:
// handleCmd has C++ objects (std::string) requiring unwinding, and MSVC forbids __try in such a function
// (C2712). No unwinding objects here → __try is legal.
static int readStatusTextRaw(char* buf, int cap)
{
    int n = 0;
    __try {
        wchar_t* P = *(wchar_t**)symAddr("StatusHintStr");
        if (P) {
            int len = *(int*)((char*)P - 4);
            if (len > 0 && len < 8192) {
                int need = WideCharToMultiByte(CP_UTF8, 0, P, len, NULL, 0, NULL, NULL);
                if (need > 0 && need < cap) n = WideCharToMultiByte(CP_UTF8, 0, P, len, buf, cap, NULL, NULL);
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { n = 0; }
    return n;
}
static void readLoadStateRaw(int* loading, int* busy)
{
    *loading = 1; *busy = 1;
    __try { *loading = *(unsigned char*)rb(0x157f667); *busy = *(int*)symAddr("BusyCounter"); } __except (EXCEPTION_EXECUTE_HANDLER) {}
}
// Set FL's status/hint bar text on the MAIN thread via FL's own FLui_SetStatusHintAndRefresh (@0x10ec870;
// param_1 = mainForm *(0x14A8750), param_2 = a Delphi UStr). It assigns DAT_015817d0, stamps the timer and
// repaints the hint bar; the wrapper passes flag=0 => the message is PERSISTENT (stays until overwritten),
// which is exactly what a "loading…" indicator wants. Used by the plugin-host loading indicator. Returns
// false if FL isn't up yet (mainForm null) — the SEH guard covers a too-early deref. Lives OUTSIDE handleCmd
// because __try can't coexist with C++ unwinding objects there (C2712). Caller must not pass an empty string
// (a null/empty Delphi UStr trips FL's "license expired" hint branch inside the core setter).
static bool setHintRaw(const wchar_t* text)
{
    void* mainForm = NULL;
    __try { mainForm = *(void**)symAddr("MainFormPtr"); } __except (EXCEPTION_EXECUTE_HANDLER) { mainForm = NULL; }
    if (!mainForm) return false;
    void* us = makeUStr(text);
    if (!us) return false;
    ULONG_PTR a[2] = { (ULONG_PTR)mainForm, (ULONG_PTR)us };
    ULONG_PTR ret = 0; bool ok = false;
    if (!callOnMain(symAddr("FLui_SetStatusHint"), a, 2, &ret, &ok)) return false;
    return ok;
}

static std::string handleCmd(const std::string& req)
{
    if (g_bridgeStopping.load()) return "err:bridge-stopping";
    // Resolve the signature-scanning table ONCE, up front — BEFORE any dispatch (including fl_ready).
    // sig_resolveAll() only needs FLEngine_x64.dll loaded (it scans .text), which is always true when we
    // are processing a command; it is idempotent (guarded by g_symsResolved) and a cheap no-op after the
    // first success. Doing it here DECOUPLES symbol resolution from the readiness gate: flIsReady() now
    // READS the resolved table instead of TRIGGERING the resolve, breaking the old chicken-and-egg where
    // a wrong hardcoded gate address (e.g. on FL 2026) kept sig_resolveAll from ever running and gated
    // the entire bridge off.
    sig_resolveAll();

    if (req == "ping") return "pong";

    // FL readiness gate (task #60): "1" once mainForm+toolbarForm+songObj+chanList are all allocated
    // (default project loaded), else "0". The managed bootstrap polls this before FL-state work. The
    // symbol table is already resolved (above), so this is a pure read of the version-portable table.
    if (req == "fl_ready") return flIsReady() ? "1" : "0";

    // Signature-scanning diagnostics (like `info`): {"ver":N,"ok":N,"fail":M,"unresolved":[{name,why}]}.
    // The managed tool registry uses this to mark tools whose symbol didn't resolve as UNAVAILABLE.
    if (req == "syms") { sig_resolveAll(); return sig_symsJson(); }

    // Internal managed transport uses this for FL addresses passed as arguments (e.g. typeinfo).
    if (starts(req, "resolve ")) {
        const char* p = req.c_str() + 8;
        unsigned long long real = 0;
        std::string error;
        int status = trySymToken(&p, &real, &error);
        if (status == -1) return error;
        while (*p == ' ' || *p == '\t') ++p;
        if (status != 1 || *p) return "err:usage resolve sym:NAME";
        char result[32]; sprintf_s(result, sizeof(result), "%llx", real);
        return result;
    }

    // Return the absolute address of a zeroed scratch buffer (pass to call as an out-param, then peekabs it).
    if (req == "scratch") {
        memset(g_scratch, 0, sizeof(g_scratch));
        char b[32]; sprintf_s(b, sizeof(b), "%llx", (unsigned long long)g_scratch);
        return b;
    }

    // Post a keystroke to FL's main window (safe transport/shortcut path; e.g. key 20 = Space = play/stop).
    if (starts(req, "key ")) {
        unsigned int vk = 0;
        if (sscanf_s(req.c_str() + 4, "%x", &vk) != 1) return "err:usage key <vkHex>";
        HWND w = findMainWindow();
        if (!w) return "err:no-wnd";
        PostMessageW(w, WM_KEYDOWN, vk, 0);
        PostMessageW(w, WM_KEYUP, vk, 0xC0000001);
        return "ok";
    }

    if (req == "info") {
        HMODULE eng = GetModuleHandleA("FLEngine_x64.dll");
        unsigned long long sz = 0;
        if (eng) { MODULEINFO mi{}; if (GetModuleInformation(GetCurrentProcess(), eng, &mi, sizeof(mi))) sz = mi.SizeOfImage; }
        char b[320]; sprintf_s(b, sizeof(b),
            "{\"pid\":%lu,\"bridgeBase\":\"0x%p\",\"flEngineBase\":\"0x%p\",\"flEngineSize\":%llu,\"mainTid\":%lu}",
            GetCurrentProcessId(), (void*)selfModule(), (void*)eng, sz, mainThreadId());
        return b;
    }

    if (req == "tid") {
        char b[96]; sprintf_s(b, sizeof(b), "{\"main\":%lu,\"worker\":%lu}", mainThreadId(), GetCurrentThreadId());
        return b;
    }

    // Read FL's current status/hint text (RE 2026-07-01). DAT_015817d0 is a Delphi UnicodeString holding the
    // live hint/status string (raw "tooltip|status" + '^' markup); companion globals +0x8 = timeGetTime()
    // stamp, +0x10 = auto-clear timeout ms (0 = persistent). Direct read — no FL call, safe on demand;
    // SEH-guarded against a mid-reassign race (returns "" on any fault). The managed side splits '|' + strips
    // '^' markup for a clean status string. Returns the RAW text (UTF-8); empty when there is no hint.
    if (req == "status") {
        char sb[8192]; int n = readStatusTextRaw(sb, sizeof(sb));
        return std::string(sb, (size_t)n);
    }

    // FL load/busy state (RE 2026-07-01, Agent B) — for diagnostics + SDK. loading=*(0x0157f667) is 1 while a
    // project/plugins load; busy=*(0x014bdbac) is the wait-cursor nesting counter (>0 during blocking ops).
    if (req == "fl_loadstate") {
        int loading, busy; readLoadStateRaw(&loading, &busy);
        char b[96]; sprintf_s(b, sizeof(b), "{\"loading\":%d,\"busy\":%d,\"idle\":%d}", loading, busy, (loading == 0 && busy == 0) ? 1 : 0);
        return b;
    }

    // Set FL's status/hint bar text — the plugin-host loading indicator (RE 2026-07-01). "hint <utf8 text>"
    // routes to FL's own hint setter on the main thread (see setHintRaw). The managed host shows a
    // "…loading plugin host…" message once FL is ready + before the heavy CoreCLR/Avalonia work, then
    // "…ready" once the chat UI is embedded. Empty text -> a single space (a null/empty Delphi UStr would
    // trip FL's "license expired" branch). Cheap: it just assigns a string + repaints; the heavy plugin
    // load runs off FL's main thread so the bar actually updates during the wait.
    if (starts(req, "hint ") || req == "hint") {
        const char* txt = (req.size() > 5) ? req.c_str() + 5 : "";
        wchar_t w[122];
        int wn = MultiByteToWideChar(CP_UTF8, 0, txt, -1, w, 121);
        if (wn <= 1) { w[0] = L' '; w[1] = 0; }   // empty/too-long -> space (dodge FL's license-message branch)
        w[121] = 0;
        return setHintRaw(w) ? "ok" : "err:hint";
    }

    if (req == "apctest") {
        // call GetCurrentThreadId on the main thread; ret should equal mainTid.
        void* fn = (void*)GetProcAddress(GetModuleHandleA("kernel32.dll"), "GetCurrentThreadId");
        if (!fn) return "err:no-fn";
        ULONG_PTR ret = 0; bool ok = false;
        if (!callOnMain(fn, NULL, 0, &ret, &ok)) return "err:no-mainwindow";
        char b[96]; sprintf_s(b, sizeof(b), "{\"ret\":%llu,\"main\":%lu,\"ok\":%d}", (unsigned long long)ret, mainThreadId(), ok ? 1 : 0);
        return b;
    }

    if (starts(req, "peek ") || starts(req, "peekabs ")) {
        bool abs = starts(req, "peekabs ");
        const char* p = req.c_str() + (abs ? 8 : 5);
        // sym: token support (additive) — resolves to an absolute address; else fall through to hex.
        {
            const char* sp = p; unsigned long long symReal = 0; std::string symErr;
            int st = trySymToken(&sp, &symReal, &symErr);
            if (st == -1) return symErr;
            if (st == 1) {
                unsigned int len2 = 0;
                if (sscanf_s(sp, " %u", &len2) != 1) return "err:usage peek <hex|sym:NAME> <len>";
                return hexDump(symReal, len2);
            }
        }
        unsigned long long a = 0; unsigned int len = 0;
        if (sscanf_s(p, "%llx %u", &a, &len) != 2) return "err:usage peek <hex> <len>";
        bool ok; unsigned long long real = resolveAddr(abs, a, &ok);
        if (!ok) return "err:no-flengine";
        return hexDump(real, len);
    }

    // call family: call/callabs (main thread, GP args), callhere (worker thread, GP args),
    // callf/callfabs (main thread, XMM-capable — args -> GP + XMM0-3, also reports XMM0 for
    // float-arg/float-return fns). All share one dispatch.
    {
        bool isCall     = starts(req, "call ");
        bool isCallAbs  = starts(req, "callabs ");
        bool isCallHere = starts(req, "callhere ");
        bool isCallF    = starts(req, "callf ");
        bool isCallFAbs = starts(req, "callfabs ");
        if (isCall || isCallAbs || isCallHere || isCallF || isCallFAbs) {
            int verblen = isCall ? 5 : isCallAbs ? 8 : isCallHere ? 9 : isCallF ? 6 : 9;
            const char* p = req.c_str() + verblen;
            bool absolute = isCallAbs || isCallFAbs;
            bool useXmm   = isCallF || isCallFAbs;
            bool onMain   = !isCallHere;
            unsigned long long real = 0; ULONG_PTR args[8]; int argc = 0;
            // sym: token support (additive) — resolves to an absolute address; else legacy hex path (verbatim).
            {
                const char* sp = p; unsigned long long symReal = 0; std::string symErr;
                int st = trySymToken(&sp, &symReal, &symErr);
                if (st == -1) return symErr;
                if (st == 1) {
                    real = symReal;                      // sym addresses are already absolute
                    argc = parseArgs(sp, args);
                } else {
                    unsigned long long a = 0; int n = 0;
                    if (sscanf_s(p, "%llx%n", &a, &n) != 1) return "err:usage call <hex|sym:NAME> [args]";
                    argc = parseArgs(p + n, args);
                    bool addrOk; real = resolveAddr(absolute, a, &addrOk);
                    if (!addrOk) return "err:no-flengine";
                }
            }
            ULONG_PTR ret = 0; bool ok = false; double xmm0 = 0.0;
            if (onMain) { if (!callOnMain((void*)real, args, argc, &ret, &ok, useXmm, &xmm0)) return "err:no-mainwindow"; }
            else        { ret = invokeGuarded((void*)real, args, argc, &ok); }
            char b[176];
            if (useXmm) {
                unsigned long long xbits; memcpy(&xbits, &xmm0, sizeof(xbits));
                sprintf_s(b, sizeof(b), "{\"ret\":\"0x%llx\",\"xmm0\":\"0x%llx\",\"ok\":%d,\"argc\":%d}",
                    (unsigned long long)ret, xbits, ok ? 1 : 0, argc);
            } else {
                sprintf_s(b, sizeof(b), "{\"ret\":\"0x%llx\",\"ok\":%d,\"argc\":%d}",
                    (unsigned long long)ret, ok ? 1 : 0, argc);
            }
            return b;
        }
    }

    if (starts(req, "poke ") || starts(req, "pokeabs ")) {
        bool abs = starts(req, "pokeabs ");
        const char* p = req.c_str() + (abs ? 8 : 5);
        unsigned long long real = 0; const char* hp = NULL; bool haveReal = false;
        unsigned long long a = 0; int adv = 0;
        // sym: token support (additive) — resolves to an absolute address; else legacy hex path (verbatim).
        {
            const char* sp = p; unsigned long long symReal = 0; std::string symErr;
            int st = trySymToken(&sp, &symReal, &symErr);
            if (st == -1) return symErr;
            if (st == 1) { real = symReal; haveReal = true; hp = sp; while (*hp == ' ') hp++; }
        }
        if (!haveReal) {
            if (sscanf_s(p, "%llx%n", &a, &adv) != 1) return "err:usage poke <hex|sym:NAME> <hexbytes>";
            hp = p + adv; while (*hp == ' ') hp++;
        }
        std::vector<unsigned char> bytes;
        while (hp[0] && hp[1]) { int hi = hexNib(hp[0]), lo = hexNib(hp[1]); if (hi < 0 || lo < 0) break; bytes.push_back((unsigned char)((hi << 4) | lo)); hp += 2; }
        if (bytes.empty()) return "err:no-bytes";
        if (!haveReal) { bool addrOk; real = resolveAddr(abs, a, &addrOk); if (!addrOk) return "err:no-flengine"; }
        bool ok = safeWrite((void*)real, bytes.data(), bytes.size());
        char b[64]; sprintf_s(b, sizeof(b), "{\"ok\":%d,\"wrote\":%llu}", ok ? 1 : 0, (unsigned long long)bytes.size());
        return b;
    }

    // In-FL chat tab: create our native browser tab + widgets + content-switch hook (on the main thread).
    if (req == "chattab_open") {
        if (!ensureSubclassed()) return "err:no-mainwindow";
        g_chatOpenOk = false;
        SendMessageW(g_mainWnd, WM_BRIDGE_CHATOPEN, 0, 0);
        char b[160]; sprintf_s(b, sizeof(b), "{\"ok\":%d,\"tabId\":%d,\"browser\":\"0x%p\",\"input\":\"0x%p\",\"display\":\"0x%p\"}",
            g_chatOpenOk ? 1 : 0, g_ourTabId, g_browser, g_inputCtrl, g_displayCtrl);
        return b;
    }
    if (req == "chattab_close") {
        if (g_mainWnd && g_origProc) SendMessageW(g_mainWnd, WM_BRIDGE_CHATCLOSE, 0, 0);
        return "{\"ok\":1}";
    }
    if (req == "chattab_status") {
        int scflag = readMainFormFlag();
        char b[320]; sprintf_s(b, sizeof(b), "{\"open\":%d,\"tabId\":%d,\"vtblSlot\":\"0x%p\",\"input\":\"0x%p\",\"display\":\"0x%p\",\"sendBtn\":\"0x%p\",\"mainForm\":\"0x%p\",\"scHooked\":%d,\"x4c5\":%d}",
            g_vtblSlot ? 1 : 0, g_ourTabId, (void*)g_vtblSlot, g_inputCtrl, g_displayCtrl, g_sendBtn, g_mainForm, g_scHooked ? 1 : 0, scflag);
        return b;
    }
    // Headless self-test: simulate a SPACE keydown to the main window; report the is-playing flag before/after.
    // With our tab active + the FormKeyDown hook installed, space must NOT toggle play (playingBefore==After).
    if (req == "chat_spacetest") {
        int p0 = readPlaying();
        int editBefore = (int)readEditW(g_inputCtrl).size();
        if (g_mainWnd) { SendMessageW(g_mainWnd, WM_KEYDOWN, 0x20, 0); SendMessageW(g_mainWnd, WM_KEYUP, 0x20, 0); }
        int p1 = readPlaying();
        if (p1 != p0 && g_mainWnd) { SendMessageW(g_mainWnd, WM_KEYDOWN, 0x20, 0); SendMessageW(g_mainWnd, WM_KEYUP, 0x20, 0); } // restore transport if it toggled
        if (g_mainWnd) SendMessageW(g_mainWnd, WM_CHAR, 0x20, 0);   // does the focused edit accept the space char?
        int editAfter = (int)readEditW(g_inputCtrl).size();
        char b[224]; sprintf_s(b, sizeof(b),
            "{\"playingBefore\":%d,\"playingAfter\":%d,\"editBefore\":%d,\"editAfter\":%d,\"kdHooked\":%d,\"scHooked\":%d}",
            p0, p1, editBefore, editAfter, g_kdHooked ? 1 : 0, g_scHooked ? 1 : 0);
        return b;
    }

    // chat comms. chat_say <utf8> appends a line to the display; chat_poll returns the user's submitted
    // message (utf8, empty if none) and clears it; chat_submit forces submit of whatever's in the input.
    if (starts(req, "chat_say ")) {
        if (!(g_mainWnd && g_origProc)) return "err:no-mainwindow";
        g_sayText = req.substr(9);
        SendMessageW(g_mainWnd, WM_BRIDGE_CHATSAY, 0, 0);
        return "{\"ok\":1}";
    }
    if (req == "chat_poll" || req == "chat_submit") {
        if (!(g_mainWnd && g_origProc)) return "";
        g_pollForce = (req == "chat_submit");
        g_pollResult.clear();
        SendMessageW(g_mainWnd, WM_BRIDGE_CHATPOLL, 0, 0);
        return g_pollResult;            // raw utf8 message, or "" if nothing pending
    }

    // ---- Plugins toolbar dropdown (re/16) ----
    // Add the far-right "Plugins" button to FL's menu bar (main thread). The in-FL host calls this at
    // startup; flprobe can call it for testing.
    if (req == "plugins_button_install") {
        if (!ensureSubclassed()) return "err:no-mainwindow";
        g_pluginsInstallOk = false;
        SendMessageW(g_mainWnd, WM_BRIDGE_PLUGINSINSTALL, 0, 0);
        char b[224]; sprintf_s(b, sizeof(b), "{\"ok\":%d,\"item\":\"0x%p\",\"hostFns\":%d}",
            g_pluginsInstallOk ? 1 : 0, g_pluginsItem, resolvePluginFns() ? 1 : 0);
        return b;
    }
    if (req == "plugins_button_remove") {
        if (g_mainWnd && g_origProc) SendMessageW(g_mainWnd, WM_BRIDGE_PLUGINSREMOVE, 0, 0);
        return "{\"ok\":1}";
    }
    // Diagnostics + direct list/toggle (bypasses the UI; drives the managed manager via the host).
    if (req == "plugins_status") {
        void* tf = NULL; void* bar = NULL;
        readToolbarPtrs(&tf, &bar);
        char b[256]; sprintf_s(b, sizeof(b), "{\"toolbarForm\":\"0x%p\",\"bar\":\"0x%p\",\"item\":\"0x%p\",\"hostFns\":%d}",
            tf, bar, g_pluginsItem, resolvePluginFns() ? 1 : 0);
        return b;
    }
    if (req == "plugins_list") {
        std::string js = callPluginList();
        return js.empty() ? "{\"host\":0}" : js;
    }
    if (starts(req, "plugins_toggle ")) {       // plugins_toggle <id> <0|1>
        std::string rest = req.substr(15);
        size_t sp = rest.find_last_of(' ');
        if (sp == std::string::npos) return "err:usage plugins_toggle <id> <0|1>";
        std::string id = rest.substr(0, sp);
        std::string en = rest.substr(sp + 1);
        int r = callPluginToggle(id, en == "1" || en == "true");
        char b[64]; sprintf_s(b, sizeof(b), "{\"ret\":%d}", r);
        return b;
    }

    // ---- Settings (task #61): debug-output window visibility (so flprobe can drive it too) ----
    if (starts(req, "debug_show ")) {           // debug_show <0|1>
        std::string a = req.substr(11);
        int v = (a == "1" || a == "true") ? 1 : 0;
        callDebugVisibleSet(v);
        char b[48]; sprintf_s(b, sizeof(b), "{\"debug\":%d}", v);
        return b;
    }
    if (req == "settings_get") {
        bool host = resolveSettingsFns();
        int dv = host ? callDebugVisibleGet() : -1;
        char b[64]; sprintf_s(b, sizeof(b), "{\"debug\":%d,\"hostFns\":%d}", dv, host ? 1 : 0);
        return b;
    }

    // ---- Generic plugin menu contributions (re/16) ----
    // (Re)materialize plugin-contributed entries into FL's native top-level menus (e.g. the FL Agent
    // View toggle). The in-FL host calls install at startup and refresh after enable/disable / on a
    // tracked-window visibility change; both rebuild the set + check-states. MAIN thread.
    if (req == "menu_contrib_install" || req == "menu_contrib_refresh") {
        if (!ensureSubclassed()) return "err:no-mainwindow";
        SendMessageW(g_mainWnd, WM_BRIDGE_MENUCONTRIBINSTALL, 0, 0);
        char b[96]; sprintf_s(b, sizeof(b), "{\"ok\":1,\"count\":%d,\"hostFns\":%d}",
            (int)g_menuItems.size(), resolveMenuFns() ? 1 : 0);
        return b;
    }
    if (req == "menu_contrib_remove") {
        if (g_mainWnd && g_origProc) SendMessageW(g_mainWnd, WM_BRIDGE_MENUCONTRIBREMOVE, 0, 0);
        return "{\"ok\":1}";
    }
    if (req == "menu_contrib_list") {       // diagnostics: the raw managed contribution JSON
        std::string js = callMenuList();
        return js.empty() ? "{\"host\":0}" : js;
    }

    // ---- Plugin toolbar TOGGLE buttons (task #87, re/24) ----
    // (Re)materialize plugin-declared square toggle buttons onto FL's main toolbar. add == refresh ==
    // rebuild-from-list; both post the install to the MAIN thread. MAIN thread does the FL widget work.
    if (req == "toolbar_button_add" || req == "toolbar_button_refresh") {
        if (!ensureSubclassed()) return "err:no-mainwindow";
        SendMessageW(g_mainWnd, WM_BRIDGE_TOOLBARINSTALL, 0, 0);
        char b[96]; sprintf_s(b, sizeof(b), "{\"ok\":1,\"count\":%d,\"hostFns\":%d}",
            (int)g_toolbarBtns.size(), resolveToolbarFns() ? 1 : 0);
        return b;
    }
    if (req == "toolbar_button_remove") {
        if (g_mainWnd && g_origProc) SendMessageW(g_mainWnd, WM_BRIDGE_TOOLBARREMOVE, 0, 0);
        return "{\"ok\":1}";
    }
    if (req == "toolbar_button_list") {     // diagnostics: the raw managed toolbar-button JSON
        std::string js = callToolbarList();
        return js.empty() ? "{\"host\":0}" : js;
    }

    if (starts(req, "automation_")) {
        if (!ensureSubclassed()) return "{\"ok\":0,\"reason\":\"main-window-unavailable\",\"mayHaveChanged\":false}";
        HWND target = g_mainWnd.load();
        if (!target || !IsWindow(target)) return "{\"ok\":0,\"reason\":\"main-window-unavailable\",\"mayHaveChanged\":false}";
        PendingWindowCommand pending{req};
        SendMessageW(target, WM_BRIDGE_AUTOMATION, 0, reinterpret_cast<LPARAM>(&pending));
        return pending.response;
    }
    if (starts(req, "mixer_add")) {
        if (!ensureSubclassed()) return "{\"ok\":0,\"reason\":\"main-window-unavailable\",\"mayHaveChanged\":false}";
        HWND target = g_mainWnd.load();
        if (!target || !IsWindow(target)) return "{\"ok\":0,\"reason\":\"main-window-unavailable\",\"mayHaveChanged\":false}";
        PendingWindowCommand pending{req};
        SendMessageW(target, WM_BRIDGE_MIXERADD, 0, reinterpret_cast<LPARAM>(&pending));
        return pending.response;
    }

    // Keyed asynchronous managed hosting and legacy slot zero share the same validated factory.
    if (starts(req, "winhost_")) {
        if (!ensureSubclassed()) return "{\"ok\":0,\"exists\":0,\"reason\":\"main-window-unavailable\"}";
        HWND target = g_mainWnd.load();
        if (!target || !IsWindow(target)) return "{\"ok\":0,\"exists\":0,\"reason\":\"main-window-unavailable\"}";
        PendingWindowCommand pending{req};
        SendMessageW(target, WM_BRIDGE_WINHOST_EMBED, 0, reinterpret_cast<LPARAM>(&pending));
        return pending.response;
    }

#ifdef FRUITYLINK_DEBUG_PIPE
    // ---- flprobe debug-pipe plugin contract (re/integration-pending-probe-debug.md) ----
    // DEBUG builds only. Maps "ok"/"err:<reason>" so flprobe's error sentinel works (any reply whose
    // first non-space chars are "err" is a failure). plugins_list above already emits the array with
    // the "loaded" flag, satisfying the list half of the contract.
    if (req == "plugins_dir") {                 // absolute dir the host watches (never starts with "err")
        std::string d = callPluginsDir();
        return d.empty() ? std::string("err:no-host") : d;
    }
    if (starts(req, "plugin_enable ") || starts(req, "plugin_disable ")) {
        bool en = starts(req, "plugin_enable ");
        std::string id = trimArg(req.substr(en ? 14 : 15));
        if (id.empty()) return std::string("err:usage ") + (en ? "plugin_enable" : "plugin_disable") + " <id>";
        int r = callPluginToggle(id, en);
        if (r == 1)  return "ok";
        if (r == -1) return "err:no-manager";
        if (r == -2) return "err:no-host";
        return "err:toggle-failed";
    }
    if (starts(req, "plugin_reload ")) {
        std::string id = trimArg(req.substr(14));
        if (id.empty()) return "err:usage plugin_reload <id>";
        int r = callPluginReload(id);
        if (r == 1)  return "ok";
        if (r == -1) return "err:no-manager";
        if (r == -2) return "err:no-host-or-unsupported";
        return "err:reload-failed";
    }
#endif // FRUITYLINK_DEBUG_PIPE

    return "err:unknown";
}

// ===================== in-process command surface (no pipe) =====================
// When FlBridge.dll is loaded IN-PROCESS via the version.dll proxy / CLR-host chain (instead of
// being injected + pipe-driven), managed code P/Invokes this export directly. Same string-in /
// string-out protocol as the pipe (handleCmd), so every existing typed control op works unchanged
// — the named pipe is simply bypassed. handleCmd already marshals native FL calls onto the MAIN
// thread (WM_BRIDGE_CALL) and SEH-guards them, so this path keeps the same correctness discipline.
//
//   int FlBridge_Command(const char* reqUtf8NullTerm, char* outBuf, int outLen);
//     returns the FULL response length in bytes (may exceed outLen; DO NOT retry a mutating command);
//     writes up to outLen bytes into outBuf (not null-terminated; use the returned length).
//     returns -1 on a null request.
// New hosts use FlBridge_CommandAlloc below to obtain the complete response without replay.
extern "C" __declspec(dllexport) int FlBridge_Command(const char* req, char* outBuf, int outLen)
{
    if (!req) return -1;
    std::string resp = handleCmd(std::string(req));   // handleCmd is internally SEH-guarded
    int n = (int)resp.size();
    if (outBuf && outLen > 0) {
        int copy = (n < outLen) ? n : outLen;
        memcpy(outBuf, resp.data(), (size_t)copy);
    }
    return n;
}

// Owned-response transport: execute each command once even when its reply exceeds a caller's buffer.
// Empty replies have length 0 and a null pointer. Release every non-null response with the matching
// bridge export so allocation and deallocation use the same CRT.
extern "C" __declspec(dllexport) int FlBridge_CommandAlloc(const char* req, char** response)
{
    if (!response) return -1;
    *response = nullptr;
    if (!req) return -1;
    try {
        std::string result = handleCmd(std::string(req));
        if (result.size() > INT_MAX) return -1;
        if (result.empty()) return 0;
        char* buffer = static_cast<char*>(std::malloc(result.size()));
        if (!buffer) return -1;
        memcpy(buffer, result.data(), result.size());
        *response = buffer;
        return static_cast<int>(result.size());
    } catch (...) { return -1; }
}

extern "C" __declspec(dllexport) void FlBridge_FreeResponse(char* response)
{
    std::free(response);
}

#ifdef FRUITYLINK_DEBUG_PIPE
// External named-pipe server — DEBUG builds only. Production/Release installs do not compile this in,
// so no \\.\pipe\FruityLinkBridge is ever created; the in-process FlBridge_Command surface still works.
static DWORD WINAPI Worker(LPVOID)
{
    logline("worker start");
    HANDLE evt = CreateEventA(NULL, TRUE, FALSE, NULL);
    while (true) {
        HANDLE pipe = CreateNamedPipeA("\\\\.\\pipe\\FruityLinkBridge",
            PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
            PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT, 1, 65536, 65536, 0, NULL);
        if (pipe == INVALID_HANDLE_VALUE) { if (WaitForSingleObject(g_stop, 50) == WAIT_OBJECT_0) break; continue; }

        OVERLAPPED ov{}; ResetEvent(evt); ov.hEvent = evt;
        BOOL conn = ConnectNamedPipe(pipe, &ov); DWORD err = GetLastError(); bool connected = false;
        if (conn) connected = true;
        else if (err == ERROR_PIPE_CONNECTED) connected = true;
        else if (err == ERROR_IO_PENDING) {
            HANDLE hs[2] = { g_stop, evt }; DWORD w = WaitForMultipleObjects(2, hs, FALSE, INFINITE);
            if (w == WAIT_OBJECT_0) { CancelIo(pipe); CloseHandle(pipe); break; }
            DWORD bytes = 0; connected = GetOverlappedResult(pipe, &ov, &bytes, FALSE);
        }
        if (!connected) { CloseHandle(pipe); if (WaitForSingleObject(g_stop, 0) == WAIT_OBJECT_0) break; continue; }

        char buf[65536]; DWORD nread = 0;
        if (ReadFile(pipe, buf, sizeof(buf) - 1, &nread, NULL) && nread > 0) {
            buf[nread] = 0; std::string req(buf, nread);
            while (!req.empty() && (req.back() == '\n' || req.back() == '\r' || req.back() == ' ' || req.back() == 0)) req.pop_back();
            if (req == "shutdown") {
                const char* r = "bye"; DWORD wn = 0; WriteFile(pipe, r, 3, &wn, NULL);
                FlushFileBuffers(pipe); DisconnectNamedPipe(pipe); CloseHandle(pipe); break;
            }
            std::string resp = handleCmd(req);
            DWORD wn = 0; WriteFile(pipe, resp.c_str(), (DWORD)resp.size(), &wn, NULL); FlushFileBuffers(pipe);
        }
        DisconnectNamedPipe(pipe); CloseHandle(pipe);
        if (WaitForSingleObject(g_stop, 0) == WAIT_OBJECT_0) break;
    }
    if (evt) CloseHandle(evt);
    logline("worker exit");
    return 0;
}
#endif // FRUITYLINK_DEBUG_PIPE

// Restore the content-switch vtbl slot directly (memory write, any thread) — last-ditch so a dangling
// thunk can never survive into the unmapped DLL even if the main-thread close path can't run.
static void forceRestoreHook()
{
    if (g_vtblSlot && g_origContentSwitch) {
        DWORD oldP;
        if (VirtualProtect(g_vtblSlot, 8, PAGE_READWRITE, &oldP)) {
            *g_vtblSlot = g_origContentSwitch;
            DWORD t; VirtualProtect(g_vtblSlot, 8, oldP, &t);
        }
        g_vtblSlot = NULL; g_origContentSwitch = NULL;
    }
    // Also un-hook the Send button's onClick (memory write, any thread) so a click can't enter the unmapped DLL.
    if (g_sendBtn) { __try { *(void**)((char*)g_sendBtn + 0x1e4) = g_sendBtnOrigClick; } __except (EXCEPTION_EXECUTE_HANDLER) {} g_sendBtn = NULL; }
    // Clear the Plugins submenu's onClick thunks + hide the item (memory-only; main-thread rebuild is
    // done in DoPluginsRemove before this) so no menu interaction can enter the unmapping DLL.
    if (g_pluginsItem) clearChildrenThunksMem(g_pluginsItem);
    // Same for the generic menu contributions (View etc.): neutralize their onClick thunks (the
    // main-thread free is done by DoMenuContribRemove before this) so no menu click can re-enter us.
    clearMenuContribThunksMem();
    // Same for our toolbar toggle buttons: neutralize their onChange/paint TMethods (the main-thread
    // teardown is done by DoToolbarRemove before this) so no toolbar click can re-enter the unmapping DLL.
    clearToolbarThunksMem();
    // Native-window callbacks remain in the pinned bridge. Never detach another UI thread's child
    // or destroy a native form from DLL_PROCESS_DETACH / loader lock.
    // Restore the suppress flag + un-hook FormShortCut (its jmp must not point into the unmapping DLL).
    setShortcutSuppress(0);
    removeShortcutHook();
    removeKeyDownHook();   // FormKeyDown jmp must not point into the unmapping DLL
}

extern "C" __declspec(dllexport) void BridgeStop()
{
    g_bridgeStopping.store(true);
    // Tear the chat tab down on the MAIN thread (restore vtbl + hide widgets) BEFORE unload, while the
    // subclass is still installed; then a direct restore as a backstop.
    if (g_vtblSlot && g_mainWnd && g_origProc) SendMessageW(g_mainWnd, WM_BRIDGE_CHATCLOSE, 0, 0);
    // Remove the Plugins menu entry on the MAIN thread (clear children + hide + rebuild bar) before unload.
    if (g_pluginsItem && g_mainWnd && g_origProc) SendMessageW(g_mainWnd, WM_BRIDGE_PLUGINSREMOVE, 0, 0);
    // Remove generic menu contributions (View etc.) on the MAIN thread (clear thunks + free items) too.
    if (!g_menuItems.empty() && g_mainWnd && g_origProc) SendMessageW(g_mainWnd, WM_BRIDGE_MENUCONTRIBREMOVE, 0, 0);
    // Remove our toolbar toggle buttons on the MAIN thread (clear TMethods + hide + unparent) before unload.
    if (!g_toolbarBtns.empty() && g_mainWnd && g_origProc) SendMessageW(g_mainWnd, WM_BRIDGE_TOOLBARREMOVE, 0, 0);
    // Hide hosts and release only detached forms. Bound children stay with their own UI thread;
    // their native subclass remains safe because creating a native host pins this bridge.
    if (g_mainWnd && g_origProc) SendMessageW(g_mainWnd, WM_BRIDGE_WINHOST_CLOSE, 0, 0);
    forceRestoreHook();
    if (g_stop) SetEvent(g_stop);
    if (g_thread) { WaitForSingleObject(g_thread, 5000); CloseHandle(g_thread); g_thread = NULL; }
    revertSubclass(); // remove our window proc before unload
    logline("BridgeStop done");
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved)
{
    switch (reason) {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(hModule);
#ifdef FRUITYLINK_DEBUG_PIPE
        // DEBUG only: stand up the external named-pipe server (for flprobe). Release omits it entirely.
        g_stop = CreateEventA(NULL, TRUE, FALSE, NULL);
        g_thread = CreateThread(NULL, 0, Worker, NULL, 0, NULL);
        logline("attach (debug pipe enabled)");
#else
        logline("attach (release; no debug pipe)");
#endif
        break;
    case DLL_PROCESS_DETACH:
        // Process termination already stopped other threads. Never call UI APIs, acquire their locks,
        // or wait for worker/UI completion from the loader lock; Windows reclaims these resources.
        if (reserved) break;
        forceRestoreHook(); // never leave a vtbl slot pointing into the unmapping DLL (loader-lock safe: no SendMessage)
        // Never wait for a worker under loader lock; explicit BridgeStop performs the join.
        if (g_stop) SetEvent(g_stop);
        if (g_thread) { CloseHandle(g_thread); g_thread = NULL; }
        revertSubclass(false);
        if (g_stop) { CloseHandle(g_stop); g_stop = NULL; }
        logline("detach");
        break;
    }
    return TRUE;
}
