using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Application;

public class AudioSourcePortTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void InMemory_source_yields_positioned_in_range_frames()
    {
        var rate = new SampleRate(16000);
        var samples = new float[4096];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = MathF.Sin(i * 0.1f) * 0.5f; // deterministic, within [-0.5, 0.5]

        IAudioSource source = new InMemoryAudioSource(samples, rate, new FrameParameters(1024, 1024));

        var frames = AudioSources.Collect(source); // decision-agnostic collection seam
        Assert.Equal(4, frames.Count);
        Assert.Equal(16000, frames[0].Rate.Hz); // "declared sample rate" (R2.1) — carried on each frame
        Assert.Equal(0, frames[0].Start.Samples);
        Assert.Equal(1024, frames[1].Start.Samples); // each frame carries its starting SamplePosition
        foreach (var f in frames)
        {
            Assert.Equal(16000, f.Rate.Hz);
            foreach (var s in f.Samples)
                Assert.InRange(s, -1f, 1f); // mono float in [-1, 1]
        }
    }
}
