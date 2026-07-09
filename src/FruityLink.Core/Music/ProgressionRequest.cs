namespace FruityLink.Core.Music;

/// <summary>
/// A request to build a diatonic chord progression from scale degrees, e.g.
/// degrees [1,6,4,3] in C Major produces I–vi–IV–iii.
/// </summary>
/// <param name="Root">Tonic note name, e.g. "C", "F#", "Bb".</param>
/// <param name="Scale">Scale/mode the progression is diatonic to.</param>
/// <param name="Degrees">1-based scale degrees, in order.</param>
/// <param name="SeventhChords">When true, builds four-note seventh chords instead of triads.</param>
/// <param name="Octave">Octave of the tonic (C4 = MIDI 60).</param>
/// <param name="BeatsPerChord">Quarter-note beats each chord sustains (4 = one 4/4 bar).</param>
/// <param name="Velocity">MIDI velocity 1-127 for the generated notes.</param>
/// <param name="Ppq">Pulses-per-quarter resolution for tick math (FL default is 96).</param>
public sealed record ProgressionRequest(
    string Root,
    ScaleType Scale,
    IReadOnlyList<int> Degrees,
    bool SeventhChords = false,
    int Octave = 4,
    int BeatsPerChord = 4,
    int Velocity = 100,
    int Ppq = 96);

/// <summary>The result of composing a progression: labelled chords and flattened notes.</summary>
public sealed record ProgressionResult(
    ProgressionRequest Request,
    Scale Scale,
    IReadOnlyList<Chord> Chords,
    IReadOnlyList<NoteEvent> Notes,
    int Ppq)
{
    /// <summary>Compact human/LLM-readable summary, e.g. "C Major: I – vi – IV – V".</summary>
    public string Summary =>
        $"{NoteName.SharpNames[NoteName.ParsePitchClass(Request.Root)]} {Scale.DisplayName}: "
        + string.Join(" – ", Chords.Select(c => c.Roman))
        + $"  [{string.Join(" ", Chords.Select(c => c.Symbol))}]";
}
