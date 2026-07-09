using System.Collections.Generic;
using System.Linq;

namespace AudioClaudio.Domain.Evaluation;

/// <summary>
/// Time-alignment for scoring a performance against a score. A recording (candidate) and an
/// engraved score (reference) live on different time bases — overall tempo plus rubato — so raw
/// onset matching measures drift, not pitch recovery. <see cref="GlobalScale"/> linearly rescales
/// the candidate's onset span onto the reference's, cancelling the gross global tempo difference.
/// This is a deliberate first-order alignment; a monotonic DTW warp (handling rubato) is the
/// documented refinement. Pure and deterministic; the input is not mutated.
/// </summary>
public static class OnsetAlignment
{
    public static IReadOnlyList<NoteEvent> GlobalScale(
        IReadOnlyList<NoteEvent> candidate, IReadOnlyList<NoteEvent> reference)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(reference);
        if (candidate.Count == 0)
        {
            return candidate;
        }

        long candidateMin = candidate.Min(e => e.Onset.Samples);
        long candidateMax = candidate.Max(e => e.Onset.Samples);
        long referenceMin = reference.Count > 0 ? reference.Min(e => e.Onset.Samples) : candidateMin;
        long referenceMax = reference.Count > 0 ? reference.Max(e => e.Onset.Samples) : candidateMax;

        long candidateSpan = candidateMax - candidateMin;
        long referenceSpan = referenceMax - referenceMin;
        double scale = candidateSpan > 0 ? (double)referenceSpan / candidateSpan : 1.0;

        var aligned = new List<NoteEvent>(candidate.Count);
        foreach (NoteEvent e in candidate)
        {
            long onset = (long)System.Math.Round(
                ((e.Onset.Samples - candidateMin) * scale) + referenceMin, System.MidpointRounding.AwayFromZero);
            if (onset < 0)
            {
                onset = 0;
            }

            long duration = System.Math.Max(1,
                (long)System.Math.Round(e.Duration.Samples * scale, System.MidpointRounding.AwayFromZero));

            aligned.Add(new NoteEvent(
                e.Pitch,
                new SamplePosition(onset, e.Onset.Rate),
                new SampleDuration(duration, e.Duration.Rate),
                e.Velocity));
        }

        return aligned;
    }
}
