using System;
using System.Collections.Generic;

namespace AudioClaudio.Infrastructure.Separation;

/// <summary>
/// The two non-learned Spleeter reconstruction stages that are deliberately lifted OUT of the ONNX
/// graph into C# (MODEL_CARD.md): (1) <see cref="Softmax"/> — the cross-branch softmax over the 5
/// stems' raw logits, elementwise, x the mixture magnitude, giving 5 "estimated" magnitudes
/// (Spleeter's <c>{stem}_spectrogram/mul</c> — exactly what <c>golden/masked_{stem}_nhwc.f32</c>
/// validates); (2) <see cref="RatioMask"/> — the power-ratio remask (<c>separation_exponent=2</c>,
/// <c>EPSILON=1e-10</c>) that turns those estimates into per-stem ratio masks for reconstruction.
/// Pure, stateless, and internal: exercised directly by <see cref="SpleeterSourceSeparator"/> and by
/// the golden-parity tests (this assembly grants <c>InternalsVisibleTo</c> to AudioClaudio.Tests).
/// </summary>
internal static class SpleeterMasking
{
    /// <summary>Spleeter's <c>separation_exponent</c>: the ratio mask is built from squared
    /// (exponent 2) estimated magnitudes, not the magnitudes themselves.</summary>
    internal const double SeparationExponent = 2.0;

    /// <summary>Spleeter's <c>EPSILON</c>: a small additive constant that keeps the ratio mask
    /// finite when every stem's estimate is exactly zero at a bin.</summary>
    internal const double Epsilon = 1e-10;

    /// <summary>
    /// Cross-branch softmax over <paramref name="logits"/> (one <c>[num_splits, T, F, C]</c> array
    /// per stem, in stem order), elementwise x <paramref name="magnitude"/> (the same
    /// <c>[num_splits, T, F, C]</c> shape). Uses the max-subtraction trick for numerical stability.
    /// Returns one estimated-magnitude array per stem, in the same order as <paramref name="logits"/>;
    /// by construction they sum to exactly <paramref name="magnitude"/> at every pixel (the masks sum
    /// to 1).
    /// </summary>
    internal static IReadOnlyList<double[,,,]> Softmax(IReadOnlyList<float[,,,]> logits, double[,,,] magnitude)
    {
        ArgumentNullException.ThrowIfNull(logits);
        ArgumentNullException.ThrowIfNull(magnitude);
        int stemCount = logits.Count;
        int numSplits = magnitude.GetLength(0);
        int t = magnitude.GetLength(1);
        int f = magnitude.GetLength(2);
        int channels = magnitude.GetLength(3);

        var estimated = new double[stemCount][,,,];
        for (int k = 0; k < stemCount; k++) estimated[k] = new double[numSplits, t, f, channels];

        var expVals = new double[stemCount]; // reused per pixel -- avoids a per-pixel allocation
        for (int s = 0; s < numSplits; s++)
        {
            for (int ti = 0; ti < t; ti++)
            {
                for (int fi = 0; fi < f; fi++)
                {
                    for (int ch = 0; ch < channels; ch++)
                    {
                        double max = double.NegativeInfinity;
                        for (int k = 0; k < stemCount; k++)
                        {
                            double v = logits[k][s, ti, fi, ch];
                            if (v > max) max = v;
                        }

                        double sumExp = 0.0;
                        for (int k = 0; k < stemCount; k++)
                        {
                            double e = Math.Exp(logits[k][s, ti, fi, ch] - max);
                            expVals[k] = e;
                            sumExp += e;
                        }

                        double mag = magnitude[s, ti, fi, ch];
                        for (int k = 0; k < stemCount; k++)
                        {
                            estimated[k][s, ti, fi, ch] = (expVals[k] / sumExp) * mag;
                        }
                    }
                }
            }
        }

        return estimated;
    }

    /// <summary>
    /// Spleeter's <c>_build_masks</c> power-ratio remask: <c>ratio_i = (estimated_i^2 + EPSILON/N) /
    /// (sum_j estimated_j^2 + EPSILON)</c> where N is the stem count. By construction the ratios sum
    /// to exactly 1 across stems at every pixel.
    /// </summary>
    internal static IReadOnlyList<double[,,,]> RatioMask(IReadOnlyList<double[,,,]> estimated)
    {
        ArgumentNullException.ThrowIfNull(estimated);
        int n = estimated.Count;
        int numSplits = estimated[0].GetLength(0);
        int t = estimated[0].GetLength(1);
        int f = estimated[0].GetLength(2);
        int channels = estimated[0].GetLength(3);

        var ratio = new double[n][,,,];
        for (int k = 0; k < n; k++) ratio[k] = new double[numSplits, t, f, channels];

        var pow = new double[n]; // reused per pixel -- avoids a per-pixel allocation
        for (int s = 0; s < numSplits; s++)
        {
            for (int ti = 0; ti < t; ti++)
            {
                for (int fi = 0; fi < f; fi++)
                {
                    for (int ch = 0; ch < channels; ch++)
                    {
                        double sumPow = 0.0;
                        for (int k = 0; k < n; k++)
                        {
                            double p = Math.Pow(estimated[k][s, ti, fi, ch], SeparationExponent);
                            pow[k] = p;
                            sumPow += p;
                        }

                        double denom = sumPow + Epsilon;
                        for (int k = 0; k < n; k++)
                        {
                            ratio[k][s, ti, fi, ch] = (pow[k] + (Epsilon / n)) / denom;
                        }
                    }
                }
            }
        }

        return ratio;
    }
}
