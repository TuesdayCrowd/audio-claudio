using System.Linq;
using AudioClaudio.Domain;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class FramingProperties
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Split_with_hop_equal_to_size_tiles_the_buffer_exactly()
    {
        var rate = new SampleRate(8000);
        var samples = Enumerable.Range(0, 12).Select(i => i / 100f).ToArray();

        var frames = Framing.Split(samples, rate, new FrameParameters(4, 4));

        Assert.Equal(3, frames.Count);
        Assert.Equal(new long[] { 0, 4, 8 }, frames.Select(f => f.Start.Samples).ToArray());
        Assert.Equal(samples, frames.SelectMany(f => f.Samples).ToArray()); // no gaps, no overlap
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Split_zero_pads_a_short_final_frame()
    {
        var rate = new SampleRate(8000);
        var samples = new float[] { 1, 2, 3, 4, 5 };

        var frames = Framing.Split(samples, rate, new FrameParameters(4, 4));

        Assert.Equal(2, frames.Count);                          // starts 0 and 4
        Assert.Equal(new float[] { 1, 2, 3, 4 }, frames[0].Samples);
        Assert.Equal(new float[] { 5, 0, 0, 0 }, frames[1].Samples); // padded with zeros
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Frame_starts_advance_by_exactly_the_hop_and_cover_the_buffer()
    {
        var rate = new SampleRate(44100);

        // Property: in the gap-free regime (hop <= size), successive frame starts differ by
        // exactly the hop, the count is ceil(length/hop), and every frame has N samples.
        Gen.Select(Gen.Int[1, 64], Gen.Int[1, 64], Gen.Int[0, 400])
           .Where(t => t.Item2 <= t.Item1) // hop <= size
           .Sample(t =>
           {
               var (size, hop, length) = t;
               var samples = new float[length];
               var frames = Framing.Split(samples, rate, new FrameParameters(size, hop));

               long expectedCount = length == 0 ? 0 : (length - 1) / hop + 1; // ceil(length/hop)
               Assert.Equal(expectedCount, frames.Count);
               for (int i = 0; i < frames.Count; i++)
               {
                   Assert.Equal((long)i * hop, frames[i].Start.Samples);
                   Assert.Equal(size, frames[i].Samples.Length);
               }
           }, iter: 1000, seed: "0N0XnlbeE0O5");
        // Foundation testing convention "Fix CsCheck seeds for reproducibility": the run is pinned to
        // a fixed seed up front so every CI run is deterministic from the start. If a genuine failure
        // surfaces, CsCheck prints a reproduction seed — replace the value here with it to lock the case.
    }
}
