using System;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class TimeSignatureTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void FourFour_is_four_over_four()
    {
        var ts = TimeSignature.FourFour;
        Assert.Equal(4, ts.BeatsPerMeasure);
        Assert.Equal(4, ts.BeatUnit);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Rejects_non_positive_numerator()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeSignature(0, 4));
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(3)]  // not a power of two
    [InlineData(0)]
    [InlineData(-4)]
    public void Rejects_denominator_that_is_not_a_positive_power_of_two(int denominator)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeSignature(4, denominator));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Has_value_equality()
    {
        Assert.Equal(new TimeSignature(4, 4), TimeSignature.FourFour);
    }
}
