using System;
using AudioClaudio.Domain.Spectral;
using Xunit;

namespace AudioClaudio.Tests.Spectral;

// Stage 1.1b (source separation STFT front end) — Spleeter's analysis/synthesis window is
// the PERIODIC Hann (denominator N, not N-1). The existing HannWindow is symmetric
// (HannWindowTests: endpoints both zero, denominator N-1) and stays that way for the
// monophonic pipeline; this is the sibling type the Spleeter front end needs instead.
[Trait("Category", "Fast")]
public class PeriodicHannWindowTests
{
    [Fact]
    public void Periodic_hann_matches_the_reference_formula()
    {
        const int n = 16;
        double[] w = PeriodicHannWindow.Coefficients(n);

        Assert.Equal(n, w.Length);
        for (int k = 0; k < n; k++)
        {
            double expected = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * k / n); // denom N, not N-1
            Assert.Equal(expected, w[k], 12);
        }
    }

    [Fact]
    public void Periodic_hann_first_sample_is_zero_but_last_is_not()
    {
        double[] w = PeriodicHannWindow.Coefficients(8);

        Assert.Equal(0.0, w[0], 12);
        Assert.NotEqual(0.0, w[^1]); // the defining difference from the symmetric HannWindow

        double expectedLast = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * 7 / 8);
        Assert.Equal(expectedLast, w[^1], 12);
    }

    [Fact]
    public void Periodic_hann_rejects_non_positive_size()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PeriodicHannWindow.Coefficients(0));
    }
}
