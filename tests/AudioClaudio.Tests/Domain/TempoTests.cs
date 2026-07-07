using System;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class TempoTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Tempo_stores_beats_per_minute()
    {
        var tempo = new Tempo(120.0);
        Assert.Equal(120.0, tempo.BeatsPerMinute);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Tempo_rejects_non_positive_or_non_finite_bpm(double bpm)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Tempo(bpm));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Tempo_has_value_equality()
    {
        Assert.Equal(new Tempo(96.0), new Tempo(96.0));
    }
}
