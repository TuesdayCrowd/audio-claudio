using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests;

public class PitchEstimateTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Voiced_CarriesFrequencyConfidenceAndIsVoiced()
    {
        var e = PitchEstimate.Voiced(frequencyHz: 440.0, confidence: 0.97);

        Assert.True(e.IsVoiced);
        Assert.Equal(440.0, e.FrequencyHz, precision: 9);
        Assert.Equal(0.97, e.Confidence, precision: 9);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Unvoiced_IsNotVoiced()
    {
        Assert.False(PitchEstimate.Unvoiced.IsVoiced);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Voiced_RejectsNonPositiveFrequency(double badHz)
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => PitchEstimate.Voiced(badHz, confidence: 0.5));
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Voiced_RejectsConfidenceOutsideUnitInterval(double badConf)
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => PitchEstimate.Voiced(440.0, badConf));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Estimates_HaveValueEquality()
    {
        Assert.Equal(PitchEstimate.Voiced(440.0, 0.9), PitchEstimate.Voiced(440.0, 0.9));
        Assert.NotEqual(PitchEstimate.Voiced(440.0, 0.9), PitchEstimate.Unvoiced);
    }
}
