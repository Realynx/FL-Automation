using System.ComponentModel;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;
using FruityLink.Core.Music;
using Microsoft.SemanticKernel;
using static FruityLink.Agent.Plugins.PluginSupport;

namespace FruityLink.Agent.Plugins;

/// <summary>
/// Music-theory tools: list scales and build diatonic chord progressions. Returns the computed
/// notes in native_add_notes' exact CSV grammar so the model can pass them straight through to the
/// piano roll without re-encoding (re-encoding is where weak backends drop or mangle notes).
/// The ticks are emitted at the composer's default PPQ of 96 (FL's factory timebase): this tool is
/// deliberately offline (no bridge dependency), so it cannot read the project's live PPQ — the
/// result instead tells the model to rescale via native_get_ppq when the project timebase differs.
/// </summary>
public sealed class MusicTheoryPlugin(IOperationAuditSink audit, ISettingsStore settings)
{
    [KernelFunction("get_creative_soul")]
    [Description("User's creative profile (adventurousness, brightness, complexity, dissonance, rhythmic density, style). Consult BEFORE generating any melody/chord/musical idea so choices match the artist's direction.")]
    public async Task<string> GetCreativeSoulAsync(CancellationToken ct = default)
    {
        var app = await settings.LoadAsync(ct);
        return Ok(app.SoulOrDefault.ToPromptFragment());
    }

    [KernelFunction("list_scales")]
    [Description("List supported scales/modes for chords + melodies.")]
    public string ListScales() =>
        Ok(string.Join("\n", Scales.All.Select(s => $"- {s.DisplayName} [{string.Join(",", s.Intervals)}]")));

    [KernelFunction("build_chord_progression")]
    [Description("Build a diatonic chord progression from scale degrees (e.g. '1,6,4,5' in C major = I–vi–IV–V). Returns the chords plus a notes string for native_add_notes (ticks at PPQ 96; rescale if the project PPQ differs).")]
    public string BuildChordProgression(
        [Description("Tonic/key root, e.g. C, F#, Bb")] string root,
        [Description("Scale/mode, e.g. major, minor, dorian")] string scale,
        [Description("Scale degrees, 1-based, e.g. '1,6,4,5'")] string degrees,
        [Description("Seventh chords instead of triads")] bool sevenths = false,
        [Description("Tonic octave; 4 = middle C region")] int octave = 4,
        [Description("Beats per chord; 4 = one bar in 4/4")] int beatsPerChord = 4)
    {
        if (!Scales.TryParse(scale, out var scaleType))
            return Err($"unknown scale '{scale}'. Supported: {string.Join(", ", Scales.All.Select(s => s.DisplayName))}");

        // degrees is a lenient STRING, not int[] — it was the surface's only array parameter, and
        // weak backends routinely mis-encode JSON arrays (double-encoded, stringified, decimals).
        // Parse with the same tolerance rule as native_add_notes: ints or decimals (rounded),
        // ','/';'/space separated, malformed tokens skipped rather than failing the call.
        var degList = new List<int>();
        foreach (string tok in (degrees ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (TryNum(tok, out int d)) degList.Add(d);
        if (degList.Count == 0)
            return Err("provide scale degrees like '1,6,4,5'");

        // Clamp the ranges a hallucinated value could blow past (octave/beats out of MIDI range would
        // otherwise throw out of this method and abort the whole tool-calling turn).
        octave = Math.Clamp(octave, 0, 10);
        beatsPerChord = Math.Clamp(beatsPerChord, 1, 64);

        try
        {
            var request = new ProgressionRequest(root, scaleType, degList, sevenths, octave, beatsPerChord);
            var result = ChordProgressionComposer.Compose(request);

            audit.Record(new FlOperation(
                FlOperationKind.WriteNotes,
                $"Computed {result.Notes.Count} notes for {result.Summary}",
                "pianoroll", UndoHint: null, DateTimeOffset.UtcNow));

            // Emit the notes in native_add_notes' EXACT CSV grammar ('key,start,length,velocity'
            // entries joined by "; ") so the model forwards them without re-encoding. The ticks are
            // at the composer's default PPQ (96 — see ProgressionRequest.Ppq), but native_add_notes
            // writes RAW ticks at the PROJECT's timebase, which is user-configurable (often 960).
            // A blanket "pass verbatim" would land ~10x-compressed notes on such projects, so the
            // instruction states the tick base and how to rescale, actionably.
            string notesCsv = string.Join("; ", result.Notes.Select(n => $"{n.Number},{n.StartTicks},{n.LengthTicks},{n.Velocity}"));
            return Ok($"{result.Summary} ({result.Chords.Count} chords, {result.Notes.Count} notes).\n" +
                      $"notes for native_add_notes — ticks assume PPQ={result.Ppq}; if native_get_ppq reports a different ppq, " +
                      $"multiply every start/length by ppq/{result.Ppq} before adding: {notesCsv}");
        }
        catch (FormatException ex) { return Err($"couldn't read the key/root: {ex.Message}"); }
        catch (ArgumentException ex) { return Err($"invalid request: {ex.Message}"); }
        catch (Exception ex) { return Err($"couldn't build progression: {ex.Message}"); }
    }
}
