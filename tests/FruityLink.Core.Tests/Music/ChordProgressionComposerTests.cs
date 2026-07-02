using FruityLink.Core.Music;
using Shouldly;
using Xunit;

namespace FruityLink.Core.Tests.Music;

public class ChordProgressionComposerTests
{
    private static ProgressionResult Compose(
        string root, ScaleType scale, int[] degrees, bool sevenths = false, int octave = 4) =>
        ChordProgressionComposer.Compose(
            new ProgressionRequest(root, scale, degrees, SeventhChords: sevenths, Octave: octave));

    [Fact]
    public void I_vi_IV_V_in_C_major_has_correct_notes_and_labels()
    {
        var result = Compose("C", ScaleType.Major, new[] { 1, 6, 4, 5 });

        result.Chords.Count.ShouldBe(4);

        result.Chords[0].NoteNumbers.ShouldBe(new[] { 60, 64, 67 }); // C E G
        result.Chords[0].Roman.ShouldBe("I");
        result.Chords[0].Symbol.ShouldBe("C");
        result.Chords[0].Quality.ShouldBe(ChordQuality.Major);

        result.Chords[1].NoteNumbers.ShouldBe(new[] { 69, 72, 76 }); // A C E
        result.Chords[1].Roman.ShouldBe("vi");
        result.Chords[1].Symbol.ShouldBe("Am");
        result.Chords[1].Quality.ShouldBe(ChordQuality.Minor);

        result.Chords[2].NoteNumbers.ShouldBe(new[] { 65, 69, 72 }); // F A C
        result.Chords[2].Symbol.ShouldBe("F");

        result.Chords[3].NoteNumbers.ShouldBe(new[] { 67, 71, 74 }); // G B D
        result.Chords[3].Roman.ShouldBe("V");
        result.Chords[3].Symbol.ShouldBe("G");
    }

    [Fact]
    public void Seventh_degree_in_C_major_is_diminished()
    {
        var chord = Compose("C", ScaleType.Major, new[] { 7 }).Chords[0];
        chord.NoteNumbers.ShouldBe(new[] { 71, 74, 77 }); // B D F
        chord.Quality.ShouldBe(ChordQuality.Diminished);
        chord.Roman.ShouldBe("vii°");
        chord.Symbol.ShouldBe("Bdim");
    }

    [Fact]
    public void Dominant_seventh_on_V_in_C_major()
    {
        var chord = Compose("C", ScaleType.Major, new[] { 5 }, sevenths: true).Chords[0];
        chord.NoteNumbers.ShouldBe(new[] { 67, 71, 74, 77 }); // G B D F
        chord.Quality.ShouldBe(ChordQuality.DominantSeventh);
        chord.Roman.ShouldBe("V7");
        chord.Symbol.ShouldBe("G7");
    }

    [Fact]
    public void Tonic_of_D_dorian_is_minor()
    {
        // D Dorian = D E F G A B C; the i chord is D F A (D minor).
        var chord = Compose("D", ScaleType.Dorian, new[] { 1 }).Chords[0];
        chord.NoteNumbers.ShouldBe(new[] { 62, 65, 69 });
        chord.Quality.ShouldBe(ChordQuality.Minor);
        chord.Symbol.ShouldBe("Dm");
    }

    [Fact]
    public void Timing_uses_ppq_and_beats_per_chord()
    {
        var result = ChordProgressionComposer.Compose(
            new ProgressionRequest("C", ScaleType.Major, new[] { 1, 5 }, Ppq: 96, BeatsPerChord: 4));

        result.Chords[0].StartTicks.ShouldBe(0);
        result.Chords[0].LengthTicks.ShouldBe(384); // 96 * 4
        result.Chords[1].StartTicks.ShouldBe(384);

        // 2 triads => 6 note events, all velocity-clamped into 1..127.
        result.Notes.Count.ShouldBe(6);
        result.Notes.ShouldAllBe(n => n.Velocity >= 1 && n.Velocity <= 127);
    }

    [Fact]
    public void Empty_degrees_throws() =>
        Should.Throw<ArgumentException>(() =>
            ChordProgressionComposer.Compose(new ProgressionRequest("C", ScaleType.Major, Array.Empty<int>())));
}
