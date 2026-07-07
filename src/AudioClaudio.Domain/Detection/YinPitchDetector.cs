using System;

namespace AudioClaudio.Domain;

/// <summary>
/// Monophonic fundamental-frequency estimator via the YIN algorithm
/// (de Cheveigné &amp; Kawahara, 2002): the difference function, its
/// cumulative-mean normalization, an absolute threshold with the smallest-lag
/// rule, and parabolic interpolation. Pure and per-frame (R4.4); deterministic
/// (non-negotiable 3). Works in the lag/period domain, so piano partials do not
/// pull the estimate to an overtone (R4.3).
/// </summary>
public static class YinPitchDetector
{
    /// <summary>Detect with the default options.</summary>
    public static PitchEstimate Detect(Frame frame) => Detect(frame, YinOptions.Default);

    /// <summary>
    /// Estimate the fundamental of one frame, or report it unvoiced. Identical
    /// input yields an identical estimate on every run and machine.
    /// </summary>
    public static PitchEstimate Detect(Frame frame, YinOptions options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);

        float[] x = frame.Samples;            // mono PCM in [-1, 1]  (single Frame-shape coupling)
        int n = x.Length;
        int rate = frame.Rate.Hz;

        // Split the frame: integration window W and lag search each get half, so
        // the deepest access x[j + tau] with j < W and tau <= N/2 stays in bounds.
        int w = n / 2;
        int tauCeiling = n / 2;

        // A high frequency is a short period (small lag); a low frequency a long one.
        int tauMin = Math.Max(1, (int)Math.Floor(rate / options.MaxFrequencyHz));
        int tauMax = Math.Min(tauCeiling, (int)Math.Ceiling(rate / options.MinFrequencyHz));

        if (w <= 0 || tauMin >= tauMax)
            return PitchEstimate.Unvoiced;

        // (1) Difference function d(tau), computed over the FULL range 1..tauMax so
        //     the cumulative mean in step (2) is exact; the search is narrowed later.
        double[] d = new double[tauMax + 1];
        for (int tau = 1; tau <= tauMax; tau++)
        {
            double sum = 0.0;
            for (int j = 0; j < w; j++)
            {
                double diff = x[j] - x[j + tau];
                sum += diff * diff;
            }
            d[tau] = sum;
        }

        // (2) Cumulative-mean-normalized difference d'(tau). d'(0) = 1 by convention;
        //     an all-zero (silent) frame stays at 1 everywhere and reads unvoiced.
        double[] dPrime = new double[tauMax + 1];
        dPrime[0] = 1.0;
        double runningSum = 0.0;
        for (int tau = 1; tau <= tauMax; tau++)
        {
            runningSum += d[tau];
            dPrime[tau] = runningSum > 0.0 ? d[tau] * tau / runningSum : 1.0;
        }

        // (3) Absolute threshold + smallest-lag rule. Within the plausible window,
        //     take the first lag whose d' dips below the threshold, then descend to
        //     the bottom of that dip. Smallest qualifying lag => the fundamental,
        //     not a multiple of it (this is what avoids octave errors).
        int tauStar = -1;
        for (int tau = tauMin; tau <= tauMax; tau++)
        {
            if (dPrime[tau] < options.Threshold)
            {
                while (tau + 1 <= tauMax && dPrime[tau + 1] < dPrime[tau])
                    tau++;
                tauStar = tau;
                break;
            }
        }

        if (tauStar == -1)
            return PitchEstimate.Unvoiced;   // no periodic dip => silence or noise (R4.1)

        // (4) Parabolic interpolation for sub-sample precision — essential at the
        //     top of the range where one whole sample of lag is several cents.
        double refinedTau = ParabolicMinimum(dPrime, tauStar, tauMin, tauMax);

        double f0 = rate / refinedTau;
        double confidence = Math.Clamp(1.0 - dPrime[tauStar], 0.0, 1.0);
        return PitchEstimate.Voiced(f0, confidence);
    }

    /// <summary>
    /// Refine an integer lag minimum to sub-sample precision by fitting a parabola
    /// through d' at (tau-1, tau, tau+1) and returning its vertex. Falls back to the
    /// integer lag at the search edges or a degenerate (flat/divergent) fit.
    /// </summary>
    private static double ParabolicMinimum(double[] dPrime, int tau, int tauMin, int tauMax)
    {
        if (tau <= tauMin || tau >= tauMax)
            return tau;

        double s0 = dPrime[tau - 1];
        double s1 = dPrime[tau];
        double s2 = dPrime[tau + 1];

        double denom = s0 + s2 - 2.0 * s1;
        if (denom == 0.0)
            return tau;                       // flat: no better estimate than the integer lag

        double delta = 0.5 * (s0 - s2) / denom;
        if (delta is < -1.0 or > 1.0)
            return tau;                       // divergent fit: keep the integer lag

        return tau + delta;
    }
}
