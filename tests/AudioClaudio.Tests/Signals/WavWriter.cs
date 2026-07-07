using System.IO;
using System.Text;
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.Signals;

/// <summary>
/// Serialises mono/stereo float buffers to canonical little-endian PCM WAV bytes (R2.3).
/// PCM convention (see plan Approach): q = round(x · 2^(bits-1)), clamped to the signed range;
/// the reader divides by the same 2^(bits-1), so the round-trip is exact on the quantisation grid.
/// </summary>
public static class WavWriter
{
    public static byte[] Write16(float[] mono, SampleRate rate) => Write(new[] { mono }, rate, 16);

    public static byte[] Write24(float[] mono, SampleRate rate) => Write(new[] { mono }, rate, 24);

    public static byte[] Write16Stereo(float[] left, float[] right, SampleRate rate)
    {
        if (left.Length != right.Length) throw new ArgumentException("Channel lengths must match.");
        return Write(new[] { left, right }, rate, 16);
    }

    /// <summary>Convenience for Steps 9/10: render a mono buffer straight to a .wav file on disk —
    /// a thin <c>File.WriteAllBytes</c> over <see cref="Write16"/>.</summary>
    public static void WriteMonoFile(string path, float[] mono, SampleRate rate)
        => File.WriteAllBytes(path, Write16(mono, rate));

    private static byte[] Write(float[][] channels, SampleRate rate, int bitsPerSample)
    {
        int channelCount = channels.Length;
        int frameCount = channels[0].Length;
        int bytesPerSample = bitsPerSample / 8;
        int blockAlign = channelCount * bytesPerSample;
        int dataBytes = frameCount * blockAlign;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms); // BinaryWriter is little-endian on every platform

        w.Write(Ascii("RIFF"));
        w.Write(36 + dataBytes);        // "WAVE"(4) + fmt chunk(8+16) + data header(8) + data
        w.Write(Ascii("WAVE"));

        w.Write(Ascii("fmt "));
        w.Write(16);                    // PCM fmt chunk size
        w.Write((short)1);              // audio format 1 == PCM
        w.Write((short)channelCount);
        w.Write(rate.Hz);
        w.Write(rate.Hz * blockAlign); // byte rate
        w.Write((short)blockAlign);
        w.Write((short)bitsPerSample);

        w.Write(Ascii("data"));
        w.Write(dataBytes);

        double scale = System.Math.Pow(2, bitsPerSample - 1); // 32768 or 8388608
        int max = (int)scale - 1;
        int min = -(int)scale;

        for (int i = 0; i < frameCount; i++)
            for (int c = 0; c < channelCount; c++)
            {
                int q = (int)System.Math.Round(channels[c][i] * scale);
                if (q > max) q = max;
                if (q < min) q = min;

                if (bitsPerSample == 16)
                {
                    w.Write((short)q);
                }
                else // 24-bit: low three bytes, little-endian
                {
                    w.Write((byte)(q & 0xFF));
                    w.Write((byte)((q >> 8) & 0xFF));
                    w.Write((byte)((q >> 16) & 0xFF));
                }
            }

        w.Flush();
        return ms.ToArray();
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
}
