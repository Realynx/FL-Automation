using System;
using System.Collections.Generic;

namespace FruityLink.Installer.Cli;

/// <summary>Parsed command line. No args => GUI; any recognized verb/flag => headless CLI.</summary>
public sealed class CliOptions
{
    public bool Install { get; private set; }
    public bool Uninstall { get; private set; }
    public bool Silent { get; private set; }
    public bool DryRun { get; private set; }
    public bool Help { get; private set; }
    public bool ShowVersion { get; private set; }
    public bool SelfTest { get; private set; }
    public bool PrintManifest { get; private set; }
    public bool PrintFlHashes { get; private set; }
    public bool ForceGui { get; private set; }
    public bool Force { get; private set; }

    public string? FlPath { get; private set; }
    public string? ManifestPath { get; private set; }
    public string? PayloadRoot { get; private set; }

    public List<string> Unknown { get; } = new();

    /// <summary>True when we should show the WPF window instead of running headless.</summary>
    public bool RunGui => ForceGui || (!IsHeadlessVerb && !Help && !ShowVersion);

    /// <summary>True when a headless verb/flag was supplied.</summary>
    public bool IsHeadlessVerb => Install || Uninstall || SelfTest || PrintManifest || PrintFlHashes;

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            var arg = raw;
            string? inlineValue = null;

            // Support --flag=value.
            var eq = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && eq > 0)
            {
                inlineValue = arg[(eq + 1)..];
                arg = arg[..eq];
            }

            string? Value()
            {
                if (inlineValue is not null) return inlineValue;
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) return args[++i];
                return null;
            }

            switch (arg.ToLowerInvariant())
            {
                case "--install":
                case "-i":
                case "/install":
                    o.Install = true;
                    break;
                case "--uninstall":
                case "-u":
                case "/uninstall":
                    o.Uninstall = true;
                    break;
                case "--silent":
                case "--headless":
                case "-s":
                case "/silent":
                    o.Silent = true;
                    break;
                case "--dry-run":
                case "--dryrun":
                case "-n":
                case "/dry-run":
                    o.DryRun = true;
                    break;
                case "--fl-path":
                case "--flpath":
                case "--path":
                case "-p":
                    o.FlPath = Value();
                    break;
                case "--manifest":
                case "-m":
                    o.ManifestPath = Value();
                    break;
                case "--payload-root":
                case "--payload":
                    o.PayloadRoot = Value();
                    break;
                case "--force":
                case "-f":
                    o.Force = true;
                    break;
                case "--gui":
                case "-g":
                    o.ForceGui = true;
                    break;
                case "--self-test":
                case "--selftest":
                    o.SelfTest = true;
                    break;
                case "--print-manifest":
                case "--printmanifest":
                    o.PrintManifest = true;
                    break;
                case "--print-fl-hashes":
                case "--printflhashes":
                    o.PrintFlHashes = true;
                    break;
                case "--help":
                case "-h":
                case "-?":
                case "/?":
                    o.Help = true;
                    break;
                case "--version":
                case "-v":
                    o.ShowVersion = true;
                    break;
                default:
                    o.Unknown.Add(raw);
                    break;
            }
        }

        return o;
    }

    public const string Usage = """
FruityLink Installer - installs / uninstalls FruityLink (proxy version.dll) into FL Studio.

USAGE
  FruityLink.Installer                         Launch the GUI (default when no args).
  FruityLink.Installer --install [options]     Install headlessly.
  FruityLink.Installer --uninstall [options]   Uninstall headlessly.

VERBS
  --install, -i            Copy the FruityLink payload into FL Studio (backs up version.dll).
  --uninstall, -u          Remove FruityLink and restore the original version.dll.
  --self-test              Run an end-to-end install+uninstall against a throwaway temp FL dir.
  --print-manifest         Print the effective payload manifest (JSON) and exit.
  --print-fl-hashes        Hash the FL install's binaries into a compatibility.json entry (dev tool
                           for adding a newly verified FL build to the packed list).
  --gui, -g                Force the GUI even if other flags are present.

OPTIONS
  --fl-path <dir>, -p      FL Studio directory. Default: auto-detect, else
                           "C:\Program Files\Image-Line\FL Studio 2025\".
  --dry-run, -n            Print every action without writing anything.
  --silent, --headless, -s No prompts; for unattended / GitHub one-line installs.
  --manifest <file>, -m    Use an external manifest.json instead of the built-in default.
  --payload-root <dir>     Where the payload files live. Default: <exe dir>\payload.
  --force, -f              Proceed past non-fatal validation warnings, INCLUDING the verified-build
                           and file-integrity gates (unsupported; for development only).
  --help, -h               Show this help.
  --version, -v            Show the installer version.

Installs are gated on compatibility.json (packed next to the EXE): the FL build must be on the
manually verified list AND FL's binaries must hash-match that official build. Unverified builds
and modified/unofficial FL installs are refused. Uninstall is never gated.

EXIT CODES
  0 success   1 error   2 bad arguments   3 FL not found / invalid
  4 payload missing      5 needs elevation (run as administrator)
  6 reboot required (some files were locked and scheduled for the next reboot)
  7 FL build not on the verified compatibility list
  8 FL file integrity check failed (modified / unofficial FL install)

EXAMPLES
  FruityLink.Installer --install --silent
  FruityLink.Installer --install --fl-path "D:\FL Studio 2025" --dry-run
  FruityLink.Installer --uninstall --silent
""";
}
