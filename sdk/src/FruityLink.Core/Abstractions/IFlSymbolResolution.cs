namespace FruityLink.Core.Abstractions;

/// <summary>
/// Result of the native bridge's <c>syms</c> diagnostic (see <c>native/bridge/sigscan.*</c>): the
/// FL version the bridge detected at runtime plus the set of reverse-engineered FL symbols it could
/// NOT locate on THIS FL build. The bridge selects a supported scanner profile and resolves addresses
/// by byte-signature scan; a symbol whose signature did not match this version is
/// reported here so a tool depending on it can be hidden instead of firing a wrong address (an
/// uncatchable access violation inside FL).
/// </summary>
/// <param name="Version">Legacy exact-build index (0 = unknown; 1 = 25.2.5.5319; 2 = 26.1.0.5530).</param>
/// <param name="Resolved">Count of symbols that resolved OK.</param>
/// <param name="Failed">Count of symbols that did not resolve.</param>
/// <param name="Unresolved">Names of the symbols that did not resolve (ordinal-compared).</param>
public sealed record FlSymbolStatus(
    int Version,
    int Resolved,
    int Failed,
    IReadOnlySet<string> Unresolved)
{
    /// <summary>Full engine file version reported by the native bridge; null for older diagnostics.</summary>
    public string? FileVersion { get; init; }

    /// <summary>Selected native scanner profile name, or "unsupported"; null for older diagnostics.</summary>
    public string? Scanner { get; init; }

    /// <summary>Whether the native bridge supports scanning this engine version. True does not imply
    /// every symbol resolved or every object layout is verified. Null means the field was not reported.</summary>
    public bool? Supported { get; init; }

    /// <summary>Whether native resolution finished, including a completed scan with no resolved symbols.
    /// False means startup is still pending; null identifies a legacy or unspecified completion field.</summary>
    public bool? Complete { get; init; }

    /// <summary>Diagnostic mixer-track stride, including exact-version compatibility for older bridges.
    /// This scalar alone cannot authorize raw mixer operations without a complete <see cref="MixerLayout"/>.</summary>
    public int? MixerTrackStride { get; init; }

    /// <summary>Complete verified mixer layout from the native profile. Null means raw mixer
    /// operations must refuse; a scalar stride or legacy version cannot establish this layout.</summary>
    public FlMixerLayout? MixerLayout { get; init; }

    /// <summary>Verified timeline marker layout and operation contract. Null means timeline
    /// operations must refuse rather than infer offsets from a version family.</summary>
    public FlTimelineLayout? TimelineLayout { get; init; }

    /// <summary>Whether the exact engine build has a verified automation-channel layout.
    /// Null or false prevents automation reads and edits; no major-version fallback is inferred.</summary>
    public bool? AutomationClips { get; init; }

    /// <summary>An empty, all-resolved status (nothing unresolved) — the common case sentinel.</summary>
    public static readonly FlSymbolStatus AllResolved =
        new(0, 0, 0, new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>Verified timeline marker layout from an exact native scanner profile.</summary>
/// <param name="MarkerManagerOffset">Marker-manager pointer offset in the active playlist object.</param>
/// <param name="MarkerStride">Size of one marker record in the manager's Delphi array.</param>
/// <param name="MarkerTickOffset">Tick integer offset in a marker record.</param>
/// <param name="MarkerNameOffset">Delphi UnicodeString pointer offset in a marker record.</param>
public sealed record FlTimelineLayout(int MarkerManagerOffset, int MarkerStride, int MarkerTickOffset, int MarkerNameOffset);

/// <summary>Verified byte layout for native mixer tracks and their send/effect structures.
/// Supplied as a complete unit by the native scanner profile; offsets must not be guessed individually.</summary>
/// <param name="TrackStride">Size of one mixer track structure.</param>
/// <param name="NameOffset">Track name pointer offset within a mixer track.</param>
/// <param name="TypeOffset">Track type integer offset within a mixer track.</param>
/// <param name="EnabledOffset">Track enabled-byte offset within a mixer track.</param>
/// <param name="SoloOffset">Track solo-byte offset within a mixer track.</param>
/// <param name="SendTableOffset">Send table offset within a mixer track.</param>
/// <param name="EffectSlotsOffset">Effect slot pointer-table offset within a mixer track.</param>
/// <param name="SendStride">Size of one destination's send record.</param>
/// <param name="SendLevelOffset">Level integer offset within a send record.</param>
/// <param name="SendActiveOffset">Active-byte offset within a send record.</param>
/// <param name="EffectSlotStride">Stride between pointers in the effect slot table.</param>
/// <param name="EffectIndexOffset">Plugin index integer offset within an effect object.</param>
/// <param name="EffectNameOffset">Plugin name pointer offset within an effect object.</param>
/// <param name="EffectLoadVtableOffset">Load-method pointer offset within the effect object's vtable.</param>
public sealed record FlMixerLayout(
    int TrackStride,
    int NameOffset,
    int TypeOffset,
    int EnabledOffset,
    int SoloOffset,
    int SendTableOffset,
    int EffectSlotsOffset,
    int SendStride,
    int SendLevelOffset,
    int SendActiveOffset,
    int EffectSlotStride,
    int EffectIndexOffset,
    int EffectNameOffset,
    int EffectLoadVtableOffset);

/// <summary>
/// Optional capability of an <see cref="INativeFlControl"/>: report which reverse-engineered FL
/// symbols resolved on the running FL Studio version. Only the real injected bridge implements this;
/// mocks/tests do not, so consumers MUST feature-detect (<c>fl is IFlSymbolResolution</c>) and fail
/// OPEN — advertise every tool — when it is absent or returns null.
/// </summary>
public interface IFlSymbolResolution
{
    /// <summary>
    /// Query symbol-resolution status, caching completed results only for the stable in-process
    /// transport. Pipe requests are re-queried because reconnection may reach another FL process.
    /// Returns <c>null</c> for unavailable, pending, or malformed diagnostics. Legacy responses without
    /// completion metadata are authoritative only when at least one symbol resolved. An explicitly
    /// completed scan is authoritative even when every symbol failed or the engine is unsupported;
    /// callers must honor that result instead of treating it as unknown. Caller cancellation propagates.
    /// </summary>
    Task<FlSymbolStatus?> GetSymbolStatusAsync(CancellationToken ct = default);
}
