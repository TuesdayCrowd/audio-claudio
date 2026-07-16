using System;
using AudioClaudio.Infrastructure.Separation;
using Xunit;

namespace AudioClaudio.Tests.Separation;

/// <summary>
/// Stage 1.3 — the cross-branch softmax and power-ratio remask are the two non-learned steps
/// Spleeter's own architecture lifts OUT of the ONNX graph into C# (MODEL_CARD.md): the 5 branches'
/// raw logits are stacked and softmaxed across the stem axis, then re-normalized via a power-ratio
/// remask for reconstruction. Both are pure elementwise math over the stem axis, so they get their
/// own fast, hand-computed unit tests independent of any ONNX inference — isolating "is the formula
/// right" from "is the model wrapper right" (the latter is covered by the Slow golden-parity tests
/// in SpleeterSourceSeparatorTests / SpleeterModelTests).
/// </summary>
public class SpleeterMaskingTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Softmax_matches_hand_computed_values_for_three_stems()
    {
        // One "pixel" (numSplits=1, T=1, F=2, channels=1); three synthetic stems with distinct
        // logits at bin 0 and identical (zero) logits at bin 1.
        var logitA = new float[1, 1, 2, 1] { { { { 1f }, { 0f } } } };
        var logitB = new float[1, 1, 2, 1] { { { { 2f }, { 0f } } } };
        var logitC = new float[1, 1, 2, 1] { { { { 0f }, { 0f } } } };
        var magnitude = new double[1, 1, 2, 1] { { { { 10.0 }, { 4.0 } } } };

        var logits = new[] { logitA, logitB, logitC };
        var estimated = SpleeterMasking.Softmax(logits, magnitude);

        double sumExpBin0 = Math.Exp(1) + Math.Exp(2) + Math.Exp(0);
        double[] expectedMaskBin0 =
        {
            Math.Exp(1) / sumExpBin0,
            Math.Exp(2) / sumExpBin0,
            Math.Exp(0) / sumExpBin0,
        };
        const double expectedMaskBin1 = 1.0 / 3.0; // all logits equal -> uniform softmax

        for (int k = 0; k < 3; k++)
        {
            AssertClose(expectedMaskBin0[k] * 10.0, estimated[k][0, 0, 0, 0], 1e-12);
            AssertClose(expectedMaskBin1 * 4.0, estimated[k][0, 0, 1, 0], 1e-12);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Softmax_estimated_magnitudes_sum_to_the_mixture_magnitude()
    {
        // Softmax masks always sum to 1 across stems, so the estimated (mask x magnitude) values
        // must sum back to exactly the mixture magnitude at every pixel -- a property that should
        // hold regardless of the (arbitrary) logit values.
        var rnd = new Random(42);
        const int stemCount = 5;
        var logits = new float[stemCount][,,,];
        for (int k = 0; k < stemCount; k++)
        {
            logits[k] = new float[2, 3, 4, 2];
            for (int s = 0; s < 2; s++)
                for (int t = 0; t < 3; t++)
                    for (int f = 0; f < 4; f++)
                        for (int ch = 0; ch < 2; ch++)
                            logits[k][s, t, f, ch] = (float)(rnd.NextDouble() * 2000 - 1000); // Spleeter's ~+-1000 logit range

        }

        var magnitude = new double[2, 3, 4, 2];
        for (int s = 0; s < 2; s++)
            for (int t = 0; t < 3; t++)
                for (int f = 0; f < 4; f++)
                    for (int ch = 0; ch < 2; ch++)
                        magnitude[s, t, f, ch] = 1.0 + rnd.NextDouble() * 100;

        var estimated = SpleeterMasking.Softmax(logits, magnitude);

        for (int s = 0; s < 2; s++)
            for (int t = 0; t < 3; t++)
                for (int f = 0; f < 4; f++)
                    for (int ch = 0; ch < 2; ch++)
                    {
                        double sum = 0.0;
                        for (int k = 0; k < stemCount; k++) sum += estimated[k][s, t, f, ch];
                        AssertClose(magnitude[s, t, f, ch], sum, 1e-9);
                    }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void RatioMask_matches_hand_computed_values_for_two_stems()
    {
        // One "pixel", two stems: estimated magnitudes 3.0 and 4.0.
        var estA = new double[1, 1, 1, 1] { { { { 3.0 } } } };
        var estB = new double[1, 1, 1, 1] { { { { 4.0 } } } };
        var estimated = new[] { estA, estB };

        var ratio = SpleeterMasking.RatioMask(estimated);

        const double epsilon = 1e-10;
        const int n = 2;
        double sumPow = (3.0 * 3.0) + (4.0 * 4.0);
        double expectedA = ((3.0 * 3.0) + (epsilon / n)) / (sumPow + epsilon);
        double expectedB = ((4.0 * 4.0) + (epsilon / n)) / (sumPow + epsilon);

        AssertClose(expectedA, ratio[0][0, 0, 0, 0], 1e-15);
        AssertClose(expectedB, ratio[1][0, 0, 0, 0], 1e-15);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void RatioMask_sums_to_one_across_stems_by_construction()
    {
        // ratio_i = (est_i^2 + eps/N) / (sum_j est_j^2 + eps) sums to exactly 1 across i by
        // algebraic construction -- a property that should hold for any nonnegative estimates.
        var rnd = new Random(7);
        const int stemCount = 5;
        var estimated = new double[stemCount][,,,];
        for (int k = 0; k < stemCount; k++)
        {
            estimated[k] = new double[1, 2, 2, 2];
            for (int t = 0; t < 2; t++)
                for (int f = 0; f < 2; f++)
                    for (int ch = 0; ch < 2; ch++)
                        estimated[k][0, t, f, ch] = rnd.NextDouble() * 50;
        }

        var ratio = SpleeterMasking.RatioMask(estimated);

        for (int t = 0; t < 2; t++)
            for (int f = 0; f < 2; f++)
                for (int ch = 0; ch < 2; ch++)
                {
                    double sum = 0.0;
                    for (int k = 0; k < stemCount; k++) sum += ratio[k][0, t, f, ch];
                    AssertClose(1.0, sum, 1e-9);
                }
    }

    private static void AssertClose(double expected, double actual, double tolerance)
    {
        double diff = Math.Abs(expected - actual);
        Assert.True(diff < tolerance, $"expected {expected}, got {actual} (diff {diff:E3} >= tolerance {tolerance:E3})");
    }
}
