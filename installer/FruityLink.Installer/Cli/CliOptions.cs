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
    public bool WithoutMcp { get; private set; }
    public bool WithoutPythonIde { get; private set; }
    public bool WithoutSerumSupport { get; private set; }
    public bool ConfigureMcp { get; private set; }
    public bool ListMcpClients { get; private set; }

    public string? FlPath { get; private set; }
    public string? ManifestPath { get; private set; }
    public string? PayloadRoot { get; private set; }
    public string? McpProfile { get; private set; }
    public string? McpAppData { get; private set; }
    public string? McpLocalAppData { get; private set; }
    public string? McpCodexHome { get; private set; }
    public string? McpPythonRuntime { get; private set; }
    public string? McpTemplate { get; private set; }
    public string? McpWorkspace { get; private set; }
    public List<string> McpClientIds { get; } = new();

    /// <summary>
    /// Community plugin ids to pre-select in the GUI (internal: preserves the user's checkbox
    /// selection across the elevation relaunch).
    /// </summary>
    public List<string> CommunityPluginIds { get; } = new();

    public List<string> Unknown { get; } = new();

    /// <summary>True when we should show the WPF window instead of running headless.</summary>
    public bool RunGui => ForceGui || (!IsHeadlessVerb && !Help && !ShowVersion);

    /// <summary>True when a headless verb/flag was supplied.</summary>
    public bool IsHeadlessVerb => Install || Uninstall || SelfTest || PrintManifest || PrintFlHashes || ConfigureMcp || ListMcpClients;

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

            if (TryParseMcpOption(o, arg.ToLowerInvariant(), Value)) continue;

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
                case "--community-plugins":
                    var ids = Value();
                    if (!string.IsNullOrWhiteSpace(ids))
                        o.CommunityPluginIds.AddRange(
                            ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--force":
                case "-f":
                    o.Force = true;
                    break;
                case "--without-mcp":
                    o.WithoutMcp = true;
                    break;
                case "--without-python-ide":
                    o.WithoutPythonIde = true;
                    break;
                case "--without-serum-support":
                    o.WithoutSerumSupport = true;
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

    private static bool TryParseMcpOption(CliOptions options, string arg, Func<string?> value)
    {
        if (arg == "--configure-mcp") { options.ConfigureMcp = true; return true; }
        if (arg == "--list-mcp-clients") { options.ListMcpClients = true; return true; }
        if (!McpValues.TryGetValue(arg, out var assign)) return false;
        var supplied = value();
        if (string.IsNullOrWhiteSpace(supplied)) options.Unknown.Add(arg + " requires a value");
        else assign(options, supplied);
        return true;
    }

    private static readonly Dictionary<string, Action<CliOptions, string>> McpValues = new()
    {
        ["--mcp-clients"] = (o, v) => o.McpClientIds.AddRange(v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
        ["--mcp-profile"] = (o, v) => o.McpProfile = v,
        ["--mcp-app-data"] = (o, v) => o.McpAppData = v,
        ["--mcp-local-app-data"] = (o, v) => o.McpLocalAppData = v,
        ["--mcp-codex-home"] = (o, v) => o.McpCodexHome = v,
        ["--mcp-python-runtime"] = (o, v) => o.McpPythonRuntime = v,
        ["--mcp-template"] = (o, v) => o.McpTemplate = v,
        ["--mcp-workspace"] = (o, v) => o.McpWorkspace = v,
    };

    public const string Usage = """
FruityLink Installer - installs / uninstalls FruityLink (proxy version.dll) into FL Studio.

USAGE
  FruityLink.Installer                         Launch the GUI (default when no args).
  FruityLink.Installer --install [options]     Install headlessly.
  FruityLink.Installer --uninstall [options]   Uninstall headlessly.

VERBS
  --install, -i            Copy the FruityLink payload into FL Studio (backs up version.dll).
  --uninstall, -u          Remove FruityLink and restore the original version.dll.
  --configure-mcp         Connect selected clients to an already installed FLMCP.
  --list-mcp-clients      List supported MCP clients and local detection results.
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
  --without-mcp           Exclude FLMCP (selected by default); keep the framework Python backend.
  --without-python-ide    Exclude the Python editor plugin (selected by default).
  --without-serum-support Exclude Serum preset inventory and audition helpers (selected by default).
  --mcp-clients <ids>     Connect only these clients; comma-separated IDs from --list-mcp-clients.
  --mcp-template <file>   Saved FLP template. Default: FL's Data\Templates\Empty\Empty.flp.
  --mcp-workspace <dir>   Project/output directory. Default: <local app data>\FlMcp\Projects.
  --mcp-python-runtime <dir> Optional CPython runtime directory. Default: bundled private Python.
  --mcp-profile <dir>     Target user profile (preserved across elevation).
  --mcp-app-data <dir>    Target user's roaming application data directory.
  --mcp-local-app-data <dir> Target user's local application data directory.
  --mcp-codex-home <dir>  Codex config directory. Default: CODEX_HOME or <profile>\.codex.
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
  FruityLink.Installer --install --silent --mcp-clients codex,claude-code
  FruityLink.Installer --configure-mcp --mcp-clients codex --dry-run
""";
}
