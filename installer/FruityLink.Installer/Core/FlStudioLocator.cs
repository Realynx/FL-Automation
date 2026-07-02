using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace FruityLink.Installer.Core;

/// <summary>Result of validating an FL Studio directory.</summary>
public sealed record FlValidation(bool IsValid, string Path, string? ExePath, string? Reason);

/// <summary>
/// Finds and validates the FL Studio install directory. "Valid" = the directory contains
/// <c>FL64.exe</c> (the shell that loads FLEngine_x64.dll, which statically imports our proxy
/// version.dll — see re/15-proxy-install.md).
/// </summary>
public static class FlStudioLocator
{
    public const string ExeName = "FL64.exe";

    /// <summary>Documented default install location.</summary>
    public const string DefaultPath = @"C:\Program Files\Image-Line\FL Studio 2025\";

    public static FlValidation Validate(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return new FlValidation(false, directory ?? string.Empty, null, "No path supplied.");

        // Normalize: strip surrounding quotes AND any trailing separator so paths that differ only by a
        // trailing "\" (e.g. the default "…\FL Studio 2025\" vs a registry "…\FL Studio 2025") compare
        // equal and Detect() de-dups them instead of listing the same install twice.
        var dir = directory.Trim().Trim('"').TrimEnd('\\', '/');
        if (!Directory.Exists(dir))
            return new FlValidation(false, dir, null, "Directory does not exist.");

        var exe = Path.Combine(dir, ExeName);
        if (!File.Exists(exe))
            return new FlValidation(false, dir, null, $"{ExeName} not found in this directory.");

        return new FlValidation(true, dir, exe, null);
    }

    /// <summary>All valid FL directories found, most-likely first, de-duplicated.</summary>
    public static IReadOnlyList<string> Detect()
    {
        var found = new List<string>();
        void Add(string? d)
        {
            if (string.IsNullOrWhiteSpace(d)) return;
            var v = Validate(d);
            if (v.IsValid && !found.Any(f => string.Equals(f, v.Path, StringComparison.OrdinalIgnoreCase)))
                found.Add(v.Path);
        }

        // 1. Documented default.
        Add(DefaultPath);

        // 2. Scan the Image-Line program folders for "FL Studio *".
        foreach (var root in ProgramFilesRoots())
        {
            var il = Path.Combine(root, "Image-Line");
            if (!Directory.Exists(il)) continue;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(il, "FL Studio*"); }
            catch { continue; }
            // Prefer the newest-named folder (e.g. 2026 over 2025).
            foreach (var sub in subs.OrderByDescending(s => s, StringComparer.OrdinalIgnoreCase))
                Add(sub);
        }

        // 3. Registry uninstall keys (covers non-default drives).
        foreach (var loc in RegistryInstallLocations())
            Add(loc);

        return found;
    }

    /// <summary>The single best guess, or null if FL Studio isn't found.</summary>
    public static string? DetectBest() => Detect().FirstOrDefault();

    private static IEnumerable<string> ProgramFilesRoots()
    {
        foreach (var v in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetEnvironmentVariable("ProgramW6432"),
                 })
        {
            if (!string.IsNullOrWhiteSpace(v))
                yield return v!;
        }
    }

    private static IEnumerable<string> RegistryInstallLocations()
    {
        var results = new List<string>();
        var roots = new (RegistryHive Hive, RegistryView View, string Sub)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var (hive, view, sub) in roots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var unin = baseKey.OpenSubKey(sub);
                if (unin is null) continue;
                foreach (var name in unin.GetSubKeyNames())
                {
                    try
                    {
                        using var app = unin.OpenSubKey(name);
                        var display = app?.GetValue("DisplayName") as string;
                        if (display is null || display.IndexOf("FL Studio", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        if (app!.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc))
                            results.Add(loc);
                    }
                    catch { /* skip unreadable entries */ }
                }
            }
            catch { /* registry unavailable / access denied */ }
        }

        return results;
    }
}
