using System.Collections.Generic;

namespace AudioClaudio.Domain.Evaluation;

/// <summary>
/// Note-level precision/recall/F1 between a candidate transcription and a reference note-set —
/// the project's audio-side trial balance: "did we recover the notes of the score?".
///
/// A candidate note matches a reference note when their pitch is identical (MIDI number equal —
/// an octave error is a wrong note, never a partial credit) and their onsets fall within
/// <see cref="NoteMatchOptions.OnsetToleranceSeconds"/>. Matching is <b>one-to-one</b>: each
/// reference note claims at most one candidate and vice versa, so duplicated candidates cannot
/// inflate the score. The pairing is a deterministic greedy: references are considered in
/// ascending (onset, pitch) order and each takes the nearest-onset unclaimed candidate of equal
/// pitch within tolerance (ties broken by earliest onset, then input order) — no randomness, no
/// clock, same inputs → same result (non-negotiable 3).
/// </summary>
public static class TranscriptionEvaluator
{
    public static NoteSetEvaluation Evaluate(
        IReadOnlyList<NoteEvent> candidate,
        IReadOnlyList<NoteEvent> reference,
        NoteMatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(options);

        // Stable deterministic order: onset (seconds), then pitch, then original index.
        int[] refOrder = OrderedIndices(reference);
        int[] candOrder = OrderedIndices(candidate);
        bool[] candUsed = new bool[candidate.Count];

        int truePositives = 0;
        foreach (int ri in refOrder)
        {
            NoteEvent r = reference[ri];
            double refOnset = r.Onset.ToSeconds();
            int bestCand = -1;
            double bestDelta = double.PositiveInfinity;

            foreach (int ci in candOrder)
            {
                if (candUsed[ci])
                {
                    continue;
                }

                NoteEvent c = candidate[ci];
                if (c.Pitch.MidiNumber != r.Pitch.MidiNumber)
                {
                    continue;
                }

                double delta = System.Math.Abs(c.Onset.ToSeconds() - refOnset);
                if (delta <= options.OnsetToleranceSeconds && delta < bestDelta)
                {
                    bestDelta = delta;
                    bestCand = ci;
                }
            }

            if (bestCand >= 0)
            {
                candUsed[bestCand] = true;
                truePositives++;
            }
        }

        return new NoteSetEvaluation(
            TruePositives: truePositives,
            FalsePositives: candidate.Count - truePositives,
            FalseNegatives: reference.Count - truePositives,
            ReferenceCount: reference.Count,
            CandidateCount: candidate.Count);
    }

    // Indices of notes sorted by (onset seconds, MIDI pitch, original index) — a total,
    // deterministic order so the greedy pairing never depends on incoming list order.
    private static int[] OrderedIndices(IReadOnlyList<NoteEvent> notes)
    {
        var order = new int[notes.Count];
        for (int i = 0; i < notes.Count; i++)
        {
            order[i] = i;
        }

        System.Array.Sort(order, (a, b) =>
        {
            int byOnset = notes[a].Onset.ToSeconds().CompareTo(notes[b].Onset.ToSeconds());
            if (byOnset != 0)
            {
                return byOnset;
            }

            int byPitch = notes[a].Pitch.MidiNumber.CompareTo(notes[b].Pitch.MidiNumber);
            return byPitch != 0 ? byPitch : a.CompareTo(b);
        });
        return order;
    }
}
