using System.Collections.Generic;
using System.Linq;

namespace AudioClaudio.Domain;

/// <summary>
/// Folds sustain-pedal (MIDI CC64) presses into note durations, so a transcription that models the
/// pedal as short notes plus a held pedal — the musically correct representation a good piano
/// transcriber emits — rings as intended when synthesized through the plain note→synth path (which has
/// no pedal concept). A note whose key-release falls within a pedal-down span is extended to the pedal's
/// release. Pure and deterministic; the input is not mutated.
/// </summary>
public static class SustainPedal
{
    /// <summary>A pedal state change: the sustain pedal goes down (<c>true</c>) or up at a sample position.</summary>
    public readonly record struct Change(long Sample, bool Down);

    public static IReadOnlyList<NoteEvent> Flatten(IReadOnlyList<NoteEvent> notes, IReadOnlyList<Change> pedalChanges)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(pedalChanges);
        if (pedalChanges.Count == 0)
        {
            return notes;
        }

        Change[] changes = pedalChanges.OrderBy(c => c.Sample).ToArray();
        var result = new List<NoteEvent>(notes.Count);
        foreach (NoteEvent note in notes)
        {
            long end = note.Onset.Samples + note.Duration.Samples;
            if (PedalDownAt(changes, end))
            {
                long release = NextReleaseAtOrAfter(changes, end);
                if (release > end)
                {
                    end = release; // ring on until the pedal lifts
                }
            }

            long duration = System.Math.Max(1, end - note.Onset.Samples);
            result.Add(new NoteEvent(
                note.Pitch, note.Onset, new SampleDuration(duration, note.Duration.Rate), note.Velocity));
        }

        return result;
    }

    // The pedal's state at sample t is the value of the last change at or before t.
    private static bool PedalDownAt(Change[] changes, long t)
    {
        bool down = false;
        foreach (Change c in changes)
        {
            if (c.Sample <= t)
            {
                down = c.Down;
            }
            else
            {
                break;
            }
        }

        return down;
    }

    // The first pedal-up at or after t, or long.MinValue if the pedal never lifts again (so no extension).
    private static long NextReleaseAtOrAfter(Change[] changes, long t)
    {
        foreach (Change c in changes)
        {
            if (c.Sample >= t && !c.Down)
            {
                return c.Sample;
            }
        }

        return long.MinValue;
    }
}
