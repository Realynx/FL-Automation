using System.ComponentModel;
using FruityLink.Core.Abstractions;
using ModelContextProtocol.Server;

namespace FruityLink.Mcp.Tools;

/// <summary>Playlist tracks: list, rename, color, mute, collapse, select.</summary>
[McpServerToolType]
public sealed class PlaylistTrackTools(INativeFlControl fl)
{
    [McpServerTool(Name = "native_list_playlist_tracks", ReadOnly = true)]
    [Description("List playlist tracks (1-50): name, color, mute, collapse, selection, type (normal/audio/instrument).")]
    public Task<string> ListPlaylistTracks(CancellationToken ct = default) =>
        McpSupport.SafeAsync(() => fl.ListPlaylistTracksAsync(ct));

    [McpServerTool(Name = "native_set_track_name")]
    [Description("Rename a playlist track (1-based).")]
    public Task<string> SetTrackName(
        [Description("Playlist track (1-based)")] int track,
        [Description("New name")] string name, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SetTrackNameAsync(track, name, ct); return $"Track {track} renamed to '{name}'."; });

    [McpServerTool(Name = "native_set_track_color")]
    [Description("Set a playlist track color. RGB hex like '#FF8800' or 'FF8800'.")]
    public Task<string> SetTrackColor(
        [Description("Playlist track (1-based)")] int track,
        [Description("RGB hex, e.g. #FF8800")] string rgbHex, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () =>
        {
            int rgb = Convert.ToInt32(rgbHex.TrimStart('#'), 16);
            await fl.SetTrackColorAsync(track, rgb, ct);
            return $"Track {track} color set to #{rgb:X6}.";
        });

    [McpServerTool(Name = "native_set_track_mute")]
    [Description("Mute/unmute a playlist track.")]
    public Task<string> SetTrackMute(
        [Description("Playlist track (1-based)")] int track,
        [Description("true = mute, false = unmute")] bool muted, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SetTrackMuteAsync(track, muted, ct); return $"Track {track} {(muted ? "muted" : "unmuted")}."; });

    [McpServerTool(Name = "native_set_track_collapsed")]
    [Description("Collapse/expand a playlist track's height.")]
    public Task<string> SetTrackCollapsed(
        [Description("Playlist track (1-based)")] int track,
        [Description("true = collapse, false = expand")] bool collapsed, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SetTrackCollapsedAsync(track, collapsed, ct); return $"Track {track} {(collapsed ? "collapsed" : "expanded")}."; });

    [McpServerTool(Name = "native_select_track")]
    [Description("Exclusively select a playlist track (1-based).")]
    public Task<string> SelectTrack([Description("Playlist track (1-based)")] int track, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SelectTrackAsync(track, ct); return $"Selected track {track}."; });
}

/// <summary>Playlist clips (arrangement): place, move, resize, delete, mute, slice, duplicate.</summary>
[McpServerToolType]
public sealed class ClipTools(INativeFlControl fl)
{
    [McpServerTool(Name = "native_list_clips", ReadOnly = true)]
    [Description("List playlist clips (slot index, track, start, length, source pattern/channel, mute).")]
    public Task<string> ListClips(CancellationToken ct = default) =>
        McpSupport.SafeAsync(() => fl.ListClipsAsync(ct));

    [McpServerTool(Name = "native_add_pattern_clip")]
    [Description("Place a pattern clip on the playlist to arrange a song. pattern 1-based; track = playlist track; startTick/lengthTick in PPQ ticks (lengthTick 0 = pattern's own length). Call native_get_ppq for the tick scale.")]
    public Task<string> AddPatternClip(
        [Description("Pattern (1-based)")] int pattern,
        [Description("Playlist track")] int track,
        [Description("Start in PPQ ticks")] int startTick,
        [Description("Length in PPQ ticks (0 = pattern length)")] int lengthTick = 0, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.AddPatternClipAsync(pattern, track, startTick, lengthTick, ct); return $"Placed pattern {pattern} on track {track} at tick {startTick}."; });

    [McpServerTool(Name = "native_move_clip")]
    [Description("Move a playlist clip (slot index from native_list_clips) to a new start tick + optionally a new track (-1 = keep current track).")]
    public Task<string> MoveClip(
        [Description("Clip slot index")] int clipIndex,
        [Description("New start in PPQ ticks")] int startTick,
        [Description("New track, or -1 = keep")] int track = -1, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.MoveClipAsync(clipIndex, startTick, track, ct); return $"Moved clip {clipIndex} to tick {startTick}{(track >= 0 ? $" track {track}" : "")}."; });

    [McpServerTool(Name = "native_resize_clip")]
    [Description("Resize a playlist clip (slot index) to a new length in PPQ ticks.")]
    public Task<string> ResizeClip(
        [Description("Clip slot index")] int clipIndex,
        [Description("New length in PPQ ticks")] int lengthTick, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.ResizeClipAsync(clipIndex, lengthTick, ct); return $"Resized clip {clipIndex} to {lengthTick} ticks."; });

    [McpServerTool(Name = "native_delete_clip")]
    [Description("Delete a playlist clip by slot index (best-effort: marks the slot inactive).")]
    public Task<string> DeleteClip([Description("Clip slot index")] int clipIndex, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.DeleteClipAsync(clipIndex, ct); return $"Deleted clip {clipIndex}."; });

    [McpServerTool(Name = "native_mute_clip")]
    [Description("Mute/unmute a playlist clip by slot index (from native_list_clips).")]
    public Task<string> MuteClip(
        [Description("Clip slot index")] int clipIndex,
        [Description("true = mute, false = unmute")] bool muted, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SetClipMutedAsync(clipIndex, muted, ct); return $"Clip {clipIndex} {(muted ? "muted" : "unmuted")}."; });

    [McpServerTool(Name = "native_slice_clip")]
    [Description("Slice/chop a playlist clip into two at an absolute tick (the split must be strictly inside the clip). Works for pattern + audio clips; audio playback stays continuous.")]
    public Task<string> SliceClip(
        [Description("Clip slot index")] int clipIndex,
        [Description("Absolute tick to cut (inside the clip)")] int tick, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SliceClipAsync(clipIndex, tick, ct); return $"Sliced clip {clipIndex} at tick {tick}."; });

    [McpServerTool(Name = "native_duplicate_clip")]
    [Description("Duplicate a playlist clip; the copy goes right after it on the same track.")]
    public Task<string> DuplicateClip([Description("Clip slot index")] int clipIndex, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.DuplicateClipAsync(clipIndex, ct); return $"Duplicated clip {clipIndex}."; });
}
