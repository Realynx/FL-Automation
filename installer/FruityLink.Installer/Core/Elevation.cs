using System;
using System.Diagnostics;
using System.Security.Principal;

namespace FruityLink.Installer.Core;

/// <summary>Admin-rights detection and self-elevation (UAC) helpers.</summary>
public static class Elevation
{
    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Re-launches this executable elevated (UAC prompt) with the given arguments. Returns true if
    /// the elevated process was started. The caller should then exit so only the elevated instance
    /// continues. Returns false if the user declined the prompt or launch failed.
    /// </summary>
    public static bool RelaunchAsAdmin(string[] args, string? extraArg = null)
    {
        try
        {
            var exe = Environment.ProcessPath
                      ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe))
                return false;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas", // triggers UAC
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            if (!string.IsNullOrEmpty(extraArg))
                psi.ArgumentList.Add(extraArg);

            Process.Start(psi);
            return true;
        }
        catch
        {
            // Most commonly: user clicked "No" on the UAC prompt (Win32 1223).
            return false;
        }
    }
}
