using System;
using AudioClaudio.Domain.Spectral;
using Xunit;

namespace AudioClaudio.Tests.Spectral;

[Trait("Category", "Fast")]
public class HannWindowTests
{
    [Fact]
    public void Hann_matches_the_reference_formula()
    {
        const int n = 16;
        double[] w = HannWindow.Coefficients(n);

        Assert.Equal(n, w.Length);
        for (int k = 0; k < n; k++)
        {
            double expected = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * k / (n - 1)));
            Assert.Equal(expected, w[k], 12);
        }
    }

    [Fact]
    public void Hann_endpoints_are_zero()
    {
        double[] w = HannWindow.Coefficients(8);
        Assert.Equal(0.0, w[0], 12);
        Assert.Equal(0.0, w[^1], 12);
    }

    [Fact]
    public void Hann_is_symmetric()
    {
        double[] w = HannWindow.Coefficients(9);
        for (int i = 0; i < w.Length; i++)
            Assert.Equal(w[i], w[w.Length - 1 - i], 12);
    }

    [Fact]
    public void Hann_rejects_non_positive_size()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HannWindow.Coefficients(0));
    }
}
