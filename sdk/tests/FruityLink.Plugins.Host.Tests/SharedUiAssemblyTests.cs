using System.Reflection;
using System.Runtime.Loader;
using FruityLink.Ui.Avalonia.Hosting;
using Xunit;

namespace FruityLink.Plugins.Host.Tests;

public sealed class SharedUiAssemblyTests
{
    [Theory]
    [InlineData("FruityLink.Ui.Avalonia.Hosting")]
    [InlineData("Avalonia.Controls")]
    [InlineData("Avalonia.Base")]
    [InlineData("Avalonia.Skia")]
    [InlineData("Avalonia.Themes.Fluent")]
    [InlineData("Avalonia.Fonts.Inter")]
    [InlineData("AvaloniaEdit")]
    [InlineData("SkiaSharp")]
    [InlineData("HarfBuzzSharp")]
    [InlineData("MicroCom.Runtime")]
    public async Task PluginCopiesCannotCreateAnotherUiRuntime(string assemblyName)
    {
        await using var scope = new PluginTestScope();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(scope.PluginDll)!, assemblyName + ".dll"), "invalid private copy");
        var context = new PluginLoadContext(scope.PluginDll);
        try
        {
            Assembly expected = AssemblyLoadContext.GetLoadContext(typeof(EmbeddedAvaloniaHost).Assembly)!
                .LoadFromAssemblyName(new AssemblyName(assemblyName));
            Assembly actual = context.LoadFromAssemblyName(expected.GetName());
            Assert.Same(expected, actual);
            Assert.False(AssemblyLoadContext.GetLoadContext(actual)!.IsCollectible);
        }
        finally { context.Unload(); }
    }

    [Fact]
    public async Task OlderCompatibleToolkitReferenceUsesHostRuntime()
    {
        await using var scope = new PluginTestScope();
        var context = new PluginLoadContext(scope.PluginDll);
        try
        {
            Assembly expected = typeof(Avalonia.Controls.Window).Assembly;
            AssemblyName request = expected.GetName();
            request.Version = new Version(11, 0, 0, 0); // AvaloniaEdit's supported minimum reference.
            Assert.Same(expected, context.LoadFromAssemblyName(request));
        }
        finally { context.Unload(); }
    }

    [Fact]
    public async Task IncompatibleToolkitVersionFailsWithoutPrivateFallback()
    {
        await using var scope = new PluginTestScope();
        var context = new PluginLoadContext(scope.PluginDll);
        try
        {
            AssemblyName request = typeof(Avalonia.Controls.Window).Assembly.GetName();
            request.Version = new Version(99, 0, 0, 0);
            Exception? error = Record.Exception(() => context.LoadFromAssemblyName(request));
            Assert.True(error is FileLoadException or FileNotFoundException, error?.ToString());
            Assert.DoesNotContain(context.Assemblies, assembly => assembly.GetName().Name == request.Name);
        }
        finally { context.Unload(); }
    }

    [Fact]
    public async Task IndependentPluginsAndDisableReenableReuseHostAndInterpreterOwners()
    {
        await using var first = new PluginTestScope();
        await using var second = new PluginTestScope();
        Assert.True(await first.Manager.EnableAsync(PluginTestScope.PluginId));
        Assert.True(await second.Manager.EnableAsync(PluginTestScope.PluginId));
        Assert.True(await first.Manager.DisableAsync(PluginTestScope.PluginId));
        Assert.True(await first.Manager.EnableAsync(PluginTestScope.PluginId));
        Assert.True(await second.Manager.ReloadAsync(PluginTestScope.PluginId));
        Assert.Equal(2, first.UiHosts.Count);
        Assert.Equal(2, second.UiHosts.Count);
        Assert.All(first.UiHosts.Concat(second.UiHosts), host => Assert.Same(EmbeddedAvaloniaHost.Instance, host));
        Assert.All(first.ScriptingTypes.Concat(second.ScriptingTypes),
            type => Assert.Same(typeof(FruityLink.Scripting.FlScriptingDispatcher), type));
    }
}
