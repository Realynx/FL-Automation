using System.ComponentModel;
using FruityLink.Core.Abstractions;
using ModelContextProtocol.Server;

namespace FruityLink.Mcp.Tools;

/// <summary>Bridge status.</summary>
[McpServerToolType]
public sealed class StatusTools(INativeFlControl fl)
{
    [McpServerTool(Name = "native_is_available", ReadOnly = true)]
    [Description("Check if the native FL Studio bridge is loaded in FL and responding. Call this first if other tools report the bridge is unreachable.")]
    public Task<string> IsAvailable(CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => await fl.IsAvailableAsync(ct) ? "Native bridge is available." : "Native bridge is NOT injected into FL Studio.");

    [McpServerTool(Name = "native_get_status", ReadOnly = true)]
    [Description("Read FL Studio's current status/hint bar text — the name/tooltip of whatever is under the mouse, current-operation messages, or load progress like 'Opening: <plugin>'. Empty when there is no active hint.")]
    public Task<string> GetStatus(CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { string s = await fl.GetStatusAsync(ct); return string.IsNullOrEmpty(s) ? "(no status/hint)" : s; });
}

/// <summary>Global / master controls (tempo, master volume/pitch, shuffle).</summary>
[McpServerToolType]
public sealed class GlobalTools(INativeFlControl fl)
{
    [McpServerTool(Name = "native_set_tempo")]
    [Description("Set project tempo in BPM (10-522).")]
    public Task<string> SetTempo([Description("BPM, 10-522")] double bpm, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SetTempoAsync(bpm, ct); return $"Tempo set to {bpm:0.##} BPM."; });

    [McpServerTool(Name = "native_get_tempo", ReadOnly = true)]
    [Description("Read the current project tempo (BPM).")]
    public Task<string> GetTempo(CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => $"{await fl.GetTempoAsync(ct):0.##} BPM");

    [McpServerTool(Name = "native_set_master_volume")]
    [Description("Set master volume, 0-12800 (~7624 = unity/0 dB).")]
    public Task<string> SetMasterVolume([Description("0-12800 (~7624 = 0 dB)")] int value, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SetMasterVolumeAsync(value, ct); return $"Master volume set to {value}."; });

    [McpServerTool(Name = "native_set_master_pitch")]
    [Description("Set master pitch in cents, -1200..1200.")]
    public Task<string> SetMasterPitch([Description("cents, -1200..1200")] int cents, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SetMasterPitchAsync(cents, ct); return $"Master pitch set to {cents} cents."; });

    [McpServerTool(Name = "native_set_shuffle")]
    [Description("Set global shuffle/swing amount, 0-128.")]
    public Task<string> SetShuffle([Description("0-128")] int value, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SetShuffleAsync(value, ct); return $"Shuffle set to {value}."; });
}

/// <summary>Transport + song/playback state, seek, and timeline markers.</summary>
[McpServerToolType]
public sealed class TransportTools(INativeFlControl fl)
{
    [McpServerTool(Name = "native_transport_play")]
    [Description("Start playback.")]
    public Task<string> Play(CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.TransportPlayAsync(ct); return "Playback started."; });

    [McpServerTool(Name = "native_transport_stop")]
    [Description("Stop playback.")]
    public Task<string> Stop(CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.TransportStopAsync(ct); return "Playback stopped."; });

    [McpServerTool(Name = "native_transport_toggle_record")]
    [Description("Toggle record-arm on/off.")]
    public Task<string> ToggleRecord(CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.TransportToggleRecordAsync(ct); return "Toggled record."; });

    [McpServerTool(Name = "native_get_song_state", ReadOnly = true)]
    [Description("Read playback context: playhead (bar/beat/tick), song-vs-pattern mode, play state, loop region, song length, and PPQ.")]
    public Task<string> GetSongState(CancellationToken ct = default) =>
        McpSupport.SafeAsync(() => fl.GetSongStateAsync(ct));

    [McpServerTool(Name = "native_set_song_mode")]
    [Description("Switch between song mode (full arrangement) and pattern mode. song=true -> song mode.")]
    public Task<string> SetSongMode([Description("true = song mode, false = pattern mode")] bool song, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SetSongModeAsync(song, ct); return $"Switched to {(song ? "song" : "pattern")} mode."; });

    [McpServerTool(Name = "native_seek")]
    [Description("Move the song playhead to an absolute tick (PPQ; call native_get_ppq for ticks/quarter). Clamps to song length.")]
    public Task<string> Seek([Description("Absolute tick (PPQ)")] int tick, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SeekAsync(tick, ct); return $"Playhead moved to tick {tick}."; });

    [McpServerTool(Name = "native_list_markers", ReadOnly = true)]
    [Description("List timeline markers (name + tick).")]
    public Task<string> ListMarkers(CancellationToken ct = default) =>
        McpSupport.SafeAsync(() => fl.ListMarkersAsync(ct));

    [McpServerTool(Name = "native_add_marker")]
    [Description("Add a timeline marker at a tick with a name (e.g. 'Verse','Chorus','Drop'). Tick is in PPQ (native_get_ppq).")]
    public Task<string> AddMarker(
        [Description("Absolute tick (PPQ)")] int tick,
        [Description("Marker name")] string name, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.AddMarkerAsync(tick, name, ct); return $"Added marker '{name}' at tick {tick}."; });
}
