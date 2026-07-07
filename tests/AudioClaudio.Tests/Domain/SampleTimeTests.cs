using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class SampleTimeTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void SampleRate_CarriesHzAndComparesByValue()
    {
        var a = new SampleRate(44100);
        var b = new SampleRate(44100);
        var c = new SampleRate(48000);
        Assert.Equal(44100, a.Hz);
        Assert.Equal(a, b);        // value equality
        Assert.NotEqual(a, c);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(0)]
    [InlineData(-1)]
    public void SampleRate_RejectsNonPositive(int hz)
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new SampleRate(hz));
    }

    private static readonly SampleRate R44 = new(44100);
    private static readonly SampleRate R48 = new(48000);

    [Fact]
    [Trait("Category", "Fast")]
    public void Position_And_Duration_CarrySamplesWithRate()
    {
        var pos = new SamplePosition(1000, R44);
        var dur = new SampleDuration(500, R44);
        Assert.Equal(1000, pos.Samples);
        Assert.Equal(R44, pos.Rate);
        Assert.Equal(500, dur.Samples);
        Assert.Equal(R44, dur.Rate);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(-1)]
    public void Position_And_Duration_RejectNegativeSamples(long samples)
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new SamplePosition(samples, R44));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new SampleDuration(samples, R44));
    }

    // R1.3 — same-rate arithmetic: position + duration -> position; position - position -> duration.
    [Fact]
    [Trait("Category", "Fast")]
    public void SameRateArithmetic_AddsAndSubtracts()
    {
        var pos = new SamplePosition(1000, R44);
        SamplePosition later = pos + new SampleDuration(500, R44);
        Assert.Equal(1500, later.Samples);
        Assert.Equal(R44, later.Rate);

        SampleDuration between = later - pos;
        Assert.Equal(500, between.Samples);
        Assert.Equal(R44, between.Rate);

        SampleDuration total = new SampleDuration(200, R44) + new SampleDuration(300, R44);
        Assert.Equal(500, total.Samples);
    }

    // R1.3 — the currency-mismatch rule: mixing 44.1 kHz and 48 kHz throws, never coerces.
    [Fact]
    [Trait("Category", "Fast")]
    public void MixedRateArithmetic_Throws()
    {
        var pos44 = new SamplePosition(1000, R44);
        var dur48 = new SampleDuration(500, R48);
        var pos48 = new SamplePosition(1000, R48);

        Assert.Throws<System.InvalidOperationException>(() => { var _ = pos44 + dur48; });
        Assert.Throws<System.InvalidOperationException>(() => { var _ = pos44 - pos48; });
        Assert.Throws<System.InvalidOperationException>(() =>
        {
            var _ = new SampleDuration(1, R44) + new SampleDuration(1, R48);
        });
    }

    // Section 4 — seconds are a DISPLAY conversion at the edge only.
    [Fact]
    [Trait("Category", "Fast")]
    public void ToSeconds_IsEdgeDisplayConversion()
    {
        var oneSecond = new SamplePosition(44100, R44);
        Assert.True(System.Math.Abs(oneSecond.ToSeconds() - 1.0) < 1e-12);
        var halfSecond = new SampleDuration(22050, R44);
        Assert.True(System.Math.Abs(halfSecond.ToSeconds() - 0.5) < 1e-12);
    }
}
