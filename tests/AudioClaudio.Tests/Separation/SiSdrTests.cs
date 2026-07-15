using System;
using AudioClaudio.Domain.Separation;
using Xunit;

namespace AudioClaudio.Tests.Separation;

/// <summary>
/// SI-SDR (scale-invariant signal-to-distortion ratio): the yardstick for "how well did the
/// recovered stem match the ground-truth stem?", independent of the estimate's overall gain — a
/// perfectly-recovered stem played back 3x too loud is still a perfect recovery. The headline
/// property is the scale invariance itself (the whole reason the metric projects onto the
/// reference before scoring, rather than comparing samples directly).
/// </summary>
public class SiSdrTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void IdenticalSignals_ScoresPositiveInfinity()
    {
        float[] reference = [0.1f, -0.4f, 0.9f, -0.2f, 0.05f];
        float[] estimate = [0.1f, -0.4f, 0.9f, -0.2f, 0.05f];

        double db = SiSdr.Compute(estimate, reference);

        Assert.Equal(double.PositiveInfinity, db);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void PerfectlyScaledEstimate_AlsoScoresPositiveInfinity()
    {
        // Values are powers of two so that scaling by 3 is bit-exact in float32 (no rounding
        // noise sneaking a finite-but-huge dB value in where the math says the residual is zero).
        float[] reference = [0.25f, -0.5f, 0.125f, -0.0625f, 1.0f];
        float[] estimate = Scale(reference, 3.0f); // derived, not re-typed, so it is exactly a scaled copy

        double db = SiSdr.Compute(estimate, reference);

        Assert.Equal(double.PositiveInfinity, db);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void ScalingTheEstimate_LeavesTheScoreUnchanged()
    {
        // A genuinely noisy (non-perfectly-scaled) estimate: SI-SDR's whole point is that the
        // score for k*estimate is identical for any non-zero k, because the projection cancels
        // the scale before distortion is measured.
        float[] reference = [1f, 2f, -1f, 3f, 0.5f, -2f];
        float[] estimate = [0.8f, 2.5f, -0.6f, 2.7f, 0.9f, -1.5f];

        double dbAtDoubled = SiSdr.Compute(Scale(estimate, 2.0f), reference);
        double dbAtNegativeFifth = SiSdr.Compute(Scale(estimate, -5.0f), reference);
        double dbAtOneTenth = SiSdr.Compute(Scale(estimate, 0.1f), reference);

        // Inputs are float32, so the projection carries single-precision rounding; the equality
        // only needs to hold to well beyond float32's ~7 significant digits, not to double's full
        // precision.
        Assert.True(double.IsFinite(dbAtDoubled));
        Assert.Equal(dbAtDoubled, dbAtNegativeFifth, precision: 4);
        Assert.Equal(dbAtDoubled, dbAtOneTenth, precision: 4);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void HandComputedCase_MatchesExactValue()
    {
        // reference = [1, 0], estimate = [2, 1].
        // alpha = dot(e,r)/dot(r,r) = 2/1 = 2 -> s_target = [2, 0].
        // e_noise = estimate - s_target = [0, 1].
        // energy(s_target) = 4, energy(e_noise) = 1 -> SI-SDR = 10*log10(4) dB.
        float[] reference = [1f, 0f];
        float[] estimate = [2f, 1f];

        double db = SiSdr.Compute(estimate, reference);

        Assert.Equal(10.0 * Math.Log10(4.0), db, precision: 6);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void MoreNoise_ScoresLower()
    {
        float[] reference = [1f, -1f, 2f, -2f, 0.5f, -0.5f, 1.5f, -1.5f];
        float[] smallNoise = [0.05f, -0.03f, 0.02f, -0.04f, 0.01f, -0.02f, 0.03f, -0.01f];
        float[] largeNoise = [0.5f, -0.3f, 0.2f, -0.4f, 0.1f, -0.2f, 0.3f, -0.1f];

        double dbWithSmallNoise = SiSdr.Compute(Add(reference, smallNoise), reference);
        double dbWithLargeNoise = SiSdr.Compute(Add(reference, largeNoise), reference);

        Assert.True(dbWithSmallNoise > dbWithLargeNoise);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void LengthMismatch_Throws()
    {
        float[] reference = [1f, 2f, 3f];
        float[] estimate = [1f, 2f];

        Assert.Throws<ArgumentException>(() => SiSdr.Compute(estimate, reference));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void SilentReference_Throws()
    {
        float[] reference = [0f, 0f, 0f];
        float[] estimate = [0.1f, -0.2f, 0.3f];

        Assert.Throws<ArgumentException>(() => SiSdr.Compute(estimate, reference));
    }

    private static float[] Scale(float[] x, float k)
    {
        var scaled = new float[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            scaled[i] = x[i] * k;
        }

        return scaled;
    }

    private static float[] Add(float[] a, float[] b)
    {
        var sum = new float[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            sum[i] = a[i] + b[i];
        }

        return sum;
    }
}
