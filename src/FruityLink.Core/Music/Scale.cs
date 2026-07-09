namespace FruityLink.Core.Music;

/// <summary>
/// A scale defined by its semitone intervals from the root (within one octave).
/// </summary>
/// <param name="Type">The scale identity.</param>
/// <param name="DisplayName">Human-friendly name for prompts/UI.</param>
/// <param name="Intervals">Ascending semitone offsets from the root, e.g. major = 0,2,4,5,7,9,11.</param>
public sealed record Scale(ScaleType Type, string DisplayName, IReadOnlyList<int> Intervals)
{
    /// <summary>Number of notes per octave (7 for diatonic, 5 for pentatonic, 6 for blues).</summary>
    public int Count => Intervals.Count;

    /// <summary>
    /// Returns the MIDI note for a 0-based scale degree relative to <paramref name="rootMidi"/>,
    /// wrapping across octaves so indices beyond the scale length keep ascending.
    /// </summary>
    public int DegreeToMidi(int rootMidi, int degreeIndex)
    {
        int count = Intervals.Count;
        int octaveShift = (int)Math.Floor((double)degreeIndex / count);
        int within = ((degreeIndex % count) + count) % count;
        return rootMidi + octaveShift * 12 + Intervals[within];
    }
}

/// <summary>Catalog of the supported scales.</summary>
public static class Scales
{
    private static readonly IReadOnlyDictionary<ScaleType, Scale> Map = new[]
    {
        new Scale(ScaleType.Major,           "Major (Ionian)",       new[] { 0, 2, 4, 5, 7, 9, 11 }),
        new Scale(ScaleType.NaturalMinor,    "Natural Minor (Aeolian)", new[] { 0, 2, 3, 5, 7, 8, 10 }),
        new Scale(ScaleType.Dorian,          "Dorian",               new[] { 0, 2, 3, 5, 7, 9, 10 }),
        new Scale(ScaleType.Phrygian,        "Phrygian",             new[] { 0, 1, 3, 5, 7, 8, 10 }),
        new Scale(ScaleType.Lydian,          "Lydian",               new[] { 0, 2, 4, 6, 7, 9, 11 }),
        new Scale(ScaleType.Mixolydian,      "Mixolydian",           new[] { 0, 2, 4, 5, 7, 9, 10 }),
        new Scale(ScaleType.Locrian,         "Locrian",              new[] { 0, 1, 3, 5, 6, 8, 10 }),
        new Scale(ScaleType.HarmonicMinor,   "Harmonic Minor",       new[] { 0, 2, 3, 5, 7, 8, 11 }),
        new Scale(ScaleType.MelodicMinor,    "Melodic Minor",        new[] { 0, 2, 3, 5, 7, 9, 11 }),
        new Scale(ScaleType.MajorPentatonic, "Major Pentatonic",     new[] { 0, 2, 4, 7, 9 }),
        new Scale(ScaleType.MinorPentatonic, "Minor Pentatonic",     new[] { 0, 3, 5, 7, 10 }),
        new Scale(ScaleType.Blues,           "Blues (minor)",        new[] { 0, 3, 5, 6, 7, 10 }),
    }.ToDictionary(s => s.Type);

    /// <summary>All supported scales.</summary>
    public static IReadOnlyCollection<Scale> All => (IReadOnlyCollection<Scale>)Map.Values;

    /// <summary>Looks up a scale by type.</summary>
    public static Scale Get(ScaleType type) => Map[type];

    /// <summary>
    /// Resolves a scale from a free-text name (case/space/punctuation insensitive),
    /// understanding common aliases such as "minor", "ionian", "aeolian".
    /// </summary>
    public static bool TryParse(string name, out ScaleType type)
    {
        string key = Normalize(name);
        switch (key)
        {
            case "major": case "ionian": case "maj": type = ScaleType.Major; return true;
            case "minor": case "naturalminor": case "aeolian": case "min": type = ScaleType.NaturalMinor; return true;
            case "dorian": type = ScaleType.Dorian; return true;
            case "phrygian": type = ScaleType.Phrygian; return true;
            case "lydian": type = ScaleType.Lydian; return true;
            case "mixolydian": case "mixo": type = ScaleType.Mixolydian; return true;
            case "locrian": type = ScaleType.Locrian; return true;
            case "harmonicminor": case "harmonic": type = ScaleType.HarmonicMinor; return true;
            case "melodicminor": case "melodic": type = ScaleType.MelodicMinor; return true;
            case "majorpentatonic": case "majpentatonic": case "pentatonicmajor": type = ScaleType.MajorPentatonic; return true;
            case "minorpentatonic": case "minpentatonic": case "pentatonic": case "pentatonicminor": type = ScaleType.MinorPentatonic; return true;
            case "blues": case "bluesminor": case "minorblues": type = ScaleType.Blues; return true;
            default:
                type = ScaleType.Major;
                return false;
        }
    }

    private static string Normalize(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        int n = 0;
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
                buffer[n++] = char.ToLowerInvariant(c);
        }
        return new string(buffer[..n]);
    }
}
