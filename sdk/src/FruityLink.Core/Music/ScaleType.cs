namespace FruityLink.Core.Music;

/// <summary>
/// The scales/modes the engine understands. The seven diatonic modes plus harmonic
/// and melodic minor are heptatonic (7 notes) and support standard tertian chord
/// building; the pentatonic and blues scales are included for melodic generation.
/// </summary>
public enum ScaleType
{
    /// <summary>Ionian mode.</summary>
    Major,          // Ionian
    /// <summary>Aeolian mode.</summary>
    NaturalMinor,   // Aeolian
    /// <summary>Minor mode with a raised sixth.</summary>
    Dorian,
    /// <summary>Minor mode with a lowered second.</summary>
    Phrygian,
    /// <summary>Major mode with a raised fourth.</summary>
    Lydian,
    /// <summary>Major mode with a lowered seventh.</summary>
    Mixolydian,
    /// <summary>Minor mode with a lowered second and fifth.</summary>
    Locrian,
    /// <summary>Natural minor with a raised seventh.</summary>
    HarmonicMinor,
    /// <summary>Ascending melodic minor (jazz minor).</summary>
    MelodicMinor,   // ascending (jazz) melodic minor
    /// <summary>Five-note major scale.</summary>
    MajorPentatonic,
    /// <summary>Five-note minor scale.</summary>
    MinorPentatonic,
    /// <summary>Six-note minor blues scale.</summary>
    Blues,          // minor blues hexatonic
}
