using System.ComponentModel;
using FruityLink.Core.Abstractions;
using ModelContextProtocol.Server;

namespace FruityLink.Mcp.Tools;

/// <summary>Patterns + the project timebase (PPQ).</summary>
[McpServerToolType]
public sealed class PatternTools(INativeFlControl fl)
{
    [McpServerTool(Name = "native_list_patterns", ReadOnly = true)]
    [Description("List patterns that have content as 'index: name', marking the current one.")]
    public Task<string> ListPatterns(CancellationToken ct = default) =>
        McpSupport.SafeAsync(() => fl.ListPatternsAsync(ct));

    [McpServerTool(Name = "native_get_current_pattern", ReadOnly = true)]
    [Description("Get the current (selected) pattern number (1-based).")]
    public Task<string> GetCurrentPattern(CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => $"Current pattern: {await fl.GetCurrentPatternAsync(ct)}");

    [McpServerTool(Name = "native_get_pattern_name", ReadOnly = true)]
    [Description("Get a pattern's display name by number (1-based).")]
    public Task<string> GetPatternName([Description("Pattern number (1-based)")] int index, CancellationToken ct = default) =>
        McpSupport.SafeAsync(() => fl.GetPatternNameAsync(index, ct));

    [McpServerTool(Name = "native_select_pattern")]
    [Description("Select a pattern by number (1-based).")]
    public Task<string> SelectPattern([Description("Pattern number (1-based)")] int index, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SelectPatternAsync(index, ct); return $"Selected pattern {index}."; });

    [McpServerTool(Name = "native_create_pattern")]
    [Description("Create/select a new empty pattern (first free slot); returns its number.")]
    public Task<string> CreatePattern(CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => $"Created and selected pattern {await fl.CreatePatternAsync(ct)}.");

    [McpServerTool(Name = "native_clear_pattern")]
    [Description("Delete all notes in a pattern (1-based).")]
    public Task<string> ClearPattern([Description("Pattern number (1-based)")] int index, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.ClearPatternAsync(index, ct); return $"Cleared notes in pattern {index}."; });

    [McpServerTool(Name = "native_get_ppq", ReadOnly = true)]
    [Description("Get the project timebase: ticks per quarter note (PPQ), used for note positions/lengths and clip ticks (often 960).")]
    public Task<string> GetPpq(CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => $"{await fl.GetPpqAsync(ct)} ticks per quarter note");
}

/// <summary>Piano-roll note authoring and reading.</summary>
[McpServerToolType]
public sealed class NoteTools(INativeFlControl fl)
{
    [McpServerTool(Name = "native_add_note")]
    [Description("Add ONE note to a pattern's piano roll. For >1 note (melodies/chords/drums/basslines) use native_add_notes — it places all notes in one call, much faster. pattern: 1-based, or 0 = current. Positions/lengths in PPQ ticks (call native_get_ppq, often 960/quarter). key = MIDI 0-131 (60 = middle C). velocity 0-127.")]
    public Task<string> AddNote(
        [Description("Pattern 1-based, or 0 = current")] int pattern,
        [Description("Channel index (instrument, 0-based)")] int channel,
        [Description("MIDI note 0-131 (60 = middle C)")] int key,
        [Description("Start in PPQ ticks from pattern start")] int startTick,
        [Description("Length in PPQ ticks")] int lengthTick,
        [Description("Velocity 0-127 (100 = default)")] int velocity = 100,
        CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () =>
        {
            await fl.AddNoteAsync(pattern, channel, key, startTick, lengthTick, velocity, ct);
            return $"Added note key={key} @tick {startTick} (len {lengthTick}, vel {velocity}) on pattern {(pattern <= 0 ? "current" : pattern.ToString())}, channel {channel}.";
        });

    [McpServerTool(Name = "native_add_notes")]
    [Description("Add MANY notes to a pattern's piano roll in ONE call — strongly preferred over repeated native_add_note (one bridge round-trip + one editor refresh). For melodies/chords/drums/basslines. pattern: 1-based, or 0 = current. PPQ ticks (call native_get_ppq, often 960/quarter). notes: a list separated by ';' or newlines, each entry 'key,start,length,velocity' with an OPTIONAL 5th field = that note's channel — e.g. '60,0,480,100; 64,480,480,100; 67,960,480,90'. Notes without a 5th field use the 'channel' argument. A chord = notes sharing the same start tick.")]
    public async Task<string> AddNotes(
        [Description("Pattern 1-based, or 0 = current")] int pattern,
        [Description("Default channel for notes lacking their own 5th field")] int channel,
        [Description("Notes separated by ';'/newlines; each 'key,start,length,velocity[,channel]' (PPQ ticks)")] string notes,
        CancellationToken ct = default)
    {
        try
        {
            var parsed = McpSupport.ParseNotes(notes, channel);
            if (parsed.Count == 0) return "No notes parsed — provide notes like '60,0,480,100; 64,480,480,100'.";
            await fl.AddNotesAsync(pattern, parsed, ct);
            return $"Added {parsed.Count} note(s) to pattern {(pattern <= 0 ? "current" : pattern.ToString())}.";
        }
        catch (Exception ex) { return McpSupport.BridgeError(ex); }
    }

    [McpServerTool(Name = "native_get_notes", ReadOnly = true)]
    [Description("Read piano-roll notes in a pattern (pattern: 1-based, or 0 = current). channel = index to filter, or -1 = all. Returns each note's channel, pitch, position (ticks), length, velocity. Use this to understand existing music before editing.")]
    public Task<string> GetNotes(
        [Description("Pattern 1-based, or 0 = current")] int pattern = 0,
        [Description("Channel to filter, or -1 = all")] int channel = -1,
        [Description("Skip first N notes (the result's continuation hint gives the next offset)")] int offset = 0,
        CancellationToken ct = default) =>
        McpSupport.SafeAsync(() => fl.GetNotesAsync(pattern, channel, offset, ct));
}
