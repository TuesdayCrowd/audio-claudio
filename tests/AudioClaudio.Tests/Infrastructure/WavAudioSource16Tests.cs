using System;
using System.Collections.Generic;
using System.IO;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Tests.Signals;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class WavAudioSource16Tests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Parses_a_known_mono_16bit_wav_into_expected_samples()
    {
        var rate = new SampleRate(8000);
        // +0.5 and -0.5 quantise to q = round(x*32768) = 16384 and -16384.
        byte[] bytes = WavWriter.Write16(new float[] { 0.5f, -0.5f }, rate);

        using var ms = new MemoryStream(bytes);
        var source = new WavAudioSource(ms, new FrameParameters(2, 2));

        var frames = AudioSources.Collect(source);
        Assert.Single(frames);
        Assert.Equal(8000, frames[0].Rate.Hz); // declared sample rate, carried on each frame
        Assert.Equal(16384 / 32768f, frames[0].Samples[0]);
        Assert.Equal(-16384 / 32768f, frames[0].Samples[1]);
        Assert.Equal(0, frames[0].Start.Samples);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Sine_round_trips_through_16bit_wav_exactly()
    {
        var rate = new SampleRate(44100);
        int n = 2048;
        var buffer = SignalGenerator.Sine(440.0, n * 20, rate); // exact multiple of N: no padding
        byte[] bytes = WavWriter.Write16(buffer, rate);

        // Expected = the same buffer put through the PCM convention, then framed.
        var expected = Framing.Split(Requantize16(buffer), rate, new FrameParameters(n, n));

        using var ms = new MemoryStream(bytes);
        var actual = AudioSources.Collect(new WavAudioSource(ms, new FrameParameters(n, n)));

        FrameAssert.Equal(expected, actual); // bit-exact
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void FromFile_reads_the_same_frames_and_releases_the_file_on_dispose()
    {
        var rate = new SampleRate(8000);
        byte[] bytes = WavWriter.Write16(new float[] { 0.5f, -0.5f }, rate);
        string path = Path.Combine(Path.GetTempPath(), $"audioclaudio-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, bytes);
        try
        {
            IReadOnlyList<Frame> frames;
            using (var source = WavAudioSource.FromFile(path, new FrameParameters(2, 2)))
            {
                frames = AudioSources.Collect(source);
            }

            Assert.Single(frames);
            Assert.Equal(16384 / 32768f, frames[0].Samples[0]);
            Assert.Equal(-16384 / 32768f, frames[0].Samples[1]);

            // FromFile OWNS the stream it opened and closes it on Dispose, so the file is now deletable
            // (load-bearing on Windows, where a leaked handle would lock the file; harmless elsewhere).
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Dispose_leaves_a_caller_supplied_stream_open()
    {
        var rate = new SampleRate(8000);
        byte[] bytes = WavWriter.Write16(new float[] { 0.5f, -0.5f }, rate);

        var ms = new MemoryStream(bytes);
        var source = new WavAudioSource(ms, new FrameParameters(2, 2));
        source.Dispose();

        Assert.True(ms.CanRead); // a stream passed to the ctor stays the caller's to close
    }

    private static float[] Requantize16(float[] x)
    {
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            int q = (int)System.Math.Round(x[i] * 32768.0);
            if (q > 32767) q = 32767;
            if (q < -32768) q = -32768;
            y[i] = (float)(q / 32768.0);
        }
        return y;
    }
}
