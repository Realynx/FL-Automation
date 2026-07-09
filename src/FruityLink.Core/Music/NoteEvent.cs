namespace FruityLink.Core.Music;

/// <summary>
/// A single placed note in MIDI terms, independent of any FL Studio transport.
/// Time is expressed in ticks relative to a stated pulses-per-quarter resolution.
/// </summary>
/// <param name="Number">MIDI note number 0-127 (60 = middle C).</param>
/// <param name="StartTicks">Start position in ticks from the pattern origin.</param>
/// <param name="LengthTicks">Duration in ticks.</param>
/// <param name="Velocity">MIDI velocity 1-127.</param>
public readonly record struct NoteEvent(int Number, int StartTicks, int LengthTicks, int Velocity);
