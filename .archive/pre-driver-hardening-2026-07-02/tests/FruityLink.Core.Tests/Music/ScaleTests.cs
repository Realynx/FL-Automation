using FruityLink.Core.Music;
using Shouldly;
using Xunit;

namespace FruityLink.Core.Tests.Music;

public class ScaleTests
{
    [Fact]
    public void Major_scale_has_expected_intervals() =>
        Scales.Get(ScaleType.Major).Intervals.ShouldBe(new[] { 0, 2, 4, 5, 7, 9, 11 });

    [Fact]
    public void All_twelve_scales_are_catalogued() =>
        Scales.All.Count.ShouldBe(12);

    [Theory]
    [InlineData("major", ScaleType.Major)]
    [InlineData("Minor", ScaleType.NaturalMinor)]
    [InlineData("aeolian", ScaleType.NaturalMinor)]
    [InlineData("Dorian", ScaleType.Dorian)]
    [InlineData("mixo", ScaleType.Mixolydian)]
    [InlineData("harmonic minor", ScaleType.HarmonicMinor)]
    [InlineData("blues", ScaleType.Blues)]
    public void TryParse_understands_aliases(string text, ScaleType expected)
    {
        Scales.TryParse(text, out var type).ShouldBeTrue();
        type.ShouldBe(expected);
    }

    [Fact]
    public void TryParse_rejects_unknown() =>
        Scales.TryParse("klingon", out _).ShouldBeFalse();

    [Fact]
    public void DegreeToMidi_wraps_octaves()
    {
        var major = Scales.Get(ScaleType.Major);
        major.DegreeToMidi(60, 0).ShouldBe(60);  // C4
        major.DegreeToMidi(60, 7).ShouldBe(72);  // C5 (one octave up)
        major.DegreeToMidi(60, -1).ShouldBe(59); // B3 (one below)
    }
}
