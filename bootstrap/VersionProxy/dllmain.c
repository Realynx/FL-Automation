// FruityLink transparent version.dll proxy.
//
// Replaces runtime DLL-injection of FlBridge.dll with a *filesystem* install: dropped into
// FL Studio's program directory as "version.dll", FLEngine_x64.dll (which statically imports
// version.dll, app-dir-first because version is not a KnownDLL — see re/15-proxy-install.md)
// loads THIS dll at startup, every session, transparently.
//
// Responsibilities:
//   1. Forward all 17 genuine version.dll exports to C:\Windows\System32\version.dll so FL
//      behaves exactly as before (transparent — milestone 1).
//   2. On a worker thread (off the loader lock), LoadLibrary our CoreCLR host
//      "<proxyDir>\FruityLink\FlClrHost.dll", which boots the .NET runtime in-process and runs
//      our managed entry (milestones 2-4).
//
// Safety: the CLR bootstrap is GATED on the FruityLink install subdir existing next to this dll
// (a bare version.dll is pure passthrough) and can be force-disabled with FRUITYLINK_DISABLE=1.
// All bootstrap work is SEH-guarded so a failure never takes FL Studio down. DllMain stays
// minimal (loader-lock rule): table populate + CreateThread only; no CLR work under the lock.

#include <windows.h>
#include "versionStubExports.h"

extern void* g_FunctionTable[]; // defined in versionASMStubs.asm

// ---- logging (proof + diagnostics) -------------------------------------------------------
// Writes to %TEMP%\fruitylink-proxy.log so we can confirm the proxy loaded inside FL even when
// there is no console/UI.
static void proxyLog(const char* msg)
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
    WriteFile(h, msg, lstrlenA(msg), &wrote, NULL);
    WriteFile(h, "\r\n", 2, &wrote, NULL);
    CloseHandle(h);
}

// Populate the forward table with the genuine version.dll proc addresses. Loaded by ABSOLUTE
// System32 path so we never re-find our own proxy (no infinite loop). version.dll only depends
// on already-mapped low-level DLLs, so this is safe to do under the loader lock.
static void PopulateFunctionTable(void)
{
    HMODULE m = LoadLibraryA("C:\\Windows\\System32\\version.dll");
    if (!m) { proxyLog("FATAL: could not load System32\\version.dll for forwarding"); return; }
    g_FunctionTable[0]  = (void*)GetProcAddress(m, "GetFileVersionInfoA");
    g_FunctionTable[1]  = (void*)GetProcAddress(m, "GetFileVersionInfoByHandle");
    g_FunctionTable[2]  = (void*)GetProcAddress(m, "GetFileVersionInfoExA");
    g_FunctionTable[3]  = (void*)GetProcAddress(m, "GetFileVersionInfoExW");
    g_FunctionTable[4]  = (void*)GetProcAddress(m, "GetFileVersionInfoSizeA");
    g_FunctionTable[5]  = (void*)GetProcAddress(m, "GetFileVersionInfoSizeExA");
    g_FunctionTable[6]  = (void*)GetProcAddress(m, "GetFileVersionInfoSizeExW");
    g_FunctionTable[7]  = (void*)GetProcAddress(m, "GetFileVersionInfoSizeW");
    g_FunctionTable[8]  = (void*)GetProcAddress(m, "GetFileVersionInfoW");
    g_FunctionTable[9]  = (void*)GetProcAddress(m, "VerFindFileA");
    g_FunctionTable[10] = (void*)GetProcAddress(m, "VerFindFileW");
    g_FunctionTable[11] = (void*)GetProcAddress(m, "VerInstallFileA");
    g_FunctionTable[12] = (void*)GetProcAddress(m, "VerInstallFileW");
    g_FunctionTable[13] = (void*)GetProcAddress(m, "VerLanguageNameA");
    g_FunctionTable[14] = (void*)GetProcAddress(m, "VerLanguageNameW");
    g_FunctionTable[15] = (void*)GetProcAddress(m, "VerQueryValueA");
    g_FunctionTable[16] = (void*)GetProcAddress(m, "VerQueryValueW");
}

static HINSTANCE g_self = NULL;

// Resolve "<dir of this proxy dll>\FruityLink\FlClrHost.dll" into out (MAX_PATH). Returns 0 on failure.
static int BuildHostPath(char* out)
{
    char self[MAX_PATH];
    DWORD n = GetModuleFileNameA((HMODULE)g_self, self, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return 0;
    // strip filename → directory
    for (int i = (int)n - 1; i >= 0; i--) { if (self[i] == '\\' || self[i] == '/') { self[i] = 0; break; } }
    wsprintfA(out, "%s\\FruityLink\\FlClrHost.dll", self);
    return 1;
}

// Worker (off the loader lock): load the CoreCLR host, which boots .NET and runs our managed entry.
static DWORD WINAPI BootstrapWorker(LPVOID param)
{
    (void)param;
    if (GetEnvironmentVariableA("FRUITYLINK_DISABLE", NULL, 0) > 0)
    {
        proxyLog("FRUITYLINK_DISABLE set — passthrough only, not booting CLR host");
        return 0;
    }

    char hostPath[MAX_PATH];
    if (!BuildHostPath(hostPath)) { proxyLog("could not resolve host path"); return 0; }

    // Gate: only boot if the install is present. A bare version.dll stays pure passthrough.
    if (GetFileAttributesA(hostPath) == INVALID_FILE_ATTRIBUTES)
    {
        proxyLog("FlClrHost.dll not installed next to proxy — passthrough only. Path was:");
        proxyLog(hostPath);
        return 0;
    }

    proxyLog("loading CLR host:");
    proxyLog(hostPath);
    __try
    {
        // Add the FruityLink subdir to the DLL search path so the host's own deps resolve.
        char dir[MAX_PATH];
        lstrcpynA(dir, hostPath, MAX_PATH);
        for (int i = lstrlenA(dir) - 1; i >= 0; i--) { if (dir[i] == '\\') { dir[i] = 0; break; } }
        SetDllDirectoryA(dir);

        HMODULE h = LoadLibraryA(hostPath);
        if (!h) { char b[64]; wsprintfA(b, "LoadLibrary(FlClrHost) failed, err=%lu", GetLastError()); proxyLog(b); }
        else      proxyLog("FlClrHost.dll loaded — CLR bootstrap handed off");
        SetDllDirectoryA(NULL);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        proxyLog("EXCEPTION while loading CLR host (swallowed; FL unaffected)");
    }
    return 0;
}

BOOL APIENTRY DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        g_self = inst;
        DisableThreadLibraryCalls(inst);
        PopulateFunctionTable();      // forwarders ready before FL makes any version.dll call
        proxyLog("version.dll proxy attached (forwarders populated)");
        // Heavy lifting (LoadLibrary CLR host) on a worker thread — never under the loader lock.
        CreateThread(NULL, 0, BootstrapWorker, NULL, 0, NULL);
        break;
    case DLL_PROCESS_DETACH:
        proxyLog("version.dll proxy detached");
        break;
    }
    return TRUE;
}
