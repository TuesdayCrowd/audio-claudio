using System.Collections.Generic;

namespace AudioClaudio.Domain.Polyphony;

/// <summary>
/// A vertical sonority before quantization: one or more pitches sounding from a shared
/// <paramref name="Onset"/>, lasting <paramref name="Duration"/> (the longest of its notes), at a
/// representative <paramref name="Velocity"/>. Produced by <see cref="ChordGrouper"/>; consumed by
/// the polyphonic quantizer. Pitches are held in ascending MIDI order.
/// </summary>
public sealed record Chord(
    SamplePosition Onset,
    IReadOnlyList<Pitch> Pitches,
    SampleDuration Duration,
    int Velocity);
