using System.IO;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Tests.Signals;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class WavAudioSourceStereoTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Stereo_downmix_is_the_sample_mean()
    {
        var rate = new SampleRate(44100);
        var left = new float[] { 0.5f, 0.25f, -0.75f, 0f };
        var right = new float[] { -0.5f, 0.75f, 0.5f, 0.2f };
        byte[] bytes = WavWriter.Write16Stereo(left, right, rate);

        using var ms = new MemoryStream(bytes);
        var frames = AudioSources.Collect(new WavAudioSource(ms, new FrameParameters(4, 4)));
        Assert.Single(frames);

        for (int i = 0; i < 4; i++)
        {
            double ql = QuantizeDequantize16(left[i]);
            double qr = QuantizeDequantize16(right[i]);
            float expected = (float)((ql + qr) / 2); // adapter computes sum/channels
            Assert.Equal(expected, frames[0].Samples[i]);
        }
    }

    private static double QuantizeDequantize16(float x)
    {
        int q = (int)System.Math.Round(x * 32768.0);
        if (q > 32767) q = 32767;
        if (q < -32768) q = -32768;
        return q / 32768.0;
    }
}
