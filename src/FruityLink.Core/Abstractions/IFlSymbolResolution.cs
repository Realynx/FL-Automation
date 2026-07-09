namespace FruityLink.Core.Abstractions;

/// <summary>
/// Result of the native bridge's <c>syms</c> diagnostic (see <c>tools/bridge/sigscan.*</c>): the
/// FL version the bridge detected at runtime plus the set of reverse-engineered FL symbols it could
/// NOT locate on THIS FL build. Multi-version portability: one bridge binary runs on any FL version,
/// resolving addresses by byte-signature scan; a symbol whose signature didn't match this version is
/// reported here so a tool depending on it can be hidden instead of firing a wrong address (an
/// uncatchable access violation inside FL).
/// </summary>
/// <param name="Version">FL version index the bridge detected (0 = unknown; 1 = 25.2.5; 2 = 26.1.0).</param>
/// <param name="Resolved">Count of symbols that resolved OK.</param>
/// <param name="Failed">Count of symbols that did not resolve.</param>
/// <param name="Unresolved">Names of the symbols that did not resolve (ordinal-compared).</param>
public sealed record FlSymbolStatus(
    int Version,
    int Resolved,
    int Failed,
    IReadOnlySet<string> Unresolved)
{
    /// <summary>An empty, all-resolved status (nothing unresolved) — the common case sentinel.</summary>
    public static readonly FlSymbolStatus AllResolved =
        new(0, 0, 0, new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>
/// Optional capability of an <see cref="INativeFlControl"/>: report which reverse-engineered FL
/// symbols resolved on the running FL Studio version. Only the real injected bridge implements this;
/// mocks/tests do not, so consumers MUST feature-detect (<c>fl is IFlSymbolResolution</c>) and fail
/// OPEN — advertise every tool — when it is absent or returns null.
/// </summary>
public interface IFlSymbolResolution
{
    /// <summary>
    /// Query the bridge's symbol-resolution status ONCE and cache it (resolution is done once by the
    /// native side after FL loads and never changes for the process). Returns <c>null</c> when the
    /// status cannot be determined — bridge not injected, FL not ready yet, a malformed response, or
    /// nothing resolved at all. Callers must treat <c>null</c> as "unknown → gate nothing".
    /// </summary>
    Task<FlSymbolStatus?> GetSymbolStatusAsync(CancellationToken ct = default);
}
