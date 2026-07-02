using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace FruityLink.Probe;

/// <summary>
/// The plugin build/install/hot-reload loop for the proxy-installed FruityLink host
/// (re/integration-pending-plugin-host.md). The host discovers plugins from
/// <c>&lt;host-dir&gt;\plugins\&lt;id&gt;\</c> and hot-reloads on file change (FileSystemWatcher + shadow-copy),
/// so simply overwriting the files there triggers a reload. We additionally call the debug-pipe
/// handlers documented in <c>re/integration-pending-probe-debug.md</c>:
///
///   plugins_list                       -> JSON array of { id, name, version, enabled, loaded }
///   plugins_dir                        -> absolute path of the active plugins directory
///   plugin_enable|disable|reload &lt;id&gt;   -> "ok" or "err: &lt;reason&gt;"
///
/// Every subcommand degrades gracefully: if the bridge does not implement a command yet (or the pipe
/// is unreachable), we print the raw pipe error and exit non-zero rather than throwing. The file copy
/// is the substantive action of install/dev — once the files land, the host watcher hot-reloads even
/// if the best-effort reload/enable pipe calls don't land.
///
///   flprobe plugin list
///   flprobe plugin install <publishDirOrDll> [--id <id>] [--plugins-dir <dir>]
///   flprobe plugin update  <publishDirOrDll> [--id <id>] [--plugins-dir <dir>]   (alias for install)
///   flprobe plugin enable|disable|reload <id>
///   flprobe plugin dev <projectDirOrCsproj> [--id <id>] [--plugins-dir <dir>]    (publish -c Debug, then install)
/// </summary>
internal static class Plugin
{
    // pos[0] == "plugin" (or "plugins"); pos[1] == subcommand; the rest are its args.
    public static int Run(List<string> pos)
    {
        string sub = pos.Count > 1 ? pos[1].ToLowerInvariant() : "";
        List<string> rest = pos.Skip(2).ToList();
        return sub switch
        {
            "list" or "ls" => List(),
            "install" or "update" => Install(rest),
            "enable" => Toggle("plugin_enable", rest),
            "disable" => Toggle("plugin_disable", rest),
            "reload" => Toggle("plugin_reload", rest),
            "dev" => Dev(rest),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.WriteLine("flprobe plugin — build / install / hot-reload FruityLink plugins");
        Console.WriteLine("  list                                              list installed plugins (pipe 'plugins_list')");
        Console.WriteLine("  install <publishDirOrDll> [--id <id>] [--plugins-dir <dir>]   copy a publish closure -> <pluginsDir>\\<id>\\, then reload+enable");
        Console.WriteLine("  update  <publishDirOrDll> [--id <id>] [--plugins-dir <dir>]   alias for install");
        Console.WriteLine("  dev     <projectDirOrCsproj> [--id <id>] [--plugins-dir <dir>]   dotnet publish -c Debug, then install");
        Console.WriteLine("  enable  <id>      enable a plugin   (pipe 'plugin_enable')");
        Console.WriteLine("  disable <id>      disable a plugin  (pipe 'plugin_disable')");
        Console.WriteLine("  reload  <id>      reload a plugin   (pipe 'plugin_reload')");
        return 1;
    }

    // ---- subcommands ---------------------------------------------------------

    private static int List()
    {
        if (!Inject.TryPipe("plugins_list", 4000, out string? r) || r is null)
            return Inject.Fail($"plugins_list: pipe unreachable ({r}) — is FL running with the proxy installed? (bootstrap\\install.ps1)");
        if (LooksLikeError(r))
            return Inject.Fail($"plugins_list -> {r.Trim()} (bridge debug pipe handler not implemented yet?)");
        Console.WriteLine(PrettyJson(r));
        return 0;
    }

    private static int Toggle(string pipeCmd, List<string> rest)
    {
        string? id = rest.FirstOrDefault(a => !a.StartsWith("--"));
        if (string.IsNullOrWhiteSpace(id))
            return Inject.Fail($"usage: flprobe plugin {pipeCmd.Replace("plugin_", "")} <id>");
        return SendToggle($"{pipeCmd} {id}");
    }

    private static int Install(List<string> rest)
    {
        (Dictionary<string, string> flags, List<string> positional) = ParseFlags(rest);
        if (positional.Count < 1)
            return Inject.Fail("usage: flprobe plugin install <publishDirOrDll> [--id <id>] [--plugins-dir <dir>]");

        if (!ResolveSource(positional[0], out string? srcDir, out string? inferredId, out string srcErr))
            return Inject.Fail(srcErr);

        string id = flags.TryGetValue("id", out string? idOverride) && !string.IsNullOrWhiteSpace(idOverride)
            ? idOverride
            : inferredId!;
        flags.TryGetValue("plugins-dir", out string? dirOverride);
        return DoInstall(srcDir!, id, dirOverride);
    }

    private static int Dev(List<string> rest)
    {
        (Dictionary<string, string> flags, List<string> positional) = ParseFlags(rest);
        if (positional.Count < 1)
            return Inject.Fail("usage: flprobe plugin dev <projectDirOrCsproj> [--id <id>] [--plugins-dir <dir>]");

        if (!ResolveCsproj(positional[0], out string? csproj, out string projErr))
            return Inject.Fail(projErr);

        string projName = Path.GetFileNameWithoutExtension(csproj!);
        string id = flags.TryGetValue("id", out string? idOverride) && !string.IsNullOrWhiteSpace(idOverride)
            ? idOverride
            : projName;
        flags.TryGetValue("plugins-dir", out string? dirOverride);

        // Publish the full closure (main dll + deps + .deps.json) into a clean temp stage dir.
        string stage = Path.Combine(Path.GetTempPath(), "flprobe-publish", projName);
        try { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); } catch { /* best-effort clean */ }
        Directory.CreateDirectory(stage);

        Console.WriteLine($"publishing  : {csproj}  (-c Debug)");
        int rc = RunDotnet($"publish \"{csproj}\" -c Debug -o \"{stage}\"", out string output);
        if (output.Length > 0) Console.WriteLine(output.TrimEnd());
        if (rc != 0)
            return Inject.Fail($"dotnet publish failed (exit {rc}). See output above.");

        return DoInstall(stage, id, dirOverride);
    }

    // ---- install core --------------------------------------------------------

    private static int DoInstall(string srcDir, string id, string? dirOverride)
    {
        string? pluginsDir = ResolvePluginsDir(dirOverride, out string source);
        if (pluginsDir is null)
            return Inject.Fail("could not resolve a plugins directory (pipe 'plugins_dir' unavailable and FL not found). Pass --plugins-dir <dir>.");

        string dst = Path.Combine(pluginsDir, id);
        Console.WriteLine($"plugin id   : {id}");
        Console.WriteLine($"plugins dir : {pluginsDir}  (via {source})");
        Console.WriteLine($"source      : {srcDir}");
        Console.WriteLine($"install to  : {dst}");

        int files; long bytes;
        try { (files, bytes) = CopyTree(srcDir, dst); }
        catch (Exception ex) { return Inject.Fail($"copy failed: {ex.Message}"); }
        Inject.Ok($"copied {files} file(s), {bytes / 1024} KB -> {dst} (host watcher will hot-reload)");

        // Best-effort nudge over the debug pipe — the file-copy above already triggers the watcher,
        // so these are advisory: print results, but never fail the install just because the pipe is down
        // or the handler isn't wired yet.
        BestEffort($"plugin_reload {id}");
        BestEffort($"plugin_enable {id}");
        return 0;
    }

    private static void BestEffort(string cmd)
    {
        if (!Inject.TryPipe(cmd, 5000, out string? r) || r is null)
            Console.WriteLine($"[warn] {cmd}: pipe unavailable ({r}) — the host's file watcher still hot-reloads once FL is running with the proxy.");
        else if (LooksLikeError(r))
            Console.WriteLine($"[warn] {cmd} -> {r.Trim()} (bridge debug pipe handler not implemented yet?)");
        else
            Console.WriteLine($"[OK]   {cmd} -> {r.Trim()}");
    }

    private static int SendToggle(string cmd)
    {
        if (!Inject.TryPipe(cmd, 5000, out string? r) || r is null)
            return Inject.Fail($"'{cmd}': pipe unreachable ({r}) — is FL running with the proxy installed? (bootstrap\\install.ps1)");
        if (LooksLikeError(r))
            return Inject.Fail($"{cmd} -> {r.Trim()} (bridge debug pipe handler not implemented yet?)");
        return Inject.Ok($"{cmd} -> {r.Trim()}");
    }

    // ---- resolution helpers --------------------------------------------------

    /// <summary>
    /// Active plugins dir. Order (per integration contract): authoritative pipe 'plugins_dir';
    /// else explicit --plugins-dir; else derived default
    /// (&lt;FL install dir&gt;\FruityLink\plugins, found via the running FL64 or a Program Files scan).
    /// </summary>
    private static string? ResolvePluginsDir(string? overrideDir, out string source)
    {
        // 1) authoritative: ask the running host over the pipe.
        if (Inject.TryPipe("plugins_dir", 2000, out string? r) && r is not null)
        {
            string p = r.Trim();
            if (p.Length > 0 && !LooksLikeError(p))
            {
                source = "pipe 'plugins_dir'";
                return p;
            }
        }
        // 2) explicit override.
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            source = "--plugins-dir";
            return Path.GetFullPath(overrideDir);
        }
        // 3) derive from the running FL64 install dir.
        string? fld = Inject.FlInstallDir();
        if (fld is not null)
        {
            source = "FL64 install dir";
            return Path.Combine(fld, "FruityLink", "plugins");
        }
        // 4) scan Program Files for an FL Studio install.
        string? pf = FindFlInProgramFiles();
        if (pf is not null)
        {
            source = "Program Files FL Studio";
            return Path.Combine(pf, "FruityLink", "plugins");
        }
        source = "(unresolved)";
        return null;
    }

    private static string? FindFlInProgramFiles()
    {
        foreach (string? root in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramW6432"),
                     Environment.GetEnvironmentVariable("ProgramFiles"),
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            string imageLine = Path.Combine(root, "Image-Line");
            if (!Directory.Exists(imageLine)) continue;
            string[] dirs;
            try { dirs = Directory.GetDirectories(imageLine, "FL Studio*"); }
            catch { continue; }
            // prefer an install that already has a FruityLink subdir, else the newest-named dir.
            List<string> withFl = dirs.Where(d => Directory.Exists(Path.Combine(d, "FruityLink"))).ToList();
            string? pick = (withFl.Count > 0 ? withFl : dirs.ToList())
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (pick is not null) return pick;
        }
        return null;
    }

    /// <summary>Resolve the publish source to a directory + an inferred id (folder name or dll base name).</summary>
    private static bool ResolveSource(string srcArg, out string? srcDir, out string? inferredId, out string err)
    {
        srcDir = null;
        inferredId = null;
        err = "";
        string full;
        try { full = Path.GetFullPath(srcArg); }
        catch (Exception ex) { err = $"bad path '{srcArg}': {ex.Message}"; return false; }

        if (File.Exists(full)) // a dll (or any file) — the closure is its containing directory.
        {
            srcDir = Path.GetDirectoryName(full);
            inferredId = Path.GetFileNameWithoutExtension(full);
            if (srcDir is null) { err = $"cannot resolve directory of {full}"; return false; }
            return true;
        }
        if (Directory.Exists(full)) // a publish folder — copy it whole; infer id from the folder name.
        {
            srcDir = full;
            inferredId = new DirectoryInfo(full).Name;
            return true;
        }
        err = $"publish path not found: {full}";
        return false;
    }

    private static bool ResolveCsproj(string arg, out string? csproj, out string err)
    {
        csproj = null;
        err = "";
        string full;
        try { full = Path.GetFullPath(arg); }
        catch (Exception ex) { err = $"bad path '{arg}': {ex.Message}"; return false; }

        if (File.Exists(full) && full.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            csproj = full;
            return true;
        }
        if (Directory.Exists(full))
        {
            string[] projs = Directory.GetFiles(full, "*.csproj", SearchOption.TopDirectoryOnly);
            if (projs.Length == 1) { csproj = projs[0]; return true; }
            if (projs.Length == 0) { err = $"no .csproj found in {full}"; return false; }
            err = $"multiple .csproj in {full}; pass the .csproj path explicitly."; return false;
        }
        err = $"project path not found: {full}";
        return false;
    }

    // ---- io / process helpers ------------------------------------------------

    private static (int files, long bytes) CopyTree(string srcDir, string dstDir)
    {
        Directory.CreateDirectory(dstDir);
        int files = 0;
        long bytes = 0;
        foreach (string f in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(srcDir, f);
            string dst = Path.Combine(dstDir, rel);
            string? dstParent = Path.GetDirectoryName(dst);
            if (dstParent is not null) Directory.CreateDirectory(dstParent);
            File.Copy(f, dst, overwrite: true);
            files++;
            bytes += new FileInfo(f).Length;
        }
        return (files, bytes);
    }

    private static int RunDotnet(string args, out string output)
    {
        var psi = new ProcessStartInfo("dotnet", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var sb = new StringBuilder();
        try
        {
            using Process? p = Process.Start(psi);
            if (p is null) { output = "could not start 'dotnet' (is the .NET SDK on PATH?)"; return -1; }
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            output = sb.ToString();
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            output = "dotnet launch failed: " + ex.Message;
            return -1;
        }
    }

    // ---- arg parsing + formatting --------------------------------------------

    /// <summary>Pulls <c>--key value</c> pairs out of an arg list; everything else is positional.</summary>
    private static (Dictionary<string, string> flags, List<string> positional) ParseFlags(List<string> args)
    {
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positional = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                string key = a[2..];
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    flags[key] = args[++i];
                else
                    flags[key] = ""; // valueless flag
            }
            else
            {
                positional.Add(a);
            }
        }
        return (flags, positional);
    }

    private static bool LooksLikeError(string s) =>
        s.TrimStart().StartsWith("err", StringComparison.OrdinalIgnoreCase);

    private static string PrettyJson(string raw)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return raw.Trim();
        }
    }
}
