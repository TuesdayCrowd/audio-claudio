using System.Collections.Generic;
using System.Linq;

namespace AudioClaudio.Domain.Polyphony;

/// <summary>
/// Splits a chord across the grand staff by register: pitches at or above the split point go to the
/// treble staff, below it to the bass. A chord straddling the split yields both fragments (sharing
/// the original onset, duration and velocity); a chord entirely on one side yields only that one.
/// A fixed split at middle C is the simple, deterministic default; a smarter per-hand split is a
/// later refinement.
/// </summary>
public static class StaffSplitter
{
    /// <summary>Middle C (MIDI 60): this note and above → treble, below → bass.</summary>
    public const int DefaultSplitMidi = 60;

    public static (Chord? Treble, Chord? Bass) Split(Chord chord, int splitMidi = DefaultSplitMidi)
    {
        ArgumentNullException.ThrowIfNull(chord);

        var treble = chord.Pitches.Where(p => p.MidiNumber >= splitMidi).ToList();
        var bass = chord.Pitches.Where(p => p.MidiNumber < splitMidi).ToList();

        return (
            treble.Count > 0 ? chord with { Pitches = treble } : null,
            bass.Count > 0 ? chord with { Pitches = bass } : null);
    }
}
