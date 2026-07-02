using System.ComponentModel;
using FruityLink.Core.Abstractions;
using ModelContextProtocol.Server;

namespace FruityLink.Mcp.Tools;

/// <summary>Project lifecycle: save variants, open, new, info, recent.</summary>
[McpServerToolType]
public sealed class ProjectTools(INativeFlControl fl)
{
    [McpServerTool(Name = "native_save_project")]
    [Description("Save the current FL project. Pass an absolute .flp path to save-as, or empty to save to the current file (fails if never saved). Affects the user's project — confirm first.")]
    public Task<string> SaveProject(
        [Description("Absolute .flp path, or empty = current file")] string path = "", CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SaveProjectAsync(path, ct); return string.IsNullOrWhiteSpace(path) ? "Saved project." : $"Saved project to {path}."; });

    [McpServerTool(Name = "native_open_project", Destructive = true)]
    [Description("Open an FL project (.flp) by absolute path, REPLACING the current one (unsaved changes are lost). Confirm with the user first.")]
    public Task<string> OpenProject([Description("Absolute .flp path")] string path, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.OpenProjectAsync(path, ct); return $"Opened {path}."; });

    [McpServerTool(Name = "native_new_project", Destructive = true)]
    [Description("Start a new empty FL project, REPLACING the current one (unsaved changes are lost). Confirm with the user first.")]
    public Task<string> NewProject(CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.NewProjectAsync(ct); return "Started a new project."; });

    [McpServerTool(Name = "native_get_project_info", ReadOnly = true)]
    [Description("Report the current project's title, file path, and saved-or-untitled state.")]
    public Task<string> GetProjectInfo(CancellationToken ct = default) =>
        McpSupport.SafeAsync(() => fl.GetProjectInfoAsync(ct));

    [McpServerTool(Name = "native_save_project_as")]
    [Description("Save As: save to a new absolute .flp path AND make it the current project (updates title + recent files). Affects the user's project — confirm first.")]
    public Task<string> SaveProjectAs([Description("Absolute .flp path")] string path, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SaveProjectAsAsync(path, ct); return $"Saved as {path} (now the current project)."; });

    [McpServerTool(Name = "native_save_copy")]
    [Description("Save a COPY to an absolute .flp path WITHOUT changing the current project/title (backup/export). Safe — doesn't affect the open project's save state.")]
    public Task<string> SaveCopy([Description("Absolute .flp path for the copy")] string path, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SaveCopyAsync(path, ct); return $"Saved a copy to {path}."; });

    [McpServerTool(Name = "native_save_new_version")]
    [Description("Save an auto-incremented new version (e.g. song_2.flp, song_3.flp) and make it current. Requires the project saved at least once.")]
    public Task<string> SaveNewVersion(CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SaveNewVersionAsync(ct); return "Saved a new version."; });

    [McpServerTool(Name = "native_list_recent_projects", ReadOnly = true)]
    [Description("List recently-opened FL projects (most recent first).")]
    public Task<string> ListRecentProjects(CancellationToken ct = default) =>
        McpSupport.SafeAsync(() => fl.ListRecentProjectsAsync(ct));
}

/// <summary>Arrangements: list, create, clone, rename, delete, select.</summary>
[McpServerToolType]
public sealed class ArrangementTools(INativeFlControl fl)
{
    [McpServerTool(Name = "native_list_arrangements", ReadOnly = true)]
    [Description("List arrangements (the current one marked with *), with indices.")]
    public Task<string> ListArrangements(CancellationToken ct = default) =>
        McpSupport.SafeAsync(() => fl.ListArrangementsAsync(ct));

    [McpServerTool(Name = "native_make_arrangement")]
    [Description("Create a new empty arrangement and switch to it. Optionally name it. Returns the new index.")]
    public Task<string> MakeArrangement(
        [Description("Optional name; empty = FL default")] string name = "", CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { int i = await fl.AddArrangementAsync(string.IsNullOrWhiteSpace(name) ? null : name, ct); return $"Created arrangement {i}{(string.IsNullOrWhiteSpace(name) ? "" : $" '{name}'")}."; });

    [McpServerTool(Name = "native_clone_arrangement")]
    [Description("Clone an arrangement — deep-copy tracks + playlist clips into a new arrangement and switch to it. srcIndex < 0 = current. Optionally name it. Returns the new index.")]
    public Task<string> CloneArrangement(
        [Description("Source index, or -1 = current")] int srcIndex = -1,
        [Description("Optional name for the clone")] string name = "", CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { int i = await fl.CloneArrangementAsync(srcIndex, string.IsNullOrWhiteSpace(name) ? null : name, ct); return $"Cloned to arrangement {i}{(string.IsNullOrWhiteSpace(name) ? "" : $" '{name}'")}."; });

    [McpServerTool(Name = "native_rename_arrangement")]
    [Description("Rename an arrangement by index.")]
    public Task<string> RenameArrangement(
        [Description("Arrangement index")] int index,
        [Description("New name")] string name, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.RenameArrangementAsync(index, name, ct); return $"Renamed arrangement {index} to '{name}'."; });

    [McpServerTool(Name = "native_delete_arrangement", Destructive = true)]
    [Description("Delete an arrangement by index.")]
    public Task<string> DeleteArrangement([Description("Arrangement index")] int index, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.DeleteArrangementAsync(index, ct); return $"Deleted arrangement {index}."; });

    [McpServerTool(Name = "native_select_arrangement")]
    [Description("Switch to (make current) the arrangement at index.")]
    public Task<string> SelectArrangement([Description("Arrangement index")] int index, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SelectArrangementAsync(index, ct); return $"Switched to arrangement {index}."; });
}

/// <summary>Automation clips: a channel must host the 'Automation Clip' generator (native_add_channel).</summary>
[McpServerToolType]
public sealed class AutomationTools(INativeFlControl fl)
{
    [McpServerTool(Name = "native_list_automation_points", ReadOnly = true)]
    [Description("List an automation-clip channel's points (time in beats, value 0-1, tension, curve). The channel must host the Automation Clip generator (create via native_add_channel('Automation Clip')).")]
    public Task<string> ListAutomationPoints([Description("Automation-clip channel index")] int channel, CancellationToken ct = default) =>
        McpSupport.SafeAsync(() => fl.ListAutomationPointsAsync(channel, ct));

    [McpServerTool(Name = "native_add_automation_point")]
    [Description("Add a point to an automation-clip channel: time in BEATS (4 beats = 1 bar in 4/4), value 0.0-1.0, tension -1.0..1.0 (0 = linear). Inserts in time order and rebuilds the curve.")]
    public Task<string> AddAutomationPoint(
        [Description("Automation-clip channel index")] int channel,
        [Description("Time in beats (4 beats = 1 bar)")] double timeBeats,
        [Description("Value 0.0-1.0")] double value,
        [Description("Tension -1.0..1.0 (0 = linear)")] double tension = 0, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.AddAutomationPointAsync(channel, timeBeats, value, tension, ct); return $"Added automation point at beat {timeBeats:0.###} = {value:0.###}."; });

    [McpServerTool(Name = "native_delete_automation_point")]
    [Description("Delete an automation point by index from an automation-clip channel.")]
    public Task<string> DeleteAutomationPoint(
        [Description("Automation-clip channel index")] int channel,
        [Description("Point index")] int index, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.DeleteAutomationPointAsync(channel, index, ct); return $"Deleted automation point {index}."; });
}

/// <summary>Render / export.</summary>
[McpServerToolType]
public sealed class RenderTools(INativeFlControl fl)
{
    [McpServerTool(Name = "native_render")]
    [Description("Open FL's audio Export dialog to render to WAV/MP3. The user picks the format/path and clicks Render to finish.")]
    public Task<string> Render(CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.OpenExportDialogAsync(0, ct); return "Opened FL's export dialog — choose a format/path and click Render."; });
}

/// <summary>In-FL "FruityLink AI" chat tab (native browser tab).</summary>
[McpServerToolType]
public sealed class ChatTools(INativeFlControl fl)
{
    [McpServerTool(Name = "native_open_chat_tab")]
    [Description("Open (or focus) the native 'FruityLink AI' chat tab in FL's browser.")]
    public Task<string> OpenChatTab(CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.OpenChatTabAsync(ct); return "Opened the FruityLink AI chat tab."; });

    [McpServerTool(Name = "native_close_chat_tab")]
    [Description("Hide the chat tab and restore the browser's content hook.")]
    public Task<string> CloseChatTab(CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.CloseChatTabAsync(ct); return "Closed the FruityLink AI chat tab."; });

    [McpServerTool(Name = "native_chat_poll", ReadOnly = true)]
    [Description("Return and clear the user's submitted chat message from the in-FL chat tab (empty if none pending).")]
    public Task<string> ChatPoll(CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { string s = await fl.ChatPollAsync(ct); return string.IsNullOrEmpty(s) ? "(no message pending)" : s; });

    [McpServerTool(Name = "native_chat_say")]
    [Description("Append a line of text to the in-FL chat display (runs on FL's main thread).")]
    public Task<string> ChatSay([Description("Text to display")] string text, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.ChatSayAsync(text, ct); return "Appended to the chat display."; });
}
