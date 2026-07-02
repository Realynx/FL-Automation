using System.ComponentModel;
using System.Text;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;
using FruityLink.Core.Music;
using Microsoft.SemanticKernel;

namespace FruityLink.Agent.Plugins;

/// <summary>
/// Music-theory tools: list scales and build diatonic chord progressions. Returns the computed
/// notes (key, start tick, length, velocity) so they can be inserted into the piano roll via the
/// native control tools.
/// </summary>
public sealed class MusicTheoryPlugin(IOperationAuditSink audit, ISettingsStore settings)
{
    [KernelFunction("get_creative_soul")]
    [Description("User's current creative 'soul' — profile (adventurousness, brightness, complexity, dissonance, rhythmic density, style) you MUST apply to bias any melody/chord/musical idea you generate. Consult BEFORE generating so choices match the artist's direction.")]
    public async Task<string> GetCreativeSoulAsync(CancellationToken ct = default)
    {
        var app = await settings.LoadAsync(ct);
        return app.SoulOrDefault.ToPromptFragment();
    }

    [KernelFunction("list_scales")]
    [Description("List supported scales/modes for building chords + melodies.")]
    public string ListScales() =>
        string.Join("\n", Scales.All.Select(s => $"- {s.DisplayName} [{string.Join(",", s.Intervals)}]"));

    [KernelFunction("build_chord_progression")]
    [Description("Build a diatonic chord progression from scale degrees (e.g. degrees [1,6,4,5] in C major = I–vi–IV–V). Returns the chords + computed notes (key/MIDI number, start tick, length, velocity, PPQ) to add to a pattern's piano roll with the native note tools.")]
    public string BuildChordProgression(
        [Description("Tonic/key root, e.g. C, F#, Bb")] string root,
        [Description("Scale/mode, e.g. major, minor, dorian, mixolydian, harmonic minor")] string scale,
        [Description("Scale degrees in order, 1-based, e.g. [1,6,4,5]")] int[] degrees,
        [Description("Seventh chords instead of triads")] bool sevenths = false,
        [Description("Tonic octave; 4 = middle C region")] int octave = 4,
        [Description("Beats per chord; 4 = one bar in 4/4")] int beatsPerChord = 4)
    {
        if (!Scales.TryParse(scale, out var scaleType))
            return $"Unknown scale '{scale}'. Supported: {string.Join(", ", Scales.All.Select(s => s.DisplayName))}.";
        if (degrees is null || degrees.Length == 0)
            return "Provide at least one scale degree, e.g. [1,6,4,5].";

        // Clamp the ranges a hallucinated value could blow past (octave/beats out of MIDI range would
        // otherwise throw out of this method and abort the whole tool-calling turn).
        octave = Math.Clamp(octave, 0, 10);
        beatsPerChord = Math.Clamp(beatsPerChord, 1, 64);

        try
        {
            var request = new ProgressionRequest(root, scaleType, degrees, sevenths, octave, beatsPerChord);
            var result = ChordProgressionComposer.Compose(request);

            audit.Record(new FlOperation(
                FlOperationKind.WriteNotes,
                $"Computed {result.Notes.Count} notes for {result.Summary}",
                "pianoroll", UndoHint: null, DateTimeOffset.UtcNow));

            var sb = new StringBuilder();
            sb.AppendLine($"{result.Summary} ({result.Chords.Count} chords, {result.Notes.Count} notes, PPQ={result.Ppq}).");
            sb.AppendLine("Notes (key=MIDI number, start/length in ticks, velocity 0-127):");
            foreach (NoteEvent n in result.Notes)
                sb.AppendLine($"  key={n.Number} start={n.StartTicks} length={n.LengthTicks} vel={n.Velocity}");
            return sb.ToString();
        }
        catch (FormatException ex) { return $"Couldn't read the key/root: {ex.Message}"; }
        catch (ArgumentException ex) { return $"Invalid request: {ex.Message}"; }
        catch (Exception ex) { return $"Couldn't build progression: {ex.Message}"; }
    }
}
