using System.Collections.Generic;

namespace AudioClaudio.Domain;

/// <summary>
/// The Transkun semi-CRF Viterbi decode in C# (v2 Stage 4c): the exported score matrix <c>S</c> →
/// per-track note intervals. A faithful port of Yujia Yan's <c>viterbiBackward</c> (the inference path),
/// which is not ONNX-exportable (data-dependent backtracking). Pure, deterministic, BCL-only (Domain).
///
/// <c>S[e, b, k]</c> scores a note on track <c>k</c> spanning closed frame interval <c>[b, e]</c> (the
/// diagonal <c>e==b</c> is a single-frame note); a positive score means "include it". The "no event"
/// (skip) score is provably zero for this model (<c>S_skip ≡ 0</c>), so it is baked in as zero.
/// <see cref="Decode"/> returns, per track, the decoded closed intervals in time order.
/// </summary>
public static class SemiCrfViterbi
{
    /// <summary>A decoded closed frame interval <c>[Begin, End]</c> (both inclusive; a singleton is
    /// <c>Begin==End</c>).</summary>
    public readonly record struct Interval(int Begin, int End);

    /// <summary>
    /// Decode <paramref name="score"/> — a flat <c>[T, T, nBatch]</c> row-major matrix where
    /// <c>score[e,b,k] = score[(e*T + b)*nBatch + k]</c> (the exported <c>S</c>'s layout) — into one
    /// interval list per track. <paramref name="forcedStartPos"/> (per track, used by segment stitching)
    /// forces decoding to resume at a frame; null starts every track at 0.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Interval>> Decode(
        float[] score, int t, int nBatch, IReadOnlyList<int>? forcedStartPos = null)
    {
        System.ArgumentNullException.ThrowIfNull(score);
        if (t <= 0)
        {
            throw new System.ArgumentOutOfRangeException(nameof(t), t, "T must be positive.");
        }

        if (score.Length != (long)t * t * nBatch)
        {
            throw new System.ArgumentException(
                $"score has {score.Length} elements, expected {(long)t * t * nBatch} for [{t},{t},{nBatch}].", nameof(score));
        }

        // score[e,b,k]
        float At(int e, int b, int k) => score[(e * t + b) * nBatch + k];

        // Backward DP: q[frame,k] is the best score of the suffix starting at `frame`; ptr records, per
        // frame and track, whether to skip (−1) or the selection index of the interval that starts there.
        var q = new float[t * nBatch];
        var ptr = new int[(t - 1) * nBatch < 0 ? 0 : (t - 1) * nBatch]; // ptr[i-1, k]; empty when T==1

        for (int k = 0; k < nBatch; k++)
        {
            float d = At(t - 1, t - 1, k);
            q[(t - 1) * nBatch + k] = d > 0f ? d : 0f;
        }

        for (int i = 1; i < t; i++)
        {
            int begin = t - i - 1;
            for (int k = 0; k < nBatch; k++)
            {
                float best = q[(t - i) * nBatch + k]; // skip (selection 0 → ptr −1); noiseScore ≡ 0
                int sel = 0;
                for (int e = t - i; e < t; e++)
                {
                    float cand = q[e * nBatch + k] + At(e, begin, k);
                    int s = e - (t - i) + 1; // 1..i
                    if (cand > best)
                    {
                        best = cand;
                        sel = s;
                    }
                }

                ptr[(i - 1) * nBatch + k] = sel - 1;
                float diag = At(begin, begin, k);
                q[begin * nBatch + k] = best + (diag > 0f ? diag : 0f);
            }
        }

        var result = new List<IReadOnlyList<Interval>>(nBatch);
        for (int k = 0; k < nBatch; k++)
        {
            int j = forcedStartPos is null ? 0 : forcedStartPos[k];
            var intervals = new List<Interval>();
            while (j < t - 1)
            {
                int curSel = ptr[(t - j - 2) * nBatch + k]; // ptr for frame j
                if (At(j, j, k) > 0f)
                {
                    intervals.Add(new Interval(j, j)); // a singleton at j
                }

                if (curSel < 0)
                {
                    j += 1; // skip
                }
                else
                {
                    int e = curSel + j + 1;
                    intervals.Add(new Interval(j, e));
                    j = e;
                }
            }

            if (At(t - 1, t - 1, k) > 0f)
            {
                intervals.Add(new Interval(t - 1, t - 1));
            }

            result.Add(intervals);
        }

        return result;
    }
}
