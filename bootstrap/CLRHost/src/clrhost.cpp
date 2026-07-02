// FlClrHost.dll — FruityLink CoreCLR host (in-process .NET bootstrap).
//
// Loaded by the version.dll proxy on a worker thread. Hosts CoreCLR via nethost/hostfxr
// (libnethost linked STATICALLY so no extra nethost.dll has to ship) and invokes our managed
// entry point inside FL Studio's process:
//
//     FruityLink.Host.dll  ->  FruityLink.Host.HostEntry.Bootstrap (UnmanagedCallersOnly)
//
// Both the managed assembly and its *.runtimeconfig.json must sit next to THIS dll
// (…\FL Studio 2025\FruityLink\). Mirrors RealLoader/Bootstrapping/CLRHost.
//
// All work runs on a worker thread spawned from DllMain (off the loader lock). Everything is
// guarded so a failure logs and returns rather than taking FL down.

#include <windows.h>
#include <string>

#include <nethost.h>
#include <coreclr_delegates.h>
#include <hostfxr.h>

static const wchar_t* kManagedAssembly = L"FruityLink.Host.dll";
static const wchar_t* kRuntimeConfig   = L"FruityLink.Host.runtimeconfig.json";
static const char_t*  kEntryType       = L"FruityLink.Host.HostEntry, FruityLink.Host";
static const char_t*  kEntryMethod     = L"Bootstrap";

// Managed plugin glue resolved alongside Bootstrap and handed to the C++ bridge (FlBridge.dll) via
// the FlClr_GetPluginFns export below — so the native "Plugins" toolbar dropdown can list/toggle
// plugins without re-initializing the runtime. See re/16-toolbar-plugins-menu.md.
static const char_t*  kPluginGlueType  = L"FruityLink.Host.PluginGlue, FruityLink.Host";
static void* g_pluginListFn   = nullptr;   // int ListJson(char* buf, int len)
static void* g_pluginToggleFn = nullptr;   // int Toggle(char* idUtf8, int idLen, int enable)
// Debug-pipe plugin glue (flprobe plugin loop): resolved alongside the above and handed to the bridge
// via the FlClr_GetPluginExtraFns sibling export. See re/integration-pending-probe-debug.md.
static void* g_pluginReloadFn = nullptr;   // int Reload(char* idUtf8, int idLen)
static void* g_pluginDirFn    = nullptr;   // int PluginsDir(char* buf, int len)
// Settings glue (task #61): debug-output window visibility, handed to the bridge via the
// FlClr_GetSettingsFns sibling export so "FL Plugins ▸ Settings ▸ Show Debug Output" can toggle it.
static void* g_setDebugFn     = nullptr;   // int SetDebugVisible(int)
static void* g_getDebugFn     = nullptr;   // int GetDebugVisible()
// Menu-contribution glue: lets ANY plugin add entries to FL's native top-level menus (e.g. the FL
// Agent View toggle). Resolved alongside the above and handed to the bridge via FlClr_GetMenuFns.
static const char_t*  kMenuGlueType   = L"FruityLink.Host.MenuGlue, FruityLink.Host";
static void* g_menuListFn     = nullptr;   // int ContributionsJson(char* buf, int len)
static void* g_menuInvokeFn   = nullptr;   // int Invoke(char* idUtf8, int idLen)
static void* g_menuCheckedFn  = nullptr;   // int Checked(char* idUtf8, int idLen)
// Toolbar-button glue: lets ANY plugin materialize a big square TOGGLE button on FL's main toolbar
// (clone of the menu-contribution glue above). Resolved alongside the others and handed to the bridge
// via FlClr_GetToolbarFns. See re/24-toolbar-buttons.md.
static const char_t*  kToolbarGlueType = L"FruityLink.Host.ToolbarGlue, FruityLink.Host";
static void* g_toolbarListFn   = nullptr;  // int ContributionsJson(char* buf, int len)
static void* g_toolbarInvokeFn = nullptr;  // int Invoke(char* idUtf8, int idLen)
static void* g_toolbarActiveFn = nullptr;  // int Active(char* idUtf8, int idLen)

// Exposed to FlBridge.dll (GetProcAddress). Returns the two managed function pointers (either may be
// null if not yet resolved → the bridge degrades to "Plugin host not ready").
extern "C" __declspec(dllexport) void FlClr_GetPluginFns(void** outList, void** outToggle)
{
    if (outList)   *outList   = g_pluginListFn;
    if (outToggle) *outToggle = g_pluginToggleFn;
}

// Sibling export for the debug-pipe plugin commands (plugin_reload / plugins_dir). Kept separate from
// FlClr_GetPluginFns so the existing list/toggle ABI is unchanged. Either pointer may be null.
extern "C" __declspec(dllexport) void FlClr_GetPluginExtraFns(void** outReload, void** outDir)
{
    if (outReload) *outReload = g_pluginReloadFn;
    if (outDir)    *outDir    = g_pluginDirFn;
}

// Sibling export for the Settings submenu (debug-output visibility). Kept separate from the plugin
// exports so their ABI is unchanged. Either pointer may be null (the bridge degrades gracefully).
extern "C" __declspec(dllexport) void FlClr_GetSettingsFns(void** outSet, void** outGet)
{
    if (outSet) *outSet = g_setDebugFn;
    if (outGet) *outGet = g_getDebugFn;
}

// Sibling export for the generic plugin menu-contribution glue (list/invoke/checked). Kept separate
// so the other export ABIs are unchanged. Any pointer may be null (the bridge degrades to no entries).
extern "C" __declspec(dllexport) void FlClr_GetMenuFns(void** outList, void** outInvoke, void** outChecked)
{
    if (outList)    *outList    = g_menuListFn;
    if (outInvoke)  *outInvoke  = g_menuInvokeFn;
    if (outChecked) *outChecked = g_menuCheckedFn;
}

// Sibling export for the toolbar-button glue (list/invoke/active). Kept separate so the other export
// ABIs are unchanged. Any pointer may be null (the bridge degrades to no toolbar buttons).
extern "C" __declspec(dllexport) void FlClr_GetToolbarFns(void** outList, void** outInvoke, void** outActive)
{
    if (outList)   *outList   = g_toolbarListFn;
    if (outInvoke) *outInvoke = g_toolbarInvokeFn;
    if (outActive) *outActive = g_toolbarActiveFn;
}

static void hostLog(const char* msg)
{
    char path[MAX_PATH];
    DWORD n = GetTempPathA(MAX_PATH, path);
    if (n == 0 || n > MAX_PATH - 32) return;
    lstrcatA(path, "fruitylink-proxy.log");
    HANDLE h = CreateFileA(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
                           OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    SetFilePointer(h, 0, NULL, FILE_END);
    DWORD wrote = 0;
    WriteFile(h, "[clrhost] ", 10, &wrote, NULL);
    WriteFile(h, msg, lstrlenA(msg), &wrote, NULL);
    WriteFile(h, "\r\n", 2, &wrote, NULL);
    CloseHandle(h);
}

static HMODULE g_self = NULL;

// Directory containing THIS dll (…\FruityLink\), with a trailing backslash.
static std::wstring SelfDir()
{
    wchar_t buf[MAX_PATH];
    DWORD n = GetModuleFileNameW(g_self, buf, MAX_PATH);
    std::wstring p(buf, n);
    size_t slash = p.find_last_of(L"\\/");
    return (slash == std::wstring::npos) ? std::wstring() : p.substr(0, slash + 1);
}

// hostfxr entry points we use.
static hostfxr_initialize_for_runtime_config_fn fxr_init = nullptr;
static hostfxr_set_runtime_property_value_fn    fxr_set_prop = nullptr;
static hostfxr_get_runtime_delegate_fn          fxr_get_delegate = nullptr;
static hostfxr_close_fn                         fxr_close = nullptr;

static bool LoadHostfxr()
{
    wchar_t buf[MAX_PATH];
    size_t len = MAX_PATH;
    int rc = get_hostfxr_path(buf, &len, nullptr);
    if (rc != 0)
    {
        char b[96]; wsprintfA(b, "get_hostfxr_path failed rc=0x%x (is the .NET runtime installed?)", rc);
        hostLog(b);
        return false;
    }
    HMODULE lib = LoadLibraryW(buf);
    if (!lib) { hostLog("LoadLibrary(hostfxr) failed"); return false; }

    fxr_init        = (hostfxr_initialize_for_runtime_config_fn)GetProcAddress(lib, "hostfxr_initialize_for_runtime_config");
    fxr_set_prop    = (hostfxr_set_runtime_property_value_fn)   GetProcAddress(lib, "hostfxr_set_runtime_property_value");
    fxr_get_delegate= (hostfxr_get_runtime_delegate_fn)        GetProcAddress(lib, "hostfxr_get_runtime_delegate");
    fxr_close       = (hostfxr_close_fn)                       GetProcAddress(lib, "hostfxr_close");
    if (!fxr_init || !fxr_get_delegate || !fxr_close) { hostLog("missing hostfxr exports"); return false; }
    return true;
}

// dir = directory of the runtimeconfig (…\FruityLink\), with trailing backslash.
static load_assembly_and_get_function_pointer_fn GetLoadAssembly(const std::wstring& configPath, const std::wstring& dir)
{
    hostfxr_handle ctx = nullptr;
    int rc = fxr_init(configPath.c_str(), nullptr, &ctx);
    // 0 = Success, 1 = Success_HostAlreadyInitialized, 2 = Success_DifferentRuntimeProperties.
    bool ok = (rc == 0 || rc == 1 || rc == 2);
    if (!ok || ctx == nullptr)
    {
        char b[96]; wsprintfA(b, "hostfxr_initialize_for_runtime_config failed rc=0x%x", rc);
        hostLog(b);
        if (ctx) fxr_close(ctx);
        return nullptr;
    }
    // Anchor AppContext.BaseDirectory to our install dir so managed relative-path logic and the
    // [DllImport("FlBridge.dll")] default search both resolve from …\FruityLink\. Only settable on a
    // fresh runtime (rc==0); ignored if the runtime was already initialized by an earlier host.
    if (rc == 0 && fxr_set_prop) fxr_set_prop(ctx, L"APP_CONTEXT_BASE_DIRECTORY", dir.c_str());
    void* fn = nullptr;
    rc = fxr_get_delegate(ctx, hdt_load_assembly_and_get_function_pointer, &fn);
    fxr_close(ctx);
    if (rc != 0 || fn == nullptr)
    {
        char b[96]; wsprintfA(b, "get_runtime_delegate failed rc=0x%x", rc);
        hostLog(b);
        return nullptr;
    }
    return (load_assembly_and_get_function_pointer_fn)fn;
}

typedef int (CORECLR_DELEGATE_CALLTYPE *bootstrap_fn)(void*, int);

// SEH-guarded invoke kept in its own function — it holds no C++ objects requiring unwinding,
// so __try is legal here (MSVC C2712 otherwise).
static int CallEntryGuarded(bootstrap_fn entry)
{
    __try { return entry(nullptr, 0); }
    __except (EXCEPTION_EXECUTE_HANDLER) { hostLog("EXCEPTION in managed Bootstrap (swallowed)"); return -1; }
}

static DWORD WINAPI ClrWorker(LPVOID)
{
    std::wstring dir = SelfDir();
    std::wstring asmPath    = dir + kManagedAssembly;
    std::wstring configPath = dir + kRuntimeConfig;

    if (GetFileAttributesW(asmPath.c_str()) == INVALID_FILE_ATTRIBUTES) { hostLog("FruityLink.Host.dll missing"); return 0; }
    if (GetFileAttributesW(configPath.c_str()) == INVALID_FILE_ATTRIBUTES) { hostLog("FruityLink.Host.runtimeconfig.json missing"); return 0; }

    if (!LoadHostfxr()) return 0;
    hostLog("hostfxr loaded");

    load_assembly_and_get_function_pointer_fn loadAndGet = GetLoadAssembly(configPath, dir);
    if (!loadAndGet) return 0;
    hostLog("runtime initialized; loading managed entry");

    bootstrap_fn entry = nullptr;
    int rc = loadAndGet(asmPath.c_str(), kEntryType, kEntryMethod,
                        UNMANAGEDCALLERSONLY_METHOD, nullptr, (void**)&entry);
    if (rc != 0 || entry == nullptr)
    {
        char b[128]; wsprintfA(b, "load_assembly_and_get_function_pointer failed rc=0x%x", rc);
        hostLog(b);
        return 0;
    }

    // Resolve the plugin glue function pointers (best-effort; the dropdown degrades if absent).
    int prc = loadAndGet(asmPath.c_str(), kPluginGlueType, L"ListJson",
                         UNMANAGEDCALLERSONLY_METHOD, nullptr, &g_pluginListFn);
    int trc = loadAndGet(asmPath.c_str(), kPluginGlueType, L"Toggle",
                         UNMANAGEDCALLERSONLY_METHOD, nullptr, &g_pluginToggleFn);
    if (prc != 0 || trc != 0 || !g_pluginListFn || !g_pluginToggleFn)
    {
        char b[128]; wsprintfA(b, "plugin glue resolve: list rc=0x%x toggle rc=0x%x (dropdown will degrade)", prc, trc);
        hostLog(b);
    }
    else hostLog("plugin glue resolved (FlClr_GetPluginFns ready)");

    // Debug-pipe plugin glue (Reload / PluginsDir) — best-effort; only used by the debug-only pipe.
    int rrc = loadAndGet(asmPath.c_str(), kPluginGlueType, L"Reload",
                         UNMANAGEDCALLERSONLY_METHOD, nullptr, &g_pluginReloadFn);
    int drc = loadAndGet(asmPath.c_str(), kPluginGlueType, L"PluginsDir",
                         UNMANAGEDCALLERSONLY_METHOD, nullptr, &g_pluginDirFn);
    if (rrc != 0 || drc != 0 || !g_pluginReloadFn || !g_pluginDirFn)
    {
        char b[128]; wsprintfA(b, "plugin extra glue resolve: reload rc=0x%x dir rc=0x%x (debug pipe will degrade)", rrc, drc);
        hostLog(b);
    }
    else hostLog("plugin extra glue resolved (FlClr_GetPluginExtraFns ready)");

    // Settings glue (debug-output visibility) — best-effort; the Settings submenu degrades if absent.
    int src = loadAndGet(asmPath.c_str(), kPluginGlueType, L"SetDebugVisible",
                         UNMANAGEDCALLERSONLY_METHOD, nullptr, &g_setDebugFn);
    int gdc = loadAndGet(asmPath.c_str(), kPluginGlueType, L"GetDebugVisible",
                         UNMANAGEDCALLERSONLY_METHOD, nullptr, &g_getDebugFn);
    if (src != 0 || gdc != 0 || !g_setDebugFn || !g_getDebugFn)
    {
        char b[128]; wsprintfA(b, "settings glue resolve: set rc=0x%x get rc=0x%x (Settings menu will degrade)", src, gdc);
        hostLog(b);
    }
    else hostLog("settings glue resolved (FlClr_GetSettingsFns ready)");

    // Menu-contribution glue (generic plugin menu entries) — best-effort; the menus degrade to no
    // plugin entries if absent.
    int mlc = loadAndGet(asmPath.c_str(), kMenuGlueType, L"ContributionsJson",
                         UNMANAGEDCALLERSONLY_METHOD, nullptr, &g_menuListFn);
    int mic = loadAndGet(asmPath.c_str(), kMenuGlueType, L"Invoke",
                         UNMANAGEDCALLERSONLY_METHOD, nullptr, &g_menuInvokeFn);
    int mcc = loadAndGet(asmPath.c_str(), kMenuGlueType, L"Checked",
                         UNMANAGEDCALLERSONLY_METHOD, nullptr, &g_menuCheckedFn);
    if (mlc != 0 || mic != 0 || mcc != 0 || !g_menuListFn || !g_menuInvokeFn || !g_menuCheckedFn)
    {
        char b[160]; wsprintfA(b, "menu glue resolve: list rc=0x%x invoke rc=0x%x checked rc=0x%x (menu contributions will degrade)", mlc, mic, mcc);
        hostLog(b);
    }
    else hostLog("menu glue resolved (FlClr_GetMenuFns ready)");

    // Toolbar-button glue (plugin square toggle buttons) — best-effort; the toolbar degrades to no
    // plugin buttons if absent. Clone of the menu-contribution glue block above.
    int tlc = loadAndGet(asmPath.c_str(), kToolbarGlueType, L"ContributionsJson",
                         UNMANAGEDCALLERSONLY_METHOD, nullptr, &g_toolbarListFn);
    int tic = loadAndGet(asmPath.c_str(), kToolbarGlueType, L"Invoke",
                         UNMANAGEDCALLERSONLY_METHOD, nullptr, &g_toolbarInvokeFn);
    int tac = loadAndGet(asmPath.c_str(), kToolbarGlueType, L"Active",
                         UNMANAGEDCALLERSONLY_METHOD, nullptr, &g_toolbarActiveFn);
    if (tlc != 0 || tic != 0 || tac != 0 || !g_toolbarListFn || !g_toolbarInvokeFn || !g_toolbarActiveFn)
    {
        char b[160]; wsprintfA(b, "toolbar glue resolve: list rc=0x%x invoke rc=0x%x active rc=0x%x (toolbar buttons will degrade)", tlc, tic, tac);
        hostLog(b);
    }
    else hostLog("toolbar glue resolved (FlClr_GetToolbarFns ready)");

    hostLog("invoking FruityLink.Host.HostEntry.Bootstrap");
    int r = CallEntryGuarded(entry);
    char b[64]; wsprintfA(b, "managed Bootstrap returned %d", r);
    hostLog(b);
    return 0;
}

BOOL APIENTRY DllMain(HMODULE inst, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_self = inst;
        DisableThreadLibraryCalls(inst);
        // Spawn off the loader lock: hosting the CLR from DllMain would deadlock.
        CreateThread(NULL, 0, ClrWorker, NULL, 0, NULL);
    }
    return TRUE;
}
