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
/// <c>FruityLink.Core</c> types it exposes), plus the process-wide <c>FruityLink.Scripting</c>
/// runtime, are deliberately SHARED with the host's load
/// context: we return the host's exact assembly instances for them.
/// That gives <c>IFlPlugin</c>, <c>IPluginContext</c>, <c>INativeFlControl</c>, etc. a single type
/// identity across the boundary — otherwise an <c>(IFlPlugin)</c> cast on the instance we create in
/// the plugin context would fail. Scripting and the Avalonia toolkit also survive plugin reloads:
/// their interpreter, application, dispatcher, and native callbacks have one stable owner. Product
/// assemblies remain private and resolve their dependencies through this shared toolkit boundary.
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
            // CPython and native callbacks outlive collectible plugin instances. Sharing their
            // owner avoids initializing a second interpreter when a plugin is enabled or reloaded.
            ["FruityLink.Scripting"] = typeof(global::FruityLink.Scripting.FlScriptingDispatcher).Assembly,
            ["FruityLink.Ui.Avalonia.Hosting"] = typeof(global::FruityLink.Ui.Avalonia.Hosting.EmbeddedAvaloniaHost).Assembly,
        };

    private static readonly AssemblyLoadContext UiContext = GetLoadContext(
        typeof(global::FruityLink.Ui.Avalonia.Hosting.EmbeddedAvaloniaHost).Assembly)!;

    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _pluginDirectory;

    public PluginLoadContext(string mainPluginDll)
        : base(name: "FruityLinkPlugin:" + Path.GetFileNameWithoutExtension(mainPluginDll), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainPluginDll);
        _pluginDirectory = Path.GetDirectoryName(mainPluginDll)!;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Share the plugin contract: hand back the host's exact Assembly instance so contract types
        // unify (any private copy in the plugin folder is ignored). Everything else loads privately
        // from the plugin folder via the deps.json-driven resolver.
        if (assemblyName.Name is { } name && Shared.TryGetValue(name, out Assembly? shared))
            return shared;

        if (IsSharedUiAssembly(assemblyName.Name))
            return LoadSharedUiAssembly(assemblyName);

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is null && assemblyName.Name is { } simpleName)
        {
            string sibling = Path.Combine(_pluginDirectory, simpleName + ".dll");
            if (File.Exists(sibling)) path = sibling;
        }
        return path is not null ? LoadFromAssemblyPath(path) : null; // null => default-context fallback
    }

    private static bool IsSharedUiAssembly(string? name) =>
        name is "Avalonia" or "AvaloniaEdit" or "SkiaSharp" or "HarfBuzzSharp" or "MicroCom.Runtime"
        || name?.StartsWith("Avalonia.", StringComparison.OrdinalIgnoreCase) == true;

    internal static bool IsSharedAssemblyName(string name) => Shared.ContainsKey(name) || IsSharedUiAssembly(name);

    private static Assembly LoadSharedUiAssembly(AssemblyName requested)
    {
        // Resolve from the stable host component's deps.json, never a per-plugin shadow directory.
        // Returning null here would silently create a second toolkit/application in the plugin ALC.
        Assembly assembly = UiContext.LoadFromAssemblyName(requested);
        Version? available = assembly.GetName().Version;
        if (requested.Version is { } required && available is not null
            && (required.Major != available.Major || required > available))
            throw new FileLoadException($"Plugin requires {requested}, but the shared UI runtime is {assembly.FullName}. "
                + "Build UI plugins against the host's Avalonia version and restart FL after updating it.");
        return assembly;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (path is null)
        {
            string name = Path.GetFileName(unmanagedDllName);
            string sibling = Path.Combine(_pluginDirectory, Path.HasExtension(name) ? name : name + ".dll");
            if (File.Exists(sibling)) path = sibling;
        }
        return path is not null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
