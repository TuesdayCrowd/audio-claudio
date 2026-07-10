using System.Collections.Generic;

namespace AudioClaudio.Domain.Evaluation;

/// <summary>
/// How acoustically alike two chromagrams are, in [0, 1]: the mean per-frame cosine similarity, maximized
/// over a bounded frame offset so a constant latency between the two recordings (e.g. the transcriber's
/// onset lag) doesn't depress the score. Because chroma vectors are L2-normalized, per-frame cosine is
/// just their dot product; frames where both recordings are silent are skipped. This is the objective
/// companion to a listening test: "does the re-synthesis carry the same pitch content over time as the
/// original?". Pure and deterministic.
/// </summary>
public static class ChromaSimilarity
{
    public const int DefaultMaxOffsetFrames = 20;

    public static double Compare(
        IReadOnlyList<double[]> a, IReadOnlyList<double[]> b, int maxOffsetFrames = DefaultMaxOffsetFrames)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        double best = 0.0;
        for (int offset = -maxOffsetFrames; offset <= maxOffsetFrames; offset++)
        {
            double mean = MeanCosine(a, b, offset);
            if (mean > best)
            {
                best = mean;
            }
        }

        return best;
    }

    private static double MeanCosine(IReadOnlyList<double[]> a, IReadOnlyList<double[]> b, int offset)
    {
        double sum = 0.0;
        int count = 0;
        for (int t = 0; t < a.Count; t++)
        {
            int u = t + offset;
            if (u < 0 || u >= b.Count)
            {
                continue;
            }

            double[] va = a[t];
            double[] vb = b[u];
            double dot = 0.0;
            double na = 0.0;
            double nb = 0.0;
            for (int i = 0; i < 12; i++)
            {
                dot += va[i] * vb[i];
                na += va[i] * va[i];
                nb += vb[i] * vb[i];
            }

            if (na == 0.0 && nb == 0.0)
            {
                continue; // both frames silent — a trivial match, skip so it neither helps nor hurts
            }

            sum += dot; // vectors are L2-normalized, so the dot IS the cosine (0 if exactly one is silent)
            count++;
        }

        return count == 0 ? 0.0 : sum / count;
    }
}
