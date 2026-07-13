using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

/// <summary>Root-mean-square amplitude -- the pure math behind the live-view VU meter (S5.10).
/// No frame/audio-source coupling here (R1.5).</summary>
public class AudioLevelTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void RmsOfSilenceIsZero()
    {
        Assert.Equal(0.0, AudioLevel.Rms(new float[] { 0f, 0f, 0f, 0f }));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void RmsOfAFullScaleSquareWaveIsOne()
    {
        Assert.Equal(1.0, AudioLevel.Rms(new float[] { 1f, -1f, 1f, -1f }), 6);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void RmsOfAConstantHalfScaleSignalIsThatConstant()
    {
        Assert.Equal(0.5, AudioLevel.Rms(new float[] { 0.5f, 0.5f, 0.5f }), 6);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void RmsOfAnEmptySpanIsZero()
    {
        Assert.Equal(0.0, AudioLevel.Rms(System.ReadOnlySpan<float>.Empty));
    }
}
