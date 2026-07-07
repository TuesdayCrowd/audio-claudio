using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class FrameTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Frame_exposes_samples_start_and_rate()
    {
        var rate = new SampleRate(44100);
        var start = new SamplePosition(2048, rate);
        var frame = new Frame(new float[] { 0.1f, -0.2f }, start);

        Assert.Equal(new float[] { 0.1f, -0.2f }, frame.Samples);
        Assert.Equal(2048, frame.Start.Samples);
        Assert.Equal(44100, frame.Rate.Hz); // Rate is derived from Start — one source of truth
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void FrameParameters_carries_size_and_hop()
    {
        var p = new FrameParameters(2048, 512);
        Assert.Equal(2048, p.Size);
        Assert.Equal(512, p.Hop);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(-1, 4)]
    [InlineData(4, -1)]
    public void FrameParameters_rejects_non_positive_size_or_hop(int size, int hop)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameParameters(size, hop));
    }
}
