using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using FruityLink.Core.Abstractions;
using FruityLink.Plugins.Abstractions;

namespace FruityLink.Plugins.Host.Tests;

internal sealed class PluginTestScope : IAsyncDisposable, IServiceProvider
{
    public const string PluginId = "lifecycle-fixture";
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "FruityLink.Host.Tests", Guid.NewGuid().ToString("N"));
    public string PluginsDir => Path.Combine(Root, "plugins");
    public string ShadowRoot => Path.Combine(Root, "shadow");
    public string PluginDll { get; }
    public PluginManager Manager { get; }
    public ConcurrentQueue<string> Events { get; } = new();
    public ConcurrentQueue<Type> ScriptingTypes { get; } = new();
    public ConcurrentQueue<object> UiHosts { get; } = new();
    public Action<string>? OnEvent { get; set; }
    public Action<IFlWindowHost>? OnWindows { get; set; }
    public Func<Task>? WaitForEnable { get; set; }

    public PluginTestScope(bool flat = false, bool enabled = false, bool includeDeps = true, IFlWindowHost? windows = null)
    {
        string packageDir = flat ? PluginsDir : Path.Combine(PluginsDir, "fixture");
        Directory.CreateDirectory(packageDir);
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures")))
        {
            if (!includeDeps && file.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(packageDir, Path.GetFileName(file)));
        }
        PluginDll = Path.Combine(packageDir, "LifecycleFixture.dll");
        string stateFile = Path.Combine(Root, "plugins.json");
        if (enabled) File.WriteAllText(stateFile, JsonSerializer.Serialize(new { Enabled = new[] { PluginId } }));
        Manager = new PluginManager(DispatchProxy.Create<INativeFlControl, NeverCalledFlControl>(), this,
            PluginsDir, stateFile, Path.Combine(Root, "host.log"), ShadowRoot, windows);
        Manager.Discover();
    }

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(Func<Task>)) return WaitForEnable;
        if (serviceType == typeof(Action<Type>)) return new Action<Type>(ScriptingTypes.Enqueue);
        if (serviceType == typeof(Action<object>)) return new Action<object>(UiHosts.Enqueue);
        if (serviceType == typeof(Action<IFlWindowHost>)) return OnWindows;
        if (serviceType != typeof(Action<string>)) return null;
        return new Action<string>(message =>
        {
            Events.Enqueue(message);
            OnEvent?.Invoke(message);
        });
    }

    public async ValueTask DisposeAsync()
    {
        Manager.DisableHotReload();
        OnEvent = null;
        foreach (var plugin in Manager.List()) await Manager.DisableAsync(plugin.Id);
        Manager.Dispose();
        for (int i = 0; i < 2; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* A collectible context can remain rooted by the test runner until later GC. */ }
        catch (UnauthorizedAccessException) { /* Windows can report a mapped assembly as access denied. */ }
    }
}

public class NeverCalledFlControl : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => throw new InvalidOperationException("Lifecycle tests must not touch FL Studio.");
}
