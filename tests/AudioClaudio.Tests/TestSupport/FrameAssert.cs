using System.Collections.Generic;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.TestSupport;

/// <summary>Structural, bit-exact frame comparison (Frame has no value equality by design).</summary>
public static class FrameAssert
{
    public static void Equal(Frame expected, Frame actual)
    {
        Assert.Equal(expected.Start.Samples, actual.Start.Samples);
        Assert.Equal(expected.Rate.Hz, actual.Rate.Hz);
        Assert.Equal(expected.Samples, actual.Samples); // element-wise, exact for float[]
    }

    public static void Equal(IReadOnlyList<Frame> expected, IReadOnlyList<Frame> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++) Equal(expected[i], actual[i]);
    }
}
