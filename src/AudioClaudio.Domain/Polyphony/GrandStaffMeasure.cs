using System.Collections.Generic;

namespace AudioClaudio.Domain.Polyphony;

/// <summary>
/// One barred measure of a piano grand staff: an independent sequence of chord/rest elements for
/// the treble staff and for the bass staff. Each staff's elements sum to a full bar
/// (bar conservation holds per staff).
/// </summary>
public sealed record GrandStaffMeasure(
    IReadOnlyList<ChordElement> Treble, IReadOnlyList<ChordElement> Bass);
