using System.Collections.Generic;

namespace AudioClaudio.Domain.Polyphony;

/// <summary>
/// Removes suspected <b>sub-octave harmonic ghosts</b> from a decoded note list — the false positives a
/// neural polyphonic transcriber spawns an octave below a real note (its own sub-harmonic). A note is
/// dropped when a note exactly one octave above it overlaps in time and is at least
/// <c>minSourceToGhostRatio</c>× as loud (its velocity). The artifact is weaker than the note that
/// spawned it, so requiring the octave-above to be meaningfully louder keeps genuine bass octaves — where
/// the lower note is comparably loud — while shedding the ghosts. A <b>precision</b> fix that, validated
/// on the synthetic closed loop, does not cost real recall (unlike a blunt threshold raise). Pure and
/// deterministic; the input is never mutated.
/// </summary>
public static class HarmonicGhostFilter
{
    /// <summary>Default loudness ratio the octave-above "source" must reach over a suppressed ghost.
    /// Conservative (&gt; 1): the octave-above must be clearly louder, so near-equal real octaves survive.</summary>
    public const double DefaultMinSourceToGhostRatio = 1.1;

    public static IReadOnlyList<NoteEvent> Suppress(
        IReadOnlyList<NoteEvent> notes, double minSourceToGhostRatio = DefaultMinSourceToGhostRatio)
    {
        ArgumentNullException.ThrowIfNull(notes);
        if (notes.Count == 0)
        {
            return notes;
        }

        // Index note positions by pitch so each candidate ghost only scans its octave-above notes.
        var byPitch = new Dictionary<int, List<int>>();
        for (int i = 0; i < notes.Count; i++)
        {
            int midi = notes[i].Pitch.MidiNumber;
            if (!byPitch.TryGetValue(midi, out List<int>? list))
            {
                list = new List<int>();
                byPitch[midi] = list;
            }

            list.Add(i);
        }

        var kept = new List<NoteEvent>(notes.Count);
        for (int i = 0; i < notes.Count; i++)
        {
            if (!IsGhost(notes, i, byPitch, minSourceToGhostRatio))
            {
                kept.Add(notes[i]);
            }
        }

        return kept;
    }

    private static bool IsGhost(
        IReadOnlyList<NoteEvent> notes, int i, Dictionary<int, List<int>> byPitch, double ratio)
    {
        NoteEvent ghost = notes[i];
        if (!byPitch.TryGetValue(ghost.Pitch.MidiNumber + 12, out List<int>? octaveAbove))
        {
            return false;
        }

        long ghostStart = ghost.Onset.Samples;
        long ghostEnd = ghostStart + ghost.Duration.Samples;
        double loudnessBar = ghost.Velocity * ratio;

        foreach (int j in octaveAbove)
        {
            NoteEvent source = notes[j];
            long srcStart = source.Onset.Samples;
            long srcEnd = srcStart + source.Duration.Samples;
            bool overlaps = ghostStart < srcEnd && srcStart < ghostEnd;
            if (overlaps && source.Velocity >= loudnessBar)
            {
                return true; // a louder octave-above overlaps → this note is its sub-harmonic ghost
            }
        }

        return false;
    }
}
