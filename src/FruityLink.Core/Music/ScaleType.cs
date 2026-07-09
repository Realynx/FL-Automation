namespace FruityLink.Core.Music;

/// <summary>
/// The scales/modes the engine understands. The seven diatonic modes plus harmonic
/// and melodic minor are heptatonic (7 notes) and support standard tertian chord
/// building; the pentatonic and blues scales are included for melodic generation.
/// </summary>
public enum ScaleType
{
    Major,          // Ionian
    NaturalMinor,   // Aeolian
    Dorian,
    Phrygian,
    Lydian,
    Mixolydian,
    Locrian,
    HarmonicMinor,
    MelodicMinor,   // ascending (jazz) melodic minor
    MajorPentatonic,
    MinorPentatonic,
    Blues,          // minor blues hexatonic
}
