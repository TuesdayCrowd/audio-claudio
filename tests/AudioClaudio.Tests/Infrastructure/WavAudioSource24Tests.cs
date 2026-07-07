using System.IO;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Tests.Signals;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class WavAudioSource24Tests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void HarmonicStack_round_trips_through_24bit_wav_exactly()
    {
        var rate = new SampleRate(44100);
        int n = 1024;
        var buffer = SignalGenerator.HarmonicStack(220.0, n * 16, rate, partials: 5, decay: 1.0);
        byte[] bytes = WavWriter.Write24(buffer, rate);

        var expected = Framing.Split(Requantize24(buffer), rate, new FrameParameters(n, n));

        using var ms = new MemoryStream(bytes);
        var actual = AudioSources.Collect(new WavAudioSource(ms, new FrameParameters(n, n)));

        FrameAssert.Equal(expected, actual);
    }

    private static float[] Requantize24(float[] x)
    {
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            int q = (int)System.Math.Round(x[i] * 8388608.0);
            if (q > 8388607) q = 8388607;
            if (q < -8388608) q = -8388608;
            y[i] = (float)(q / 8388608.0);
        }
        return y;
    }
}
