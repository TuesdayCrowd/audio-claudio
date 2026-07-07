using System.IO;
using System.Text;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class WavAudioSourceRobustnessTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Skips_unknown_chunks_between_fmt_and_data()
    {
        var rate = new SampleRate(8000);
        // data for 16384 (+0.5) and -16384 (-0.5) as little-endian int16.
        byte[] data = { 0x00, 0x40, 0x00, 0xC0 };
        byte[] listBody = { (byte)'I', (byte)'N', (byte)'F', (byte)'O', 1, 2, 3, 4 };
        byte[] wav = BuildWav(channels: 1, rate: 8000, bits: 16, data: data,
                              extra: ("LIST", listBody));

        using var ms = new MemoryStream(wav);
        var frames = AudioSources.Collect(new WavAudioSource(ms, new FrameParameters(2, 2)));

        Assert.Single(frames);
        Assert.Equal(16384 / 32768f, frames[0].Samples[0]);
        Assert.Equal(-16384 / 32768f, frames[0].Samples[1]);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Parses_when_the_data_chunk_precedes_the_fmt_chunk()
    {
        // Proves the "any order" claim: the reader walks the chunk list, so data-before-fmt parses.
        var rate = new SampleRate(8000);
        byte[] data = { 0x00, 0x40, 0x00, 0xC0 }; // 16384 (+0.5), -16384 (-0.5) as little-endian int16
        byte[] wav = BuildWav(channels: 1, rate: 8000, bits: 16, data: data, dataFirst: true);

        using var ms = new MemoryStream(wav);
        var frames = AudioSources.Collect(new WavAudioSource(ms, new FrameParameters(2, 2)));

        Assert.Single(frames);
        Assert.Equal(16384 / 32768f, frames[0].Samples[0]);
        Assert.Equal(-16384 / 32768f, frames[0].Samples[1]);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Rejects_non_riff_input()
    {
        using var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 });
        Assert.Throws<InvalidDataException>(() => new WavAudioSource(ms, new FrameParameters(2, 2)));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Rejects_unsupported_bit_depth()
    {
        // 8-bit PCM is valid RIFF but unsupported by this adapter — fail fast, descriptively.
        byte[] wav = BuildWav(channels: 1, rate: 8000, bits: 8, data: new byte[] { 128, 128 });
        using var ms = new MemoryStream(wav);
        Assert.Throws<NotSupportedException>(() => new WavAudioSource(ms, new FrameParameters(2, 2)));
    }

    /// <summary>Builds a minimal WAV with fmt, an optional extra chunk, then data; patches the RIFF size.
    /// With <paramref name="dataFirst"/> the data chunk is emitted before fmt, to prove chunk-order robustness.</summary>
    private static byte[] BuildWav(int channels, int rate, int bits, byte[] data,
                                   (string id, byte[] body)? extra = null, bool dataFirst = false)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int blockAlign = channels * (bits / 8);

        void Chunk(string id, byte[] body)
        {
            w.Write(Encoding.ASCII.GetBytes(id));
            w.Write(body.Length);
            w.Write(body);
            if ((body.Length & 1) == 1) w.Write((byte)0); // word-align
        }

        byte[] fmtBody;
        using (var fmt = new MemoryStream())
        using (var fw = new BinaryWriter(fmt))
        {
            fw.Write((short)1);              // PCM
            fw.Write((short)channels);
            fw.Write(rate);
            fw.Write(rate * blockAlign);
            fw.Write((short)blockAlign);
            fw.Write((short)bits);
            fw.Flush();
            fmtBody = fmt.ToArray();
        }

        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        long sizePos = ms.Position;
        w.Write(0); // patched below
        w.Write(Encoding.ASCII.GetBytes("WAVE"));

        if (dataFirst)
        {
            Chunk("data", data);   // data before fmt: legal RIFF, must still parse
            Chunk("fmt ", fmtBody);
        }
        else
        {
            Chunk("fmt ", fmtBody);
            if (extra is { } e) Chunk(e.id, e.body);
            Chunk("data", data);
        }

        w.Flush();
        long end = ms.Position;
        ms.Position = sizePos;
        w.Write((int)(end - 8));
        w.Flush();
        return ms.ToArray();
    }
}
