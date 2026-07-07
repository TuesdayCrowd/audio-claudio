using System;
using System.IO;
using AudioClaudio.Domain;

namespace AudioClaudio.Infrastructure.Audio;

/// <summary>
/// Writes mono float PCM (values clamped to [-1, 1]) as a canonical 16-bit PCM WAV.
/// Deterministic: <see cref="BinaryWriter"/> emits little-endian on every platform, and
/// the float-to-short conversion is a plain clamp+round (<see cref="MathF.Round"/>'s
/// default banker's rounding — deterministic, no incidental tie-break).
/// </summary>
/// <remarks>
/// This is the <b>production</b> WAV writer the CLI ships (R8.3); it is distinct from
/// the test-only <c>AudioClaudio.Tests.Signals.WavWriter</c> so the CLI never depends on
/// test utilities. The two use slightly different full-scale conventions by design: this
/// writer scales by 32767 (not 2^15) so +1.0 and -1.0 map to symmetric +32767/-32767
/// codes, rather than reserving an asymmetric -32768 that a [-1, 1]-clamped signal never
/// reaches on the positive side anyway.
/// </remarks>
public static class WavFileWriter
{
    public static void Write(string path, ReadOnlySpan<float> monoPcm, SampleRate sampleRate)
        => File.WriteAllBytes(path, ToBytes(monoPcm, sampleRate));

    public static byte[] ToBytes(ReadOnlySpan<float> monoPcm, SampleRate sampleRate)
    {
        const int channels = 1;
        const int bitsPerSample = 16;
        int sampleRateHz = sampleRate.Hz;
        int blockAlign = channels * bitsPerSample / 8;
        int byteRate = sampleRateHz * blockAlign;
        int dataBytes = monoPcm.Length * blockAlign;

        using var ms = new MemoryStream(44 + dataBytes);
        using var w = new BinaryWriter(ms);

        // RIFF header
        w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
        w.Write(36 + dataBytes);
        w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });

        // fmt chunk
        w.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
        w.Write(16);              // PCM fmt chunk size
        w.Write((short)1);        // audio format = PCM
        w.Write((short)channels);
        w.Write(sampleRateHz);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write((short)bitsPerSample);

        // data chunk
        w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
        w.Write(dataBytes);
        foreach (float x in monoPcm)
        {
            float clamped = Math.Clamp(x, -1f, 1f);
            int s = (int)MathF.Round(clamped * 32767f);
            w.Write((short)Math.Clamp(s, short.MinValue, short.MaxValue));
        }

        w.Flush();
        return ms.ToArray();
    }
}
