using System.Collections.Generic;
using System.Linq;

namespace AudioClaudio.Domain.Evaluation;

/// <summary>
/// Time-alignment for scoring a performance against a score. A recording (candidate) and an
/// engraved score (reference) live on different time bases — overall tempo plus rubato — so raw
/// onset matching measures drift, not pitch recovery. <see cref="GlobalScale"/> linearly rescales
/// the candidate's onset span onto the reference's, cancelling the gross global tempo difference;
/// <see cref="DtwWarp"/> goes further, cancelling *local* rubato with a monotonic dynamic-time-warp.
/// Pure and deterministic; the input is not mutated.
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

    /// <summary>
    /// Monotonic time-warp alignment (dynamic time warping). Where <see cref="GlobalScale"/> applies
    /// a single linear tempo ratio, a real performance drifts *locally* (rubato) — DTW finds a
    /// monotonic correspondence between the candidate's and reference's onset-time sequences and
    /// applies a piecewise-linear warp built from it, re-timing each candidate onset onto the
    /// reference's local tempo. Cost is onset-time distance after a global pre-scale (so the
    /// correspondence is found in a comparable range, not swamped by the gross ratio); the standard
    /// match/insert/delete recurrence with a fixed tie order (diagonal, then delete, then insert)
    /// keeps it reproducible. Pure; the input is not mutated. Returns the candidate unchanged when
    /// either side has fewer than two distinct onsets — there is nothing to warp against.
    /// </summary>
    public static IReadOnlyList<NoteEvent> DtwWarp(
        IReadOnlyList<NoteEvent> candidate, IReadOnlyList<NoteEvent> reference)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(reference);
        if (candidate.Count == 0)
        {
            return candidate;
        }

        double[] cand = DistinctOnsetSeconds(candidate);
        double[] refr = DistinctOnsetSeconds(reference);
        if (cand.Length < 2 || refr.Length < 2)
        {
            return candidate; // not enough structure to warp against
        }

        // Pre-scale the candidate onsets onto the reference span so the DTW cost is comparable, not
        // dominated by the gross tempo ratio. Anchors below use the ORIGINAL candidate times as x and
        // the matched reference times as y, so the warp maps original candidate time -> reference time.
        double candMin = cand[0], candMax = cand[^1];
        double refMin = refr[0], refMax = refr[^1];
        double candSpan = candMax - candMin;
        double preScale = candSpan > 0 ? (refMax - refMin) / candSpan : 1.0;
        var scaled = new double[cand.Length];
        for (int i = 0; i < cand.Length; i++)
        {
            scaled[i] = ((cand[i] - candMin) * preScale) + refMin;
        }

        List<(int I, int J)> path = DtwPath(scaled, refr);

        // Reduce the path to strictly-increasing anchors: each candidate onset maps to the mean of
        // the reference times it was matched to (a many-to-one step averages, keeping y monotonic).
        var anchorX = new List<double>();
        var anchorY = new List<double>();
        int p = 0;
        while (p < path.Count)
        {
            int ci = path[p].I;
            double sum = 0.0;
            int count = 0;
            while (p < path.Count && path[p].I == ci)
            {
                sum += refr[path[p].J];
                count++;
                p++;
            }

            anchorX.Add(cand[ci]);
            anchorY.Add(sum / count);
        }

        var warped = new List<NoteEvent>(candidate.Count);
        foreach (NoteEvent e in candidate)
        {
            double mapped = PiecewiseLinear(anchorX, anchorY, e.Onset.ToSeconds(), out double slope);
            long onset = System.Math.Max(0,
                (long)System.Math.Round(mapped * e.Onset.Rate.Hz, System.MidpointRounding.AwayFromZero));
            double durScale = slope > 0.0 ? slope : 1.0;
            long duration = System.Math.Max(1,
                (long)System.Math.Round(e.Duration.Samples * durScale, System.MidpointRounding.AwayFromZero));

            warped.Add(new NoteEvent(
                e.Pitch,
                new SamplePosition(onset, e.Onset.Rate),
                new SampleDuration(duration, e.Duration.Rate),
                e.Velocity));
        }

        return warped;
    }

    // Sorted, de-duplicated onset times (seconds). Chords collapse to one onset, so the warp is about
    // time, not note count.
    private static double[] DistinctOnsetSeconds(IReadOnlyList<NoteEvent> notes)
    {
        var set = new SortedSet<double>();
        foreach (NoteEvent e in notes)
        {
            set.Add(e.Onset.ToSeconds());
        }

        return set.ToArray();
    }

    // Standard DTW: full |a_i - b_j| cost matrix, monotonic path from (0,0) to (m-1,n-1). Backtrack
    // uses a fixed tie order (diagonal, then up/delete, then left/insert) so the path is deterministic.
    private static List<(int I, int J)> DtwPath(double[] a, double[] b)
    {
        int m = a.Length, n = b.Length;
        var d = new double[m, n];
        d[0, 0] = System.Math.Abs(a[0] - b[0]);
        for (int i = 1; i < m; i++)
        {
            d[i, 0] = d[i - 1, 0] + System.Math.Abs(a[i] - b[0]);
        }

        for (int j = 1; j < n; j++)
        {
            d[0, j] = d[0, j - 1] + System.Math.Abs(a[0] - b[j]);
        }

        for (int i = 1; i < m; i++)
        {
            for (int j = 1; j < n; j++)
            {
                double best = d[i - 1, j - 1];
                if (d[i - 1, j] < best)
                {
                    best = d[i - 1, j];
                }

                if (d[i, j - 1] < best)
                {
                    best = d[i, j - 1];
                }

                d[i, j] = System.Math.Abs(a[i] - b[j]) + best;
            }
        }

        var path = new List<(int, int)>();
        int ii = m - 1, jj = n - 1;
        path.Add((ii, jj));
        while (ii > 0 || jj > 0)
        {
            if (ii == 0)
            {
                jj--;
            }
            else if (jj == 0)
            {
                ii--;
            }
            else
            {
                double diag = d[ii - 1, jj - 1];
                double up = d[ii - 1, jj];
                double left = d[ii, jj - 1];
                if (diag <= up && diag <= left)
                {
                    ii--;
                    jj--;
                }
                else if (up <= left)
                {
                    ii--;
                }
                else
                {
                    jj--;
                }
            }

            path.Add((ii, jj));
        }

        path.Reverse();
        return path;
    }

    // Linear interpolation through (xs, ys) anchors (xs strictly increasing); outside the range,
    // extrapolate along the nearest end segment. `slope` is the local dy/dx of the used segment.
    private static double PiecewiseLinear(List<double> xs, List<double> ys, double x, out double slope)
    {
        int last = xs.Count - 1;
        if (last <= 0)
        {
            slope = 1.0;
            return xs.Count == 1 ? ys[0] : x;
        }

        int k;
        if (x <= xs[0])
        {
            k = 0;
        }
        else if (x >= xs[last])
        {
            k = last - 1;
        }
        else
        {
            k = 0;
            while (k < last - 1 && x > xs[k + 1])
            {
                k++;
            }
        }

        double x0 = xs[k], x1 = xs[k + 1], y0 = ys[k], y1 = ys[k + 1];
        slope = (x1 - x0) != 0.0 ? (y1 - y0) / (x1 - x0) : 0.0;
        return y0 + ((x - x0) * slope);
    }
}
