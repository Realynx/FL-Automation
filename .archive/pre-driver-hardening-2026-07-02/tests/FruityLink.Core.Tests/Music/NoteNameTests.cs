using FruityLink.Core.Music;
using Shouldly;
using Xunit;

namespace FruityLink.Core.Tests.Music;

public class NoteNameTests
{
    [Theory]
    [InlineData("C", 0)]
    [InlineData("C#", 1)]
    [InlineData("Db", 1)]
    [InlineData("d", 2)]
    [InlineData("F#", 6)]
    [InlineData("Gb", 6)]
    [InlineData("A", 9)]
    [InlineData("Bb", 10)]
    [InlineData("B", 11)]
    [InlineData("Cb", 11)] // wraps below C
    [InlineData("B#", 0)]  // wraps above B
    public void ParsePitchClass_parses_names_and_accidentals(string name, int expected) =>
        NoteName.ParsePitchClass(name).ShouldBe(expected);

    [Theory]
    [InlineData("C", 4, 60)]
    [InlineData("A", 4, 69)]
    [InlineData("C", 5, 72)]
    [InlineData("C", -1, 0)]
    public void ToMidi_uses_scientific_pitch(string name, int octave, int expected) =>
        NoteName.ToMidi(name, octave).ShouldBe(expected);

    [Theory]
    [InlineData(60, "C4")]
    [InlineData(69, "A4")]
    [InlineData(61, "C#4")]
    public void ToName_round_trips(int midi, string expected) =>
        NoteName.ToName(midi).ShouldBe(expected);

    [Fact]
    public void ParsePitchClass_rejects_garbage() =>
        Should.Throw<FormatException>(() => NoteName.ParsePitchClass("H"));
}
