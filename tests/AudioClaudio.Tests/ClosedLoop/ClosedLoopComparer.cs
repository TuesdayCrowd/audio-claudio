using System;
using System.Collections.Generic;

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>Outcome of one score comparison — match, or the first divergence found.</summary>
public readonly record struct ClosedLoopComparison(bool IsMatch, string? Detail)
{
    public static ClosedLoopComparison Match => new(true, null);

    public static ClosedLoopComparison Fail(string detail) => new(false, detail);
}

/// <summary>Encodes R9.2: count exact, pitch exact, onset/duration within +/-1 subdivision.</summary>
public static class ClosedLoopComparer
{
    public static ClosedLoopComparison Compare(
        IReadOnlyList<NoteGridPosition> expected,
        IReadOnlyList<NoteGridPosition> actual,
        int subdivisionTolerance = 1)
    {
        if (expected.Count != actual.Count)
        {
            return ClosedLoopComparison.Fail($"note count {actual.Count} != expected {expected.Count}");
        }

        for (int i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var a = actual[i];

            if (e.Pitch.MidiNumber != a.Pitch.MidiNumber)
            {
                return ClosedLoopComparison.Fail(
                    $"note {i}: pitch {a.Pitch.MidiNumber} != expected {e.Pitch.MidiNumber}");
            }

            if (Math.Abs(e.OnsetSubdivisions - a.OnsetSubdivisions) > subdivisionTolerance)
            {
                return ClosedLoopComparison.Fail(
                    $"note {i}: onset {a.OnsetSubdivisions} not within {subdivisionTolerance} of {e.OnsetSubdivisions}");
            }

            if (Math.Abs(e.DurationSubdivisions - a.DurationSubdivisions) > subdivisionTolerance)
            {
                return ClosedLoopComparison.Fail(
                    $"note {i}: duration {a.DurationSubdivisions} not within {subdivisionTolerance} of {e.DurationSubdivisions}");
            }
        }

        return ClosedLoopComparison.Match;
    }

    /// <summary>
    /// Count exact, pitch exact, onset within +/-1 subdivision — but NOT duration. Used by the
    /// full-range (whole-keyboard) closed-loop test, where the highest pitches cannot sustain an
    /// audible eighth (so their duration is not a claim the audio can support), but their note
    /// count, pitch, and onset still must be recovered exactly. Offset refinement only ever
    /// changes durations, so this is a genuine end-to-end check of the rest of the pipeline across
    /// the full MIDI 33-96 range.
    /// </summary>
    public static ClosedLoopComparison CompareCountPitchOnset(
        IReadOnlyList<NoteGridPosition> expected,
        IReadOnlyList<NoteGridPosition> actual,
        int subdivisionTolerance = 1)
    {
        if (expected.Count != actual.Count)
        {
            return ClosedLoopComparison.Fail($"note count {actual.Count} != expected {expected.Count}");
        }

        for (int i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var a = actual[i];

            if (e.Pitch.MidiNumber != a.Pitch.MidiNumber)
            {
                return ClosedLoopComparison.Fail(
                    $"note {i}: pitch {a.Pitch.MidiNumber} != expected {e.Pitch.MidiNumber}");
            }

            if (Math.Abs(e.OnsetSubdivisions - a.OnsetSubdivisions) > subdivisionTolerance)
            {
                return ClosedLoopComparison.Fail(
                    $"note {i}: onset {a.OnsetSubdivisions} not within {subdivisionTolerance} of {e.OnsetSubdivisions}");
            }
        }

        return ClosedLoopComparison.Match;
    }
}
