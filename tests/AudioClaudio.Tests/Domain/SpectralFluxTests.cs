using System.Collections.Generic;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public sealed class SpectralFluxTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void FrameZeroMeasuresIncreaseFromImplicitSilence()
    {
        var spectra = new List<IReadOnlyList<double>>
        {
            new double[] { 1.0, 2.0, 3.0 },
        };

        double[] novelty = SpectralFlux.Compute(spectra);

        Assert.Single(novelty);
        Assert.Equal(6.0, novelty[0], 10);   // whole spectrum appears from silence
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void RisingMagnitudeProducesPositiveFlux()
    {
        var spectra = new List<IReadOnlyList<double>>
        {
            new double[] { 0.0, 0.0 },
            new double[] { 1.0, 4.0 },
        };

        double[] novelty = SpectralFlux.Compute(spectra);

        Assert.Equal(0.0, novelty[0], 10);
        Assert.Equal(5.0, novelty[1], 10);   // (1-0) + (4-0)
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void FallingMagnitudeIsRectifiedToZero()
    {
        var spectra = new List<IReadOnlyList<double>>
        {
            new double[] { 5.0, 5.0 },
            new double[] { 1.0, 0.0 },
        };

        double[] novelty = SpectralFlux.Compute(spectra);

        // Every bin decreased; half-wave rectification zeroes the decay.
        Assert.Equal(0.0, novelty[1], 10);
    }
}
