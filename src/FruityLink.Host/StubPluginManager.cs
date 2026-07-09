using System.Collections.Concurrent;
using FruityLink.Plugins.Abstractions;

namespace FruityLink.Host;

/// <summary>
/// TEST-ONLY in-memory <see cref="IPluginManager"/>. Installed only when the env var
/// <c>FRUITYLINK_PLUGIN_STUB=1</c> is set and no real manager has been registered, so the native
/// "Plugins" dropdown can be exercised end-to-end (list + toggle round-trip) before the real plugin
/// host (a sibling component) wires up <see cref="PluginManagerLocator.Current"/>. Never set in a
/// production path.
/// </summary>
internal sealed class StubPluginManager : IPluginManager
{
    private readonly ConcurrentDictionary<string, bool> _enabled = new();
    private readonly List<PluginInfo> _seed = new()
    {
        new PluginInfo("synthwave",  "Synthwave Pack",   "Retro analog presets",       "1.2.0", true,  true),
        new PluginInfo("autochord",  "AutoChord",        "Chord progression helper",   "0.9.1", false, false),
        new PluginInfo("loudness",   "Loudness Meter",   "LUFS / true-peak metering",  "2.0.0", true,  true),
        new PluginInfo("miditools",  "MIDI Tools",       "Humanize / quantize / scale","1.5.3", false, false),
    };

    public StubPluginManager()
    {
        foreach (PluginInfo p in _seed) _enabled[p.Id] = p.Enabled;
    }

    public IReadOnlyList<PluginInfo> List()
        => _seed.Select(p => p with { Enabled = _enabled.GetValueOrDefault(p.Id), Loaded = _enabled.GetValueOrDefault(p.Id) }).ToList();

    public Task<bool> EnableAsync(string id, CancellationToken ct = default)
    {
        if (!_enabled.ContainsKey(id)) return Task.FromResult(false);
        _enabled[id] = true;
        HostEntry.Log($"[stub] enabled '{id}'");
        return Task.FromResult(true);
    }

    public Task<bool> DisableAsync(string id, CancellationToken ct = default)
    {
        if (!_enabled.ContainsKey(id)) return Task.FromResult(false);
        _enabled[id] = false;
        HostEntry.Log($"[stub] disabled '{id}'");
        return Task.FromResult(true);
    }

    public bool IsEnabled(string id) => _enabled.GetValueOrDefault(id);
}
