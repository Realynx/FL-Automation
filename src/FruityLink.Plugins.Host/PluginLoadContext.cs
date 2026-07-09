using System.Reflection;
using System.Runtime.Loader;

namespace FruityLink.Plugins.Host;

/// <summary>
/// A collectible <see cref="AssemblyLoadContext"/> for ONE plugin package (its main .dll plus the
/// private dependencies sitting next to it). Each plugin gets its own context so the host can attempt
/// to unload it when the user disables the plugin (best-effort — CoreCLR unloads lazily; see
/// <see cref="PluginManager.DisableAsync"/>).
///
/// The FruityLink contract assemblies (<c>FruityLink.Plugins.Abstractions</c> and the
/// <c>FruityLink.Core</c> types it exposes) are deliberately SHARED with the host's default load
/// context: we return <c>null</c> for them so the runtime resolves them from the default context.
/// That gives <c>IFlPlugin</c>, <c>IPluginContext</c>, <c>INativeFlControl</c>, etc. a single type
/// identity across the boundary — otherwise an <c>(IFlPlugin)</c> cast on the instance we create in
/// the plugin context would fail (the classic "unifying the contract" ALC gotcha). Everything else is
/// loaded privately from the plugin folder via the deps.json-driven dependency resolver.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    // Contract assemblies that MUST unify with the host, mapped to the EXACT Assembly instances the
    // host code is compiled against (captured via typeof). Returning these from Load() guarantees
    // IFlPlugin / IPluginContext / INativeFlControl share one type identity across the boundary,
    // regardless of which load context the host itself lives in. (Returning null to rely on the
    // implicit default-context fallback is NOT safe here: under the proxy/CLR-host install the host is
    // loaded as a hostfxr *component*, so its contract dlls are neither on the default ALC's probing
    // path nor necessarily in the Default ALC at all — that produced "Could not load FruityLink.Core /
    // .Plugins.Abstractions" and silent type-identity mismatches.)
    private static readonly System.Collections.Generic.Dictionary<string, Assembly> Shared =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["FruityLink.Plugins.Abstractions"] = typeof(global::FruityLink.Plugins.Abstractions.IFlPlugin).Assembly,
            ["FruityLink.Core"] = typeof(global::FruityLink.Core.Abstractions.INativeFlControl).Assembly,
        };

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string mainPluginDll)
        : base(name: "FruityLinkPlugin:" + Path.GetFileNameWithoutExtension(mainPluginDll), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainPluginDll);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Share the plugin contract: hand back the host's exact Assembly instance so contract types
        // unify (any private copy in the plugin folder is ignored). Everything else loads privately
        // from the plugin folder via the deps.json-driven resolver.
        if (assemblyName.Name is { } name && Shared.TryGetValue(name, out Assembly? shared))
            return shared;

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null; // null => default-context fallback
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
