using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;
using Xunit;

namespace AudioClaudio.Tests.Spectral;

[Trait("Category", "Fast")]
public class MagnitudeSpectrumTests
{
    [Fact]
    public void BinCount_is_half_frame_plus_one()
    {
        var s = new MagnitudeSpectrum(new double[9], frameSize: 16, rate: new SampleRate(8000));
        Assert.Equal(9, s.BinCount);
    }

    [Fact]
    public void FrequencyOf_maps_bins_to_hertz()
    {
        var s = new MagnitudeSpectrum(new double[9], frameSize: 16, rate: new SampleRate(8000));
        Assert.Equal(0.0, s.FrequencyOf(0), 9);      // DC
        Assert.Equal(500.0, s.FrequencyOf(1), 9);    // 1·8000/16
        Assert.Equal(4000.0, s.FrequencyOf(8), 9);   // Nyquist = 8000/2
    }

    [Fact]
    public void PeakBin_returns_the_largest_bin_and_breaks_ties_to_the_lowest()
    {
        var s = new MagnitudeSpectrum(new double[] { 1.0, 3.0, 3.0, 2.0 }, frameSize: 6, rate: new SampleRate(8000));
        Assert.Equal(1, s.PeakBin()); // defined tie-break: lowest bin wins (non-negotiable #3)
    }

    [Fact]
    public void Magnitudes_are_copied_defensively()
    {
        var raw = new double[] { 1.0, 2.0 };
        var s = new MagnitudeSpectrum(raw, frameSize: 2, rate: new SampleRate(8000));
        raw[0] = 99.0;
        Assert.Equal(1.0, s[0]); // external mutation must not leak in
    }
}
