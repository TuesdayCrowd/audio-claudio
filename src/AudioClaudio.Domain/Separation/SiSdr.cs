using System;

namespace AudioClaudio.Domain.Separation;

/// <summary>
/// Scale-invariant signal-to-distortion ratio (SI-SDR), in dB — the standard source-separation
/// metric for "how well does a recovered stem match its ground-truth stem?", independent of the
/// estimate's overall gain. The reference is orthogonally projected to find the best scalar
/// multiple of itself (<c>alpha</c>) that explains the estimate; that projection is the
/// scale-invariant "target", and everything left over (<c>e_noise</c>) is distortion:
///
/// <code>
/// alpha    = dot(estimate, reference) / dot(reference, reference)
/// s_target = alpha * reference
/// e_noise  = estimate - s_target
/// SI-SDR   = 10 * log10( energy(s_target) / energy(e_noise) )
/// </code>
///
/// Because <c>alpha</c> scales linearly with the estimate, replacing <c>estimate</c> with
/// <c>k * estimate</c> for any non-zero <c>k</c> scales both <c>s_target</c> and <c>e_noise</c>
/// by the same <c>k</c>, so their energy ratio — and thus the score — is unchanged. This is the
/// metric's defining property, not an accident: an estimate that is a perfect scaled copy of the
/// reference has zero <c>e_noise</c> and scores <see cref="double.PositiveInfinity"/>.
///
/// Pure and deterministic: no I/O, no clock, no randomness. Energies are accumulated in
/// <see cref="double"/> even though the inputs are <see cref="float"/>, so long buffers don't
/// lose precision to single-precision summation.
/// </summary>
public static class SiSdr
{
    /// <summary>Scale-invariant SDR (dB) of <paramref name="estimate"/> against <paramref name="reference"/>.
    /// Returns +∞ for a perfect (or perfectly-scaled) match. Throws on length mismatch or a silent reference.</summary>
    public static double Compute(ReadOnlySpan<float> estimate, ReadOnlySpan<float> reference)
    {
        if (estimate.Length != reference.Length)
        {
            throw new ArgumentException(
                $"Estimate and reference must have equal length (estimate: {estimate.Length}, reference: {reference.Length}).",
                nameof(estimate));
        }

        double dotEstimateReference = 0.0;
        double dotReferenceReference = 0.0;
        for (int i = 0; i < reference.Length; i++)
        {
            dotEstimateReference += (double)estimate[i] * reference[i];
            dotReferenceReference += (double)reference[i] * reference[i];
        }

        // A silent reference makes alpha (and thus the whole decomposition) undefined. A
        // ground-truth stem is never silent in this project's use of the metric, so this is a
        // caller error, not a degenerate score to paper over.
        if (dotReferenceReference == 0.0)
        {
            throw new ArgumentException("Reference is silent (zero energy); SI-SDR is undefined.", nameof(reference));
        }

        double alpha = dotEstimateReference / dotReferenceReference;

        double targetEnergy = 0.0;
        double noiseEnergy = 0.0;
        for (int i = 0; i < reference.Length; i++)
        {
            double sTarget = alpha * reference[i];
            double eNoise = estimate[i] - sTarget;
            targetEnergy += sTarget * sTarget;
            noiseEnergy += eNoise * eNoise;
        }

        // A perfect (or perfectly-scaled) match leaves zero residual: the honest score is +∞,
        // not a very large finite number produced by dividing by a near-zero denominator.
        if (noiseEnergy == 0.0)
        {
            return double.PositiveInfinity;
        }

        return 10.0 * Math.Log10(targetEnergy / noiseEnergy);
    }
}
