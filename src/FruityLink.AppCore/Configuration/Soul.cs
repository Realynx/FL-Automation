using System.Text;

namespace FruityLink.Core.Configuration;

/// <summary>
/// A creative profile ("soul") that biases the AI's melody / chord / idea generation — how
/// adventurous, bright, complex, dissonant, and rhythmically dense its choices lean, plus an
/// optional free-text style. Lets an artist get ideas fast while keeping creative control:
/// turning a knob shifts the "randomness factor" of the black-box idea generation in a musical way.
/// All sliders are 0.0–1.0.
/// </summary>
public sealed record Soul(
    double Adventurousness = 0.5,
    double Brightness = 0.5,
    double Complexity = 0.5,
    double Dissonance = 0.5,
    double RhythmicDensity = 0.5,
    string Style = "")
{
    private static string Level(double v) =>
        v < 0.2 ? "very low" : v < 0.4 ? "low" : v < 0.6 ? "moderate" : v < 0.8 ? "high" : "very high";

    /// <summary>
    /// A concise natural-language creative brief the AI applies to bias any musical idea it generates.
    /// </summary>
    public string ToPromptFragment()
    {
        var sb = new StringBuilder();
        sb.Append("Creative soul — apply this to bias any melody/chord/idea you generate. ");
        sb.Append($"Adventurousness {Level(Adventurousness)}: ");
        sb.Append(Adventurousness < 0.4 ? "stay mostly diatonic and predictable, safe choices. "
                 : Adventurousness < 0.7 ? "add tasteful surprises and the occasional borrowed chord. "
                 : "be bold — secondary dominants, modal interchange, unexpected turns and wider intervals. ");
        sb.Append($"Tonal brightness {Level(Brightness)}: ");
        sb.Append(Brightness < 0.4 ? "lean dark — minor keys and modal/minor color. "
                 : Brightness < 0.7 ? "balance major and minor. "
                 : "lean bright — major keys, uplifting, open voicings. ");
        sb.Append($"Harmonic complexity {Level(Complexity)}: ");
        sb.Append(Complexity < 0.4 ? "simple triads. "
                 : Complexity < 0.7 ? "some 7th chords and smooth voice-leading. "
                 : "rich extensions (9ths/11ths/13ths), inversions and deliberate voice-leading. ");
        sb.Append($"Dissonance tolerance {Level(Dissonance)}: ");
        sb.Append(Dissonance < 0.4 ? "keep it consonant and clean. "
                 : Dissonance < 0.7 ? "allow mild tension. "
                 : "embrace tension and crunch where it serves the vibe. ");
        sb.Append($"Rhythmic density {Level(RhythmicDensity)}: ");
        sb.Append(RhythmicDensity < 0.4 ? "sparse, long held notes and space."
                 : RhythmicDensity < 0.7 ? "moderate movement."
                 : "busy, syncopated, lots of notes.");
        if (!string.IsNullOrWhiteSpace(Style))
            sb.Append($" Style / vibe: {Style.Trim()}.");
        return sb.ToString();
    }
}
