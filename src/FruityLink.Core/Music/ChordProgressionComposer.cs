namespace FruityLink.Core.Music;

/// <summary>
/// Builds diatonic chord progressions by stacking thirds within a scale. For
/// heptatonic scales this yields conventional tertian chords with correct qualities;
/// for pentatonic/blues scales it stacks the available scale tones.
/// </summary>
public static class ChordProgressionComposer
{
    private static readonly string[] RomanNumerals =
        { "I", "II", "III", "IV", "V", "VI", "VII" };

    private static readonly int[] TriadOffsets = { 0, 2, 4 };
    private static readonly int[] SeventhOffsets = { 0, 2, 4, 6 };

    public static ProgressionResult Compose(ProgressionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Degrees.Count == 0)
            throw new ArgumentException("At least one degree is required.", nameof(request));
        if (request.Ppq <= 0)
            throw new ArgumentException("Ppq must be positive.", nameof(request));

        Scale scale = Scales.Get(request.Scale);
        int rootPc = NoteName.ParsePitchClass(request.Root);
        int rootMidi = NoteName.ToMidi(rootPc, request.Octave);
        int ticksPerChord = request.Ppq * request.BeatsPerChord;
        int velocity = Math.Clamp(request.Velocity, 1, 127);

        var chords = new List<Chord>(request.Degrees.Count);
        var notes = new List<NoteEvent>(request.Degrees.Count * (request.SeventhChords ? 4 : 3));
        int[] stackOffsets = request.SeventhChords ? SeventhOffsets : TriadOffsets;

        for (int i = 0; i < request.Degrees.Count; i++)
        {
            int degree = request.Degrees[i];
            int degreeIndex = degree - 1; // 0-based into the scale

            var noteNumbers = stackOffsets
                .Select(off => scale.DegreeToMidi(rootMidi, degreeIndex + off))
                .ToArray();

            int startTicks = i * ticksPerChord;
            ChordQuality quality = InferQuality(noteNumbers);
            int chordRootPc = NoteName.PitchClass(noteNumbers[0]);

            var chord = new Chord(
                Degree: NormalizeDegree(degree),
                Roman: BuildRoman(degree, quality),
                Symbol: NoteName.SharpNames[chordRootPc] + Suffix(quality),
                RootMidi: noteNumbers[0],
                Quality: quality,
                NoteNumbers: noteNumbers,
                StartTicks: startTicks,
                LengthTicks: ticksPerChord);

            chords.Add(chord);
            notes.AddRange(chord.ToNoteEvents(velocity));
        }

        return new ProgressionResult(request, scale, chords, notes, request.Ppq);
    }

    private static int NormalizeDegree(int degree) => ((degree - 1) % 7 + 7) % 7 + 1;

    private static ChordQuality InferQuality(IReadOnlyList<int> notes)
    {
        int third = notes[1] - notes[0];
        int fifth = notes[2] - notes[0];

        if (notes.Count >= 4)
        {
            int seventh = notes[3] - notes[0];
            return (third, fifth, seventh) switch
            {
                (4, 7, 11) => ChordQuality.MajorSeventh,
                (4, 7, 10) => ChordQuality.DominantSeventh,
                (3, 7, 10) => ChordQuality.MinorSeventh,
                (3, 7, 11) => ChordQuality.MinorMajorSeventh,
                (3, 6, 10) => ChordQuality.MinorSeventhFlatFive,
                (3, 6, 9) => ChordQuality.DiminishedSeventh,
                (4, 8, 10) => ChordQuality.AugmentedSeventh,
                _ => ChordQuality.Unknown,
            };
        }

        return (third, fifth) switch
        {
            (4, 7) => ChordQuality.Major,
            (3, 7) => ChordQuality.Minor,
            (3, 6) => ChordQuality.Diminished,
            (4, 8) => ChordQuality.Augmented,
            _ => ChordQuality.Unknown,
        };
    }

    private static bool IsMinorFlavoured(ChordQuality q) => q is
        ChordQuality.Minor or ChordQuality.Diminished or ChordQuality.MinorSeventh or
        ChordQuality.MinorSeventhFlatFive or ChordQuality.DiminishedSeventh or
        ChordQuality.MinorMajorSeventh;

    private static string BuildRoman(int degree, ChordQuality quality)
    {
        string numeral = RomanNumerals[NormalizeDegree(degree) - 1];
        if (IsMinorFlavoured(quality))
            numeral = numeral.ToLowerInvariant();

        return quality switch
        {
            ChordQuality.Diminished => numeral + "°",
            ChordQuality.Augmented => numeral + "+",
            ChordQuality.MajorSeventh => numeral + "maj7",
            ChordQuality.DominantSeventh => numeral + "7",
            ChordQuality.MinorSeventh => numeral + "7",
            ChordQuality.MinorSeventhFlatFive => numeral + "ø7",
            ChordQuality.DiminishedSeventh => numeral + "°7",
            ChordQuality.MinorMajorSeventh => numeral + "(maj7)",
            ChordQuality.AugmentedSeventh => numeral + "+7",
            _ => numeral,
        };
    }

    private static string Suffix(ChordQuality quality) => quality switch
    {
        ChordQuality.Major => "",
        ChordQuality.Minor => "m",
        ChordQuality.Diminished => "dim",
        ChordQuality.Augmented => "aug",
        ChordQuality.MajorSeventh => "maj7",
        ChordQuality.DominantSeventh => "7",
        ChordQuality.MinorSeventh => "m7",
        ChordQuality.MinorSeventhFlatFive => "m7b5",
        ChordQuality.DiminishedSeventh => "dim7",
        ChordQuality.MinorMajorSeventh => "m(maj7)",
        ChordQuality.AugmentedSeventh => "aug7",
        _ => "?",
    };
}
