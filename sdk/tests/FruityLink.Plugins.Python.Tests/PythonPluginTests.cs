using FruityLink.Core.Abstractions;
using FruityLink.Plugins.Abstractions;
using FruityLink.Scripting;
using FruityLink.Scripting.Tests;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace FruityLink.Plugins.Python.Tests;

public sealed class PythonPluginTests
{
    [Fact]
    public async Task EnableDisableAndRetryAreIdempotentWithoutUiServices()
    {
        string directory = Path.Combine(Path.GetTempPath(), "FruityLink-PythonPluginTests-" + Guid.NewGuid().ToString("N"));
        var (control, _) = RecordingControl.Create<INativeFlControl>();
        var context = DispatchProxy.Create<IPluginContext, ContextProxy>();
        var recorder = (ContextProxy)context;
        recorder.Control = control;
        recorder.Options = new() { DiscoveryDirectory = directory, PipeName = "FruityLinkPythonPlugin-test-" + Guid.NewGuid().ToString("N") };
        var plugin = new PythonControlPlugin();
        string path = Path.Combine(directory, $"{Environment.ProcessId}.json");
        try
        {
            await plugin.DisableAsync();
            await Task.WhenAll(plugin.EnableAsync(context), plugin.EnableAsync(context));
            var first = JsonSerializer.Deserialize<ScriptingEndpoint>(await File.ReadAllTextAsync(path), ScriptingJson.Options)!;
            Assert.Single(recorder.Messages);
            Assert.DoesNotContain(first.Token, recorder.Messages[0], StringComparison.Ordinal);
            await Task.WhenAll(plugin.DisableAsync(), plugin.DisableAsync());
            Assert.False(File.Exists(path));
            await plugin.EnableAsync(context);
            var second = JsonSerializer.Deserialize<ScriptingEndpoint>(await File.ReadAllTextAsync(path), ScriptingJson.Options)!;
            Assert.NotEqual(first.InstanceId, second.InstanceId);
            Assert.NotEqual(first.Token, second.Token);
        }
        finally
        {
            await plugin.DisableAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

public class ContextProxy : DispatchProxy, IServiceProvider
{
    public INativeFlControl Control { get; set; } = null!;
    public ScriptingServerOptions Options { get; set; } = null!;
    public List<string> Messages { get; } = new();
    public object? GetService(Type serviceType) => serviceType == typeof(ScriptingServerOptions) ? Options : null;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == "get_Fl") return Control;
        if (targetMethod.Name == "get_Services") return this;
        if (targetMethod.Name == "Log") { Messages.Add((string)args![0]!); return null; }
        throw new InvalidOperationException($"No-UI plugin unexpectedly accessed {targetMethod.Name}.");
    }
}
