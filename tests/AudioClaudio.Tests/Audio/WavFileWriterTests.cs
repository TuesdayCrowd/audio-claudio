using System;
using System.Text;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Audio;
using Xunit;

namespace AudioClaudio.Tests.Audio;

public class WavFileWriterTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Writes_a_valid_16bit_pcm_wav_header_and_samples()
    {
        var rate = new SampleRate(48000);
        float[] pcm = { 0f, 1f, -1f, 0.5f };

        byte[] wav = WavFileWriter.ToBytes(pcm, rate);

        Assert.Equal(44 + pcm.Length * 2, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(16, BitConverter.ToInt32(wav, 16));      // PCM fmt chunk size
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));       // audio format = PCM
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));       // channels = mono
        Assert.Equal(48000, BitConverter.ToInt32(wav, 24));   // sample rate
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));      // bits per sample
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(pcm.Length * 2, BitConverter.ToInt32(wav, 40)); // data chunk size

        Assert.Equal(0, BitConverter.ToInt16(wav, 44));       // 0.0
        Assert.Equal(32767, BitConverter.ToInt16(wav, 46));   // +1.0 -> 32767
        Assert.Equal(-32767, BitConverter.ToInt16(wav, 48));  // -1.0 -> -32767
        Assert.Equal(16384, BitConverter.ToInt16(wav, 50));   // 0.5 -> round(16383.5) -> 16384 (to-even)
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Clamps_out_of_range_samples_instead_of_wrapping()
    {
        var rate = new SampleRate(44100);
        float[] pcm = { 2.5f, -3.0f };

        byte[] wav = WavFileWriter.ToBytes(pcm, rate);

        Assert.Equal(32767, BitConverter.ToInt16(wav, 44));
        Assert.Equal(-32767, BitConverter.ToInt16(wav, 46));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Write_creates_a_file_with_identical_bytes_to_ToBytes()
    {
        var rate = new SampleRate(44100);
        float[] pcm = { 0.1f, -0.2f, 0.3f };
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wavfilewriter-{Guid.NewGuid():N}.wav");

        try
        {
            WavFileWriter.Write(path, pcm, rate);
            byte[] fromFile = System.IO.File.ReadAllBytes(path);
            byte[] expected = WavFileWriter.ToBytes(pcm, rate);
            Assert.Equal(expected, fromFile);
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }
}
