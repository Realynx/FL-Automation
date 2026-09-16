namespace FruityLink.Scripting;

/// <summary>Host-owned configuration for an authenticated local scripting endpoint.</summary>
public sealed class ScriptingServerOptions
{
    /// <summary>Named-pipe name; the default is FruityLinkScripting followed by the current process ID.</summary>
    public string PipeName { get; init; } = $"FruityLinkScripting-{Environment.ProcessId}";
    /// <summary>Directory holding per-process discovery files. Its ACL is restricted to the current Windows user.</summary>
    public string DiscoveryDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FruityLink", "scripting");
    /// <summary>Whether to publish per-user discovery metadata.</summary>
    public bool PublishDiscovery { get; init; } = true;
    /// <summary>Maximum time to receive or handle one request. Started native work is drained after this deadline.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Maximum number of connected clients, including clients waiting for serialized FL access.</summary>
    public int MaximumConnections { get; init; } = 16;
    /// <summary>Optional diagnostic sink. Tokens and request contents are never logged.</summary>
    public Action<string>? Log { get; init; }
}
