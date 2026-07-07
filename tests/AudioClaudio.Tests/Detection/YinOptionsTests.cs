using System;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests;

public class YinOptionsTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Default_HasNamedThresholdAndPianoSearchRange()
    {
        var o = YinOptions.Default;

        Assert.Equal(0.15, o.Threshold, precision: 9);
        Assert.Equal(45.0, o.MinFrequencyHz, precision: 9);
        Assert.Equal(2500.0, o.MaxFrequencyHz, precision: 9);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void Constructor_RejectsThresholdOutsideOpenUnitInterval(double badThreshold)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new YinOptions(threshold: badThreshold));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Constructor_RejectsNonPositiveMinFrequency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new YinOptions(minFrequencyHz: 0.0));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Constructor_RejectsMaxNotAboveMin()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new YinOptions(minFrequencyHz: 400.0, maxFrequencyHz: 400.0));
    }
}
