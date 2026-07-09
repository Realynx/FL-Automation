namespace FruityLink.Core.Music;

/// <summary>The tertian quality of a chord, inferred from its stacked intervals.</summary>
public enum ChordQuality
{
    Major,
    Minor,
    Diminished,
    Augmented,
    MajorSeventh,
    MinorSeventh,
    DominantSeventh,
    MinorSeventhFlatFive, // half-diminished
    DiminishedSeventh,
    MinorMajorSeventh,
    AugmentedSeventh,
    Unknown,
}

/// <summary>
/// A chord produced for one step of a progression: its degree within the scale,
/// its labels, and the concrete MIDI notes plus placement in ticks.
/// </summary>
/// <param name="Degree">1-based scale degree the chord is built on.</param>
/// <param name="Roman">Roman-numeral label (e.g. "ii", "V7", "vii°").</param>
/// <param name="Symbol">Lead-sheet symbol (e.g. "Dm", "G7", "Bdim").</param>
/// <param name="RootMidi">MIDI note of the chord root.</param>
/// <param name="Quality">Inferred tertian quality.</param>
/// <param name="NoteNumbers">MIDI note numbers, lowest first.</param>
/// <param name="StartTicks">Start position of the chord in ticks.</param>
/// <param name="LengthTicks">Duration of the chord in ticks.</param>
public sealed record Chord(
    int Degree,
    string Roman,
    string Symbol,
    int RootMidi,
    ChordQuality Quality,
    IReadOnlyList<int> NoteNumbers,
    int StartTicks,
    int LengthTicks)
{
    /// <summary>Expands the chord into individual <see cref="NoteEvent"/>s at a velocity.</summary>
    public IEnumerable<NoteEvent> ToNoteEvents(int velocity) =>
        NoteNumbers.Select(n => new NoteEvent(n, StartTicks, LengthTicks, velocity));
}
