using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Audio;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class PortAudioAudioSourceTests
{
    private const int N = 1024, H = 256;

    // Device-free: construct (no Start), push a block through the callback seam,
    // Complete, and read frames. Proves the adapter only downmixes+reframes
    // (no transcription logic, R10.4) and honours the IAudioSource contract.
    // NEVER calls Start()/PortAudio.Initialize() and NEVER opens a device — there
    // is none in CI/sandbox; the real device path is manual/loopback acceptance only.
    [Fact]
    [Trait("Category", "Fast")]
    public void OnAudioBlockProducesDownmixedFrames()
    {
        using var src = new PortAudioAudioSource(
            sampleRateHz: 44100, frameSize: N, hop: H, channels: 2, channelCapacity: 1024);

        int frames = 2 * N;                       // several output frames
        var block = new float[frames * 2];        // stereo L = 1.0, R = 0.0 -> mono 0.5
        for (int i = 0; i < frames; i++) { block[2 * i] = 1.0f; block[2 * i + 1] = 0.0f; }

        src.OnAudioBlock(block, 2);
        src.Stop();                               // not started -> just completes the stream

        var outFrames = src.Frames.ToList();
        Assert.NotEmpty(outFrames);
        Assert.Equal(44100, src.SampleRate.Hz);   // CONTRACTS: SampleRate.Hz, never .Hertz
        Assert.Equal(44100, outFrames[0].Rate.Hz); // Frame.Rate is derived from Start.Rate

        // Stop() flushes the zero-padded tail (R10.1, matching WavAudioSource's own
        // end-of-file convention): a real sample downmixes to exactly 0.5, a padded
        // one is exactly 0. Frame 0 is always fully real (start=0, N <= frames).
        foreach (var f in outFrames)
            foreach (var v in f.Samples)
                Assert.True(v == 0f || MathF.Abs(v - 0.5f) < 1e-6f, $"unexpected downmixed sample {v}");
        foreach (var v in outFrames[0].Samples)
            Assert.Equal(0.5f, v, 6);
    }
}
