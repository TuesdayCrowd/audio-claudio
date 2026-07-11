using System;
using System.Collections.Generic;

namespace AudioClaudio.Domain;

/// <summary>A pitch candidate for a frame: a fundamental estimate and its aperiodicity (the
/// cumulative-mean-normalized difference d' at its lag — lower is more periodic). pYIN-lite (v2 Stage 2)
/// exposes these so a caller can reason about the runner-up periods YIN discarded.</summary>
public readonly record struct PitchCandidate(double FrequencyHz, double Aperiodicity);

/// <summary>
/// Monophonic fundamental-frequency estimator via the YIN algorithm
/// (de Cheveigné &amp; Kawahara, 2002): the difference function, its
/// cumulative-mean normalization, an absolute threshold with the smallest-lag
/// rule, and parabolic interpolation. Pure and per-frame (R4.4); deterministic
/// (non-negotiable 3). Works in the lag/period domain, so piano partials do not
/// pull the estimate to an overtone (R4.3).
///
/// <b>pYIN-lite (v2 Stage 2).</b> When the caller passes the <i>previous</i> frame's estimate, a causal
/// continuity check corrects YIN's rare octave error: if this frame's estimate jumps ~a full octave from
/// the previous pitch AND a runner-up candidate sits right at the previous pitch and is periodic enough,
/// that continuity candidate wins. It is a strict no-op without a previous estimate (so the stateless
/// property tests are unchanged) and on clean, stable signals (where YIN does not jump octaves), which is
/// why the closed loop stays green — the correction only fires on the ambiguous frames YIN gets wrong.
/// </summary>
public static class YinPitchDetector
{
    /// <summary>Detect with the default options (stateless, no continuity).</summary>
    public static PitchEstimate Detect(Frame frame) => Detect(frame, YinOptions.Default);

    /// <summary>
    /// Estimate the fundamental of one frame, or report it unvoiced. Identical input (and identical
    /// <paramref name="previous"/>) yields an identical estimate on every run and machine. When
    /// <paramref name="previous"/> is a voiced estimate, the pYIN-lite continuity correction may override
    /// a YIN octave error (see the type remarks); with no previous it is exactly YIN.
    /// </summary>
    public static PitchEstimate Detect(Frame frame, YinOptions options, PitchEstimate? previous = null)
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
        var yinPick = PitchEstimate.Voiced(f0, confidence);

        // (5) pYIN-lite: with a previous voiced estimate, let causal continuity correct an octave error.
        if (previous is { IsVoiced: true } prev)
        {
            IReadOnlyList<PitchCandidate> candidates = CollectCandidates(dPrime, rate, tauMin, tauMax, options.ContinuityThreshold);
            return ApplyContinuity(yinPick, prev, candidates, options);
        }

        return yinPick;
    }

    /// <summary>
    /// The pYIN-lite continuity correction, as a pure function (so it is directly testable): if
    /// <paramref name="yinPick"/> jumped within <see cref="YinOptions.OctaveContinuityCents"/> of a full
    /// octave from <paramref name="previous"/>, and a <paramref name="candidates"/> pitch sits within
    /// <see cref="YinOptions.ContinuityMatchCents"/> of the previous pitch with aperiodicity below
    /// <see cref="YinOptions.ContinuityThreshold"/>, that continuity candidate wins; otherwise YIN's pick
    /// stands. A near-octave jump with a corroborated near-previous candidate is the octave-error
    /// signature. Deterministic; never fabricates a pitch (it only ever returns YIN's pick or a real
    /// candidate).
    /// </summary>
    public static PitchEstimate ApplyContinuity(
        PitchEstimate yinPick, PitchEstimate previous, IReadOnlyList<PitchCandidate> candidates, YinOptions options)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(options);

        if (!yinPick.IsVoiced || !previous.IsVoiced)
            return yinPick;

        double jumpCents = PitchMath.CentsBetween(previous.FrequencyHz, yinPick.FrequencyHz);
        if (Math.Abs(Math.Abs(jumpCents) - 1200.0) > options.OctaveContinuityCents)
            return yinPick;   // not a near-octave jump → trust YIN

        PitchCandidate? nearest = null;
        double nearestCents = double.MaxValue;
        foreach (PitchCandidate c in candidates)
        {
            double cents = Math.Abs(PitchMath.CentsBetween(previous.FrequencyHz, c.FrequencyHz));
            if (cents < nearestCents)
            {
                nearestCents = cents;
                nearest = c;
            }
        }

        if (nearest is { } cand
            && nearestCents <= options.ContinuityMatchCents
            && cand.Aperiodicity < options.ContinuityThreshold
            && cand.Aperiodicity < 1.0 - yinPick.Confidence)
        {
            // The continuity candidate wins only when it is MORE periodic (a deeper dip) than YIN's pick.
            // This is what tells a within-note octave ERROR (the true pitch's dip is deeper than the
            // sub-harmonic artifact YIN latched onto) from a real octave LEAP where the previous note is
            // still ringing through a short rest (there the NEW note is the deeper dip and the fading old
            // note is shallower — so it is not overridden). Without it, a genuine octave leap next to a
            // ringing low note is wrongly pulled back an octave.
            return PitchEstimate.Voiced(cand.FrequencyHz, Math.Clamp(1.0 - cand.Aperiodicity, 0.0, 1.0));
        }

        return yinPick;
    }

    /// <summary>All local minima of d' in the lag window whose aperiodicity is below
    /// <paramref name="maxAperiodicity"/>, parabolic-refined — the candidate periods YIN chose among.
    /// First-of-plateau wins (matches the smallest-lag tie-break), so it is deterministic.</summary>
    private static IReadOnlyList<PitchCandidate> CollectCandidates(
        double[] dPrime, int rate, int tauMin, int tauMax, double maxAperiodicity)
    {
        var candidates = new List<PitchCandidate>();
        for (int tau = tauMin; tau <= tauMax; tau++)
        {
            if (dPrime[tau] >= maxAperiodicity)
                continue;

            bool leftOk = tau == tauMin || dPrime[tau] <= dPrime[tau - 1];
            bool rightOk = tau == tauMax || dPrime[tau] < dPrime[tau + 1];
            if (leftOk && rightOk)
            {
                double refined = ParabolicMinimum(dPrime, tau, tauMin, tauMax);
                candidates.Add(new PitchCandidate(rate / refined, dPrime[tau]));
            }
        }

        return candidates;
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
