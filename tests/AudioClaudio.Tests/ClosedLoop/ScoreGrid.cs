using System.Collections.Generic;
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>
/// Flattens the canonical Step 6 <see cref="Score"/> (Measures -> Elements) into the linear grid
/// sequence the comparer needs. There is no <c>Score.Notes</c>/<c>PlacedNote</c> in the domain, so
/// this LOCAL helper derives placed notes from <see cref="Score.Measures"/>: it walks measures in
/// order, accumulates a subdivision cursor, and merges any barline-tied segments (<c>TiedToNext</c>)
/// back into a single note. Step 6's grid uses one tick per subdivision
/// (<c>TicksPerBeat</c> == subdivisions-per-beat for the chosen <c>Subdivision</c>), so each
/// element's <c>LengthTicks</c> is already its length in subdivisions.
/// </summary>
public static class ScoreGrid
{
    public static IReadOnlyList<NoteGridPosition> From(Score score)
    {
        var notes = new List<NoteGridPosition>();

        int cursor = 0; // cumulative subdivision index across all measures
        bool havePending = false; // a tied note being accumulated across a barline
        Pitch pendingPitch = default;
        int pendingOnset = 0;
        int pendingDuration = 0;

        foreach (var measure in score.Measures)
        {
            foreach (var element in measure.Elements)
            {
                if (element.Kind == ElementKind.Note)
                {
                    if (!havePending)
                    {
                        havePending = true;
                        pendingPitch = element.Pitch!.Value;
                        pendingOnset = cursor;
                        pendingDuration = element.LengthTicks;
                    }
                    else
                    {
                        pendingDuration += element.LengthTicks; // continuation of a barline-split note
                    }

                    if (!element.TiedToNext)
                    {
                        notes.Add(new NoteGridPosition(pendingPitch, pendingOnset, pendingDuration));
                        havePending = false;
                    }
                }

                cursor += element.LengthTicks; // notes and rests both advance the grid cursor
            }
        }

        if (havePending) // dangling tie (malformed score) — emit what we have
        {
            notes.Add(new NoteGridPosition(pendingPitch, pendingOnset, pendingDuration));
        }

        return notes;
    }
}
