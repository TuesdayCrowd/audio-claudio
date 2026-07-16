using System.Collections.Generic;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

/// <summary>
/// <see cref="PcmAudioSource"/> is the production twin of the test-only
/// <see cref="InMemoryAudioSource"/>: a raw mono PCM buffer framed via the Domain splitter, for a
/// stem produced by an <c>ISourceSeparator</c> that has no file/device to read from.
/// </summary>
public class PcmAudioSourceTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Frames_tile_the_buffer_at_the_declared_hop_and_reconstruct_the_input()
    {
        var rate = new SampleRate(44100);
        float[] samples = { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f };
        var parameters = new FrameParameters(4, 4); // exact multiple: no zero-padding

        var source = new PcmAudioSource(samples, rate, parameters);

        IReadOnlyList<Frame> frames = AudioSources.Collect(source);
        Assert.Equal(2, frames.Count);
        Assert.Equal(0, frames[0].Start.Samples);
        Assert.Equal(4, frames[1].Start.Samples);

        float[] reconstructed = Framing.ReconstructMono(frames);
        Assert.Equal(samples, reconstructed);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Each_frame_carries_the_declared_sample_rate()
    {
        var rate = new SampleRate(22050);
        float[] samples = { 0.1f, 0.2f, 0.3f };
        var parameters = new FrameParameters(3, 3);

        var source = new PcmAudioSource(samples, rate, parameters);

        foreach (Frame frame in source.Frames)
        {
            Assert.Equal(22050, frame.Rate.Hz);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void An_empty_buffer_yields_no_frames()
    {
        var rate = new SampleRate(44100);
        var source = new PcmAudioSource(System.Array.Empty<float>(), rate, new FrameParameters(4, 4));

        Assert.Empty(source.Frames);
    }
}
