using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace FruityLink.Probe;

/// <summary>
/// FL Studio runtime harness — talks to <c>FlBridge.dll</c> over the named pipe
/// <c>\\.\pipe\FruityLinkBridge</c>, and (legacy/dev path) can hot inject/eject the bridge.
///
/// FruityLink now ships as a transparent <b>proxy-DLL filesystem install</b> (re/integration-pending-proxy.md):
/// the <c>version.dll</c> proxy boots the CoreCLR host in-process and loads <c>FlBridge.dll</c>, which serves
/// the SAME pipe. So you do NOT inject in the install model — just <c>attach</c> to the already-loaded bridge.
/// The pipe protocol (ping/info/peek/poke/call/callabs/key/shutdown/status/…) is unchanged either way.
///
///   flprobe attach               connect to the proxy-loaded bridge over the pipe (no injection) → ping + info
///   flprobe bridge [cmd...]      pipe client: no args = status; else send e.g. 'ping' | 'info' | 'peek e02d30 16'
///   flprobe inject [dllPath]     (legacy/dev) copy FlBridge.dll to a unique temp name and LoadLibrary it into FL64
///   flprobe eject                (legacy/dev) stop the bridge worker (pipe 'shutdown') then FreeLibrary — clean unload
///   flprobe reload [dllPath]     (legacy/dev) eject + inject (the iterate loop; temp-copy dodges file locks)
///
/// In the install model the bridge is owned by the proxy chain — do NOT inject a second copy over it.
/// </summary>
internal static class Inject
{
    private const string FlProcess = "FL64";
    internal const string PipeName = "FruityLinkBridge";
    private const string ModulePrefix = "FlBridge";

    // ---- public commands -----------------------------------------------------

    public static int Run(List<string> pos)
    {
        Process? fl = FindFl();
        if (fl is null) return Fail("FL Studio (FL64.exe) is not running. Launch it first, then inject.");

        string? src = pos.Count > 1 ? pos[1] : DefaultDllPath();
        if (src is null || !File.Exists(src))
            return Fail($"bridge DLL not found: {src ?? "(none)"}\n       build it: cmake -S tools/bridge -B tools/bridge/build -A x64 && cmake --build tools/bridge/build --config Release");

        // Inject a uniquely-named copy so a later rebuild never collides with a file lock.
        string tmpDir = Path.Combine(Path.GetTempPath(), "fruitylink-bridge");
        Directory.CreateDirectory(tmpDir);
        string dll = Path.Combine(tmpDir, $"FlBridge_{DateTime.Now:HHmmss_fff}.dll");
        try { File.Copy(src, dll, overwrite: true); }
        catch (Exception ex) { return Fail($"copy failed: {ex.Message}"); }

        Console.WriteLine($"FL64 pid={fl.Id}; injecting {Path.GetFileName(dll)} …");
        if (!LoadRemote(fl, dll, out string? err)) return Fail($"inject failed: {err}");

        Thread.Sleep(150);
        ProcessModule? mod = FindBridgeModule(Refresh(fl));
        if (mod is null) return Fail("LoadLibrary returned but the module isn't listed (it may have failed to load or anti-tamper blocked it). Check %TEMP%\\fruitylink-bridge.log.");
        Ok($"injected: {mod.ModuleName} @ 0x{mod.BaseAddress.ToInt64():x}");
        return Bridge(new List<string> { "bridge" }); // show status/ping
    }

    public static int Eject()
    {
        Process? fl = FindFl();
        if (fl is null) return Fail("FL Studio is not running.");
        var mods = AllBridgeModules(fl);
        if (mods.Count == 0) { Console.WriteLine("no FlBridge module loaded — nothing to eject."); return 0; }

        // 1) ask the worker to stop (so the overlapped pipe accept unblocks cleanly).
        TryPipe("shutdown", 800, out _);
        Thread.Sleep(150);

        // 2) FreeLibrary each loaded copy (DllMain DETACH joins the worker as a safety net).
        nint freeLib = ProcAddr("kernel32.dll", "FreeLibrary");
        if (freeLib == 0) return Fail("cannot resolve FreeLibrary.");
        foreach (ProcessModule m in mods)
        {
            Console.WriteLine($"FreeLibrary {m.ModuleName} @ 0x{m.BaseAddress.ToInt64():x} …");
            RemoteCall(fl, freeLib, m.BaseAddress, out _);
        }

        // 3) verify gone (retry; unload completes asynchronously)
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(120);
            if (AllBridgeModules(Refresh(fl)).Count == 0) return Ok("ejected cleanly.");
        }
        return Fail("module still present after FreeLibrary (a thread/hook may be pinning it — check the clean-unload contract).");
    }

    public static int Reload(List<string> pos)
    {
        int rc = Eject();
        if (rc != 0 && rc != 0) { /* eject prints its own status; continue to inject regardless */ }
        return Run(pos);
    }

    /// <summary>
    /// Install-model connect: talk to the proxy-loaded bridge over the pipe WITHOUT injecting.
    /// Never gates on the <c>FlBridge*</c> module heuristic (a proxy/in-process bridge won't match it);
    /// the ONLY success criterion is that the pipe answers.
    /// </summary>
    public static int Attach(List<string> pos)
    {
        Process? fl = FindFl();
        Console.WriteLine($"FL64 running : {(fl is not null ? $"yes (pid {fl.Id})" : "no")}");
        if (fl is not null)
        {
            // Informational only — proxy-installed bridges are loaded in-process and usually do NOT
            // surface a module named FlBridge*, so absence here is expected and NOT a failure.
            ProcessModule? m = FindBridgeModule(fl);
            Console.WriteLine($"bridge module: {(m is not null ? $"{m.ModuleName} @ 0x{m.BaseAddress.ToInt64():x} (injected dev path)" : "not detected via module heuristic (expected for proxy install)")}");
        }

        if (!TryPipe("ping", 2500, out string? ping))
            return Fail($"pipe '\\\\.\\pipe\\{PipeName}' not reachable: {ping}\n       Is FL running with the FruityLink proxy installed? (run bootstrap\\install.ps1, then launch FL)\n       Dev fallback: 'flprobe inject' to load the bridge manually.");
        Ok($"attached (no injection); ping -> {ping?.Trim()}");
        if (TryPipe("info", 3000, out string? info) && info is not null)
            Console.WriteLine("info -> " + info.Trim());
        return 0;
    }

    public static int Bridge(List<string> pos)
    {
        // pos[0] == "bridge"; the rest is the command to send.
        if (pos.Count <= 1)
        {
            Process? fl = FindFl();
            Console.WriteLine($"FL64 running : {(fl is not null ? $"yes (pid {fl.Id})" : "no")}");
            ProcessModule? m = fl is not null ? FindBridgeModule(fl) : null;
            Console.WriteLine($"bridge module: {(m is not null ? $"yes ({m.ModuleName} @ 0x{m.BaseAddress.ToInt64():x})" : "not detected (expected for proxy install)")}");
            // Do NOT gate on the module heuristic — the proxy-loaded bridge serves the pipe without a
            // FlBridge* module. The pipe reachability is the real status.
            return TryPipe("ping", 2000, out string? r)
                ? Ok($"ping -> {r?.Trim()}")
                : Fail($"pipe '\\\\.\\pipe\\{PipeName}' not reachable: {r} (proxy not installed / FL not running / bridge not injected).");
        }
        string cmd = string.Join(' ', pos.Skip(1));
        // No module pre-flight: attempt the pipe directly (works for both proxy-install and injected paths);
        // only the pipe being unreachable counts as failure.
        if (!TryPipe(cmd, 3000, out string? resp))
            return Fail($"pipe call '{cmd}' failed: {resp} (is the bridge loaded? proxy-install via bootstrap\\install.ps1, or dev 'flprobe inject').");
        Console.WriteLine(resp);
        return 0;
    }

    // ---- pipe client ---------------------------------------------------------

    internal static bool TryPipe(string message, int timeoutMs, out string? response)
    {
        response = null;
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(timeoutMs);
            pipe.ReadMode = PipeTransmissionMode.Message;
            byte[] outb = Encoding.UTF8.GetBytes(message);
            pipe.Write(outb, 0, outb.Length);
            pipe.Flush();
            var sb = new StringBuilder();
            byte[] buf = new byte[16384];
            do
            {
                int n = pipe.Read(buf, 0, buf.Length);
                if (n > 0) sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            } while (!pipe.IsMessageComplete);
            response = sb.ToString();
            return true;
        }
        catch (Exception ex) { response = ex.Message; return false; }
    }

    // ---- injection primitives ------------------------------------------------

    private static bool LoadRemote(Process target, string dllPath, out string? err)
    {
        err = null;
        nint loadLib = ProcAddr("kernel32.dll", "LoadLibraryW");
        if (loadLib == 0) { err = "cannot resolve LoadLibraryW"; return false; }

        nint h = OpenProcess(PROCESS_ACCESS, false, target.Id);
        if (h == 0) { err = $"OpenProcess failed ({Marshal.GetLastWin32Error()})"; return false; }
        try
        {
            byte[] path = Encoding.Unicode.GetBytes(dllPath + "\0");
            nint remote = VirtualAllocEx(h, 0, (nuint)path.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remote == 0) { err = $"VirtualAllocEx failed ({Marshal.GetLastWin32Error()})"; return false; }
            try
            {
                if (!WriteProcessMemory(h, remote, path, (nuint)path.Length, out _))
                { err = $"WriteProcessMemory failed ({Marshal.GetLastWin32Error()})"; return false; }

                nint thread = CreateRemoteThread(h, 0, 0, loadLib, remote, 0, out _);
                if (thread == 0) { err = $"CreateRemoteThread failed ({Marshal.GetLastWin32Error()})"; return false; }
                WaitForSingleObject(thread, 10000);
                GetExitCodeThread(thread, out uint code); // low 32 bits of HMODULE (informational)
                CloseHandle(thread);
                return true;
            }
            finally { VirtualFreeEx(h, remote, 0, MEM_RELEASE); }
        }
        finally { CloseHandle(h); }
    }

    private static void RemoteCall(Process target, nint func, nint arg, out uint exitCode)
    {
        exitCode = 0;
        nint h = OpenProcess(PROCESS_ACCESS, false, target.Id);
        if (h == 0) return;
        try
        {
            nint thread = CreateRemoteThread(h, 0, 0, func, arg, 0, out _);
            if (thread == 0) return;
            WaitForSingleObject(thread, 10000);
            GetExitCodeThread(thread, out exitCode);
            CloseHandle(thread);
        }
        finally { CloseHandle(h); }
    }

    // ---- process/module helpers ----------------------------------------------

    private static Process? FindFl() => Process.GetProcessesByName(FlProcess).FirstOrDefault();

    private static Process Refresh(Process p) { try { p.Refresh(); } catch { } return p; }

    private static ProcessModule? FindBridgeModule(Process p) => AllBridgeModules(p).FirstOrDefault();

    private static List<ProcessModule> AllBridgeModules(Process p)
    {
        var list = new List<ProcessModule>();
        try
        {
            foreach (ProcessModule m in p.Modules)
                if (m.ModuleName is not null && m.ModuleName.StartsWith(ModulePrefix, StringComparison.OrdinalIgnoreCase))
                    list.Add(m);
        }
        catch { /* module list can momentarily fail; caller retries */ }
        return list;
    }

    private static nint ProcAddr(string module, string proc)
    {
        nint h = GetModuleHandle(module);
        if (h == 0) h = LoadLibrary(module);
        return h == 0 ? 0 : GetProcAddress(h, proc);
    }

    private static string? DefaultDllPath()
    {
        // walk up from the exe dir to the repo root (FruityLink.slnx), then to the cmake output.
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "FruityLink.slnx")))
                return Path.Combine(dir, "tools", "bridge", "build", "Release", "FlBridge.dll");
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    /// <summary>
    /// Directory of the running FL64.exe (its install root), or null if FL isn't running / unreadable.
    /// Used to derive the default plugins dir (&lt;installDir&gt;\FruityLink\plugins) for the install model.
    /// </summary>
    internal static string? FlInstallDir()
    {
        Process? fl = FindFl();
        if (fl is null) return null;
        try { return Path.GetDirectoryName(fl.MainModule?.FileName); }
        catch { return null; }
    }

    internal static int Ok(string s) { Console.WriteLine("[OK]   " + s); return 0; }
    internal static int Fail(string s) { Console.WriteLine("[FAIL] " + s); return 2; }

    // ---- P/Invoke ------------------------------------------------------------

    private const uint PROCESS_ACCESS = 0x43A; // CREATE_THREAD|VM_OP|VM_WRITE|VM_READ|QUERY_INFO
    private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000, PAGE_READWRITE = 0x04;

    [DllImport("kernel32", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32", SetLastError = true)] private static extern nint VirtualAllocEx(nint h, nint addr, nuint size, uint type, uint protect);
    [DllImport("kernel32", SetLastError = true)] private static extern bool VirtualFreeEx(nint h, nint addr, nuint size, uint type);
    [DllImport("kernel32", SetLastError = true)] private static extern bool WriteProcessMemory(nint h, nint addr, byte[] buf, nuint size, out nuint written);
    [DllImport("kernel32", SetLastError = true)] private static extern nint CreateRemoteThread(nint h, nint attrs, nuint stack, nint start, nint param, uint flags, out uint tid);
    [DllImport("kernel32", SetLastError = true)] private static extern uint WaitForSingleObject(nint h, uint ms);
    [DllImport("kernel32", SetLastError = true)] private static extern bool GetExitCodeThread(nint h, out uint code);
    [DllImport("kernel32", SetLastError = true)] private static extern bool CloseHandle(nint h);
    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)] private static extern nint GetModuleHandle(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)] private static extern nint LoadLibrary(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)] private static extern nint GetProcAddress(nint mod, string name);
}
