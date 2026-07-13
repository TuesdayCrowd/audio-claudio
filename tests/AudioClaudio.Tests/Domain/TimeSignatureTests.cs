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

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("4/4", 4, 4)]
    [InlineData("3/4", 3, 4)]
    [InlineData("6/8", 6, 8)]
    [InlineData("2/2", 2, 2)]
    [InlineData(" 4 / 4 ", 4, 4)] // whitespace around the slash and the string is tolerated
    public void TryParse_accepts_valid_time_signatures(string text, int beatsPerMeasure, int beatUnit)
    {
        Assert.True(TimeSignature.TryParse(text, out TimeSignature result, out string? error));
        Assert.Equal(new TimeSignature(beatsPerMeasure, beatUnit), result);
        Assert.Null(error);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("5/3")]   // denominator not a power of two
    [InlineData("0/4")]   // non-positive numerator
    [InlineData("abc")]   // not numeric, no slash
    [InlineData("4")]     // no slash at all
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_rejects_invalid_time_signatures(string text)
    {
        Assert.False(TimeSignature.TryParse(text, out TimeSignature result, out string? error));
        Assert.Equal(default, result);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void TryParse_rejects_null()
    {
        Assert.False(TimeSignature.TryParse(null, out TimeSignature result, out string? error));
        Assert.Equal(default, result);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
