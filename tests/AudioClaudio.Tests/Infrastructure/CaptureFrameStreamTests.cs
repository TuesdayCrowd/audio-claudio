using System.Threading.Tasks;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Capture;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class CaptureFrameStreamTests
{
    private const int N = 1024, H = 256;
    private static readonly SampleRate Rate = new SampleRate(44100);

    [Fact]
    [Trait("Category", "Fast")]
    public void DownmixesInterleavedChannelsToMean()
    {
        var s = new CaptureFrameStream(N, H, Rate, channelCapacity: 256);

        int frames = 2 * N;                       // enough for several output frames
        var block = new float[frames * 2];        // stereo: L = 0.8, R = 0.2 -> mono 0.5
        for (int i = 0; i < frames; i++) { block[2 * i] = 0.8f; block[2 * i + 1] = 0.2f; }

        s.Submit(block, 2);
        s.Complete(); // flushes the trailing zero-padded frame(s), R10.1

        var outFrames = s.Frames.ToList();
        Assert.NotEmpty(outFrames);

        // Complete() flushes a zero-padded tail (matching WavAudioSource's own
        // end-of-file convention), so a real sample downmixes to exactly 0.5 but
        // a padded one is exactly 0 — both are valid, and at least one full frame
        // (index 0, entirely real) proves the downmix arithmetic itself.
        foreach (var f in outFrames)
            foreach (var v in f.Samples)
                Assert.True(v == 0f || MathF.Abs(v - 0.5f) < 1e-6f, $"unexpected downmixed sample {v}");
        foreach (var v in outFrames[0].Samples)
            Assert.Equal(0.5f, v, 6); // frame 0 is always fully real: start=0, N <= frames
    }

    // R10.1: total frames < channel capacity, so no drop is possible regardless of
    // thread timing; proves ordering and completeness across the thread boundary.
    [Fact]
    [Trait("Category", "Fast")]
    public async Task DeliversAllFramesInOrderWithoutDropsUnderLoad()
    {
        var s = new CaptureFrameStream(N, H, Rate, channelCapacity: 512);
        int totalSamples = 200 * H + N;           // 201 output frames, < capacity 512

        var producer = Task.Run(() =>
        {
            var mono = new float[totalSamples];
            for (int i = 0; i < totalSamples; i++) mono[i] = i; // marker = sample index
            var rng = new Random(12345);
            int idx = 0;
            while (idx < totalSamples)
            {
                int size = Math.Min(rng.Next(1, 400), totalSamples - idx);
                s.Submit(mono.AsSpan(idx, size), 1);
                idx += size;
            }
            s.Complete();
        });

        // Blocking-but-synchronous by design: Frames is the IAudioSource pull contract
        // (an IEnumerable, not awaitable), and its iterator blocks the calling thread on
        // the channel exactly as the real pipeline consumer will. Only the *producer*
        // task join below needs to be non-blocking to satisfy xUnit1031.
        var received = s.Frames.ToList();
        await producer;

        Assert.Equal(0, s.DroppedFrameCount);
        // Total frame count matches WavAudioSource/Framing.Split's own contract
        // (CONTRACTS.md §2: "Frame count = ceil(length / hop)") now that Complete()
        // flushes the zero-padded tail rather than dropping it (R10.1).
        int expected = (int)Math.Ceiling((double)totalSamples / H);
        Assert.Equal(expected, received.Count);
        for (int i = 0; i < received.Count; i++)
        {
            Assert.Equal((long)i * H, received[i].Start.Samples);
            Assert.Equal((float)(i * H), received[i].Samples[0]);
        }
    }

    // The audio thread must never silently swallow: an overflow is counted.
    [Fact]
    [Trait("Category", "Fast")]
    public void SignalsDroppedFramesWhenConsumerStalls()
    {
        var s = new CaptureFrameStream(N, H, Rate, channelCapacity: 2);
        var mono = new float[N + 50 * H];         // 51 frames into a capacity-2 channel
        s.Submit(mono, 1);
        Assert.True(s.DroppedFrameCount > 0);
    }
}
