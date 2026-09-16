namespace FruityLink.Core.Hosting;

/// <summary>Controls whether an owned FL Studio process uses the caller's interactive desktop or an isolated desktop.</summary>
public enum FlStudioLaunchMode
{
    /// <summary>Launch on the caller's current interactive desktop.</summary>
    Interactive,
    /// <summary>Launch on a private, noninteractive desktop without switching the user's display.</summary>
    PrivateDesktop
}

/// <summary>Options for starting an SDK-owned FL Studio process.</summary>
public sealed class FlStudioLaunchOptions
{
    /// <summary>Creates launch options for an absolute FL Studio executable path.</summary>
    public FlStudioLaunchOptions(string executablePath) => ExecutablePath = executablePath;

    /// <summary>Absolute path to the executable.</summary>
    public string ExecutablePath { get; }
    /// <summary>Arguments passed without shell parsing.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];
    /// <summary>Process working directory. Defaults to the executable directory.</summary>
    public string? WorkingDirectory { get; init; }
    /// <summary>Environment overrides. A null value removes an inherited variable.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } =
        new Dictionary<string, string?>();
    /// <summary>Desktop isolation mode. Background private-desktop execution is the default.</summary>
    public FlStudioLaunchMode Mode { get; init; } = FlStudioLaunchMode.PrivateDesktop;
    /// <summary>Serializes private-desktop cold startup with other launches of the same executable.</summary>
    public bool SerializeStartup { get; init; } = true;
    /// <summary>Maximum time to wait for another cold startup to report native readiness.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Maximum time to wait after terminating the owned job.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>A top-level window owned by the launched process.</summary>
public sealed record FlStudioWindowSnapshot(
    nint Window,
    int ProcessId,
    string ClassName,
    string Title,
    bool Visible,
    bool Enabled,
    nint Owner,
    bool OwnerEnabled);
