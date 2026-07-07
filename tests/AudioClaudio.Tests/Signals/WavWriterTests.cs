using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Signals;

public class WavWriterTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Write16_emits_a_canonical_pcm_riff_header()
    {
        var rate = new SampleRate(44100);
        byte[] bytes = WavWriter.Write16(new float[] { 0f, 0.5f, -0.5f, 0.25f }, rate);

        Assert.Equal("RIFF", Ascii(bytes, 0));
        Assert.Equal("WAVE", Ascii(bytes, 8));
        Assert.Equal("fmt ", Ascii(bytes, 12));
        Assert.Equal(16, I32(bytes, 16));     // PCM fmt chunk size
        Assert.Equal(1, I16(bytes, 20));      // audio format 1 == PCM
        Assert.Equal(1, I16(bytes, 22));      // channels
        Assert.Equal(44100, I32(bytes, 24));  // sample rate
        Assert.Equal(16, I16(bytes, 34));     // bits per sample
        Assert.Equal("data", Ascii(bytes, 36));
        Assert.Equal(4 * 2, I32(bytes, 40));  // data bytes: 4 samples * 2 bytes
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Write16Stereo_records_two_channels_and_interleaved_data()
    {
        var rate = new SampleRate(48000);
        byte[] bytes = WavWriter.Write16Stereo(new float[] { 0.5f, 0.25f }, new float[] { -0.5f, 0f }, rate);

        Assert.Equal(2, I16(bytes, 22));      // channels
        Assert.Equal(2 * 2, I16(bytes, 32));  // block align: channels * bytesPerSample
        Assert.Equal(2 * 2 * 2, I32(bytes, 40)); // data bytes: 2 frames * 2 ch * 2 bytes
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void WriteMonoFile_writes_the_same_bytes_as_Write16()
    {
        var rate = new SampleRate(22050);
        var mono = new float[] { 0f, 0.5f, -0.5f, 0.25f };
        string path = Path.Combine(Path.GetTempPath(), $"audioclaudio-{Guid.NewGuid():N}.wav");
        try
        {
            WavWriter.WriteMonoFile(path, mono, rate);
            Assert.Equal(WavWriter.Write16(mono, rate), File.ReadAllBytes(path)); // thin wrapper over Write16
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string Ascii(byte[] b, int off) => Encoding.ASCII.GetString(b, off, 4);
    private static int I32(byte[] b, int off) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(off, 4));
    private static short I16(byte[] b, int off) => BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(off, 2));
}
