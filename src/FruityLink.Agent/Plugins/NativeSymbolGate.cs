using System.Linq;
using FruityLink.Core.Abstractions;

namespace FruityLink.Agent.Plugins;

/// <summary>
/// Explicit, deliberately SMALL map from the highest-risk native tools to the reverse-engineered FL
/// symbol(s) their bridge call site invokes. Multi-version safety: when the bridge's <c>syms</c>
/// diagnostic (<see cref="IFlSymbolResolution"/>) reports a required symbol UNRESOLVED on the running
/// FL build — a byte-signature that didn't match this version — the tool is pruned from the kernel so
/// it is never advertised to, or invoked by, the model. The alternative on a version where the code
/// moved is a wrong address = an uncatchable access violation inside FL.
///
/// <para>NOT exhaustive on purpose. The vast majority of native tools still call through hardcoded,
/// version-rebased hex and are ALWAYS advertised; only tools that route through a dedicated FL
/// function in the sigscan table (<c>tools/bridge/sigscan.cpp</c>) whose disappearance would be
/// UNSAFE are listed. Each mapping was verified against the tool's bridge call site (the exact
/// Ghidra address matches the sigscan entry). Keep this list short and honest — add an entry only
/// when a tool depends on a table symbol AND calling it at a wrong address would fault FL.</para>
/// </summary>
internal static class NativeSymbolGate
{
    // model-visible tool name -> sigscan symbol name(s) it needs (ALL required; any missing → gated).
    private static readonly IReadOnlyDictionary<string, string[]> Map =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // Arrangements — FLar_* (FlInjectBridge.Project.cs: AddArrangement 0x11FABC0,
            // CopyInto 0x11FB420, SetName 0x11FB0D0, Delete 0x11FB1C0).
            ["native_make_arrangement"]   = new[] { "FLar_AddArrangement" },
            ["native_clone_arrangement"]  = new[] { "FLar_AddArrangement", "FLar_CopyInto" },
            ["native_rename_arrangement"] = new[] { "FLar_SetName" },
            ["native_delete_arrangement"] = new[] { "FLar_Delete" },

            // Rename tools that call a dedicated FL setter so the name persists across save/reload.
            ["native_set_pattern_name"]   = new[] { "FLpat_SetPatternName" },   // FlInjectBridge.Patterns.cs 0x11D3960
            ["native_set_track_name"]     = new[] { "FLpl_SetTrackNameColor" }, // FlInjectBridge.Playlist.cs 0x11E7940

            // Timeline marker add routes through FLtr_AddTimelineMarkerCore (0xD523C0). The
            // [KernelFunction] is currently DISABLED (raw core call freezes the DAW), so this is inert
            // today — kept so the mapping is already correct when it is re-enabled behind a safe path.
            ["native_add_marker"]         = new[] { "FLtr_AddTimelineMarkerCore" },
        };

    /// <summary>
    /// Tool names whose required symbol(s) are in <paramref name="unresolved"/> — i.e. tools that are
    /// UNSAFE on this FL build and must not be advertised. Empty when nothing is gated (the common
    /// case: every listed symbol resolved).
    /// </summary>
    public static IReadOnlyCollection<string> UnavailableTools(IReadOnlySet<string> unresolved)
    {
        if (unresolved.Count == 0) return Array.Empty<string>();
        var hidden = new List<string>();
        foreach ((string tool, string[] needs) in Map)
            if (needs.Any(unresolved.Contains))
                hidden.Add(tool);
        return hidden;
    }

    /// <summary>Human-readable FL version label for the detected version index (logging).</summary>
    public static string VersionName(int version) => version switch
    {
        1 => "25.2.5 (2025)",
        2 => "26.1.0 (2026)",
        _ => "unknown",
    };
}
