namespace FruityLink.Core.Music;

/// <summary>
/// Helpers for converting between note names, pitch classes (0-11), and MIDI note
/// numbers. Uses scientific pitch notation where middle C (C4) is MIDI note 60,
/// i.e. <c>midi = (octave + 1) * 12 + pitchClass</c>.
/// </summary>
public static class NoteName
{
    /// <summary>Sharp spelling for each pitch class 0-11.</summary>
    public static readonly IReadOnlyList<string> SharpNames =
        new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    /// <summary>Flat spelling for each pitch class 0-11.</summary>
    public static readonly IReadOnlyList<string> FlatNames =
        new[] { "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B" };

    /// <summary>
    /// Parses a note-letter (optionally with one or more accidentals) into a pitch
    /// class 0-11. Accepts ASCII '#'/'b' as well as the unicode sharp/flat glyphs.
    /// Throws <see cref="FormatException"/> for invalid input.
    /// </summary>
    public static int ParsePitchClass(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new FormatException("Note name was empty.");

        ReadOnlySpan<char> s = name.Trim();
        int semitone = char.ToUpperInvariant(s[0]) switch
        {
            'C' => 0,
            'D' => 2,
            'E' => 4,
            'F' => 5,
            'G' => 7,
            'A' => 9,
            'B' => 11,
            _ => throw new FormatException($"'{name}' is not a valid note name."),
        };

        for (int i = 1; i < s.Length; i++)
        {
            semitone += s[i] switch
            {
                '#' or '♯' => 1,
                'b' or 'B' or '♭' => -1,
                'x' or 'X' => 2, // double sharp
                _ => throw new FormatException($"'{name}' has an invalid accidental '{s[i]}'."),
            };
        }

        return ((semitone % 12) + 12) % 12;
    }

    /// <summary>True if <paramref name="name"/> parses to a valid pitch class.</summary>
    public static bool TryParsePitchClass(string name, out int pitchClass)
    {
        try
        {
            pitchClass = ParsePitchClass(name);
            return true;
        }
        catch (FormatException)
        {
            pitchClass = 0;
            return false;
        }
    }

    /// <summary>MIDI note number for a pitch class in a given octave (C4 = 60).</summary>
    public static int ToMidi(int pitchClass, int octave) => (octave + 1) * 12 + pitchClass;

    /// <summary>MIDI note number for a named root in a given octave, e.g. ("C", 4) =&gt; 60.</summary>
    public static int ToMidi(string name, int octave) => ToMidi(ParsePitchClass(name), octave);

    /// <summary>Sharp-spelled name with octave for a MIDI number, e.g. 60 =&gt; "C4".</summary>
    public static string ToName(int midi)
    {
        int pitchClass = ((midi % 12) + 12) % 12;
        int octave = midi / 12 - 1;
        return SharpNames[pitchClass] + octave;
    }
}
