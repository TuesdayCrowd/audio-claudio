using System.Collections.Generic;

namespace AudioClaudio.Domain.Polyphony;

/// <summary>
/// A quantized polyphonic performance on a piano grand staff: tempo, time signature and grid
/// subdivision, plus the barred measures (each with a treble and a bass staff of chords). The
/// polyphonic counterpart of <see cref="Score"/>; the monophonic <see cref="Score"/> is unchanged.
/// </summary>
public sealed record GrandStaffScore(
    Tempo Tempo,
    TimeSignature TimeSignature,
    Subdivision Subdivision,
    IReadOnlyList<GrandStaffMeasure> Measures);
