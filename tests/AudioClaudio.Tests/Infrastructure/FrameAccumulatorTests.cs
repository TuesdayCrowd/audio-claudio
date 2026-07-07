using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Capture;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class FrameAccumulatorTests
{
    private const int N = 1024;
    private const int H = 256;
    private static readonly SampleRate Rate = new SampleRate(44100);

    // The reference definition every correct adapter must match: full frames only,
    // tiled by hop H, first sample of frame i is mono[i*H], start position is i*H.
    private static List<Frame> Reference(float[] mono)
    {
        var frames = new List<Frame>();
        long start = 0;
        for (int pos = 0; pos + N <= mono.Length; pos += H)
        {
            var s = new float[N];
            Array.Copy(mono, pos, s, 0, N);
            frames.Add(new Frame(s, new SamplePosition(start, Rate)));
            start += H;
        }
        return frames;
    }

    private static void AssertFramesEqual(IReadOnlyList<Frame> expected, IReadOnlyList<Frame> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Start.Samples, actual[i].Start.Samples);
            Assert.Equal(expected[i].Samples.Length, actual[i].Samples.Length);
            for (int j = 0; j < expected[i].Samples.Length; j++)
                Assert.Equal(expected[i].Samples[j], actual[i].Samples[j]);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void EmitsFramesWithCorrectPositionsAndOverlap()
    {
        var mono = new float[N + 4 * H];
        for (int i = 0; i < mono.Length; i++) mono[i] = i; // distinct marker values

        var acc = new FrameAccumulator(N, H, Rate);
        var emitted = new List<Frame>();
        acc.Append(mono, emitted);

        AssertFramesEqual(Reference(mono), emitted);
        Assert.Equal(5, emitted.Count);
        Assert.Equal(0, emitted[0].Start.Samples);
        Assert.Equal(H, emitted[1].Start.Samples);
        Assert.Equal((float)H, emitted[1].Samples[0]); // frame 1 starts at sample H
    }

    // R10.1: however the device chops the stream into blocks, the frames are identical.
    [Fact]
    [Trait("Category", "Slow")]
    public void ChunkingIntoArbitraryBlocksYieldsIdenticalFrames()
    {
        var gen =
            from k in Gen.Int[1, 16]
            from mono in Gen.Single[-1f, 1f].Array[N + k * H]   // CsCheck names generators by CLR type: Gen.Single, not Gen.Float
            from blocks in Gen.Int[1, 700].Array[1, 60]
            select (mono, blocks);

        gen.Sample(t =>
        {
            var (mono, blocks) = t;
            var acc = new FrameAccumulator(N, H, Rate);
            var got = new List<Frame>();
            int idx = 0, b = 0;
            while (idx < mono.Length)
            {
                int size = Math.Min(blocks[b % blocks.Length], mono.Length - idx);
                acc.Append(mono.AsSpan(idx, size), got);
                idx += size;
                b++;
            }
            AssertFramesEqual(Reference(mono), got);
        }, iter: 1000);
    }
}
