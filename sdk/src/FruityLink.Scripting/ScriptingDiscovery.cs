using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace FruityLink.Scripting;

internal sealed class ScriptingDiscovery : IDisposable
{
    private readonly FileStream _lease;
    private readonly string _path;
    private readonly string _instanceId;

    internal ScriptingDiscovery(string directory, ScriptingEndpoint endpoint)
    {
        PrepareDirectory(directory);
        _path = Path.Combine(directory, $"{endpoint.Pid}.json");
        _instanceId = endpoint.InstanceId;
        _lease = new FileStream(Path.Combine(directory, $"{endpoint.Pid}.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    internal async Task PublishAsync(ScriptingEndpoint endpoint, CancellationToken ct)
    {
        string temporary = _path + "." + _instanceId + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(endpoint, ScriptingJson.Options), ct).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose()
    {
        try { RemoveOwnedMetadata(); }
        finally { _lease.Dispose(); }
    }

    private void RemoveOwnedMetadata()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var metadata = JsonSerializer.Deserialize<ScriptingEndpoint>(File.ReadAllText(_path), ScriptingJson.Options);
            if (metadata?.InstanceId == _instanceId) File.Delete(_path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
    }

    private static void PrepareDirectory(string directory)
    {
        var info = Directory.CreateDirectory(directory);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The scripting discovery directory cannot be a reparse point.");
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new IOException("The current Windows identity has no user SID.");
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}
