using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;

namespace AudioClaudio.Infrastructure.Audio;

/// <summary>
/// Hand-rolled 16/24-bit PCM WAV adapter (R2.2). Walks the RIFF chunk list (chunks may appear in
/// any order), downmixes multichannel to mono by averaging, and frames the result via the Domain
/// splitter. All reads are little-endian. No dependency is taken for parsing. Disposable: it owns
/// (and closes) the stream only when it opened it itself via <see cref="FromFile"/>.
/// </summary>
public sealed class WavAudioSource : IAudioSource, IDisposable
{
    private readonly IReadOnlyList<Frame> _frames;
    private readonly Stream? _ownedStream;

    public IEnumerable<Frame> Frames => _frames;

    public WavAudioSource(Stream wav, FrameParameters parameters)
        : this(wav, parameters, ownsStream: false)
    {
    }

    private WavAudioSource(Stream wav, FrameParameters parameters, bool ownsStream)
    {
        if (wav is null) throw new ArgumentNullException(nameof(wav));
        var (mono, rate) = Decode(wav);
        _frames = Framing.Split(mono, rate, parameters);
        _ownedStream = ownsStream ? wav : null; // a caller-passed stream stays the caller's to close
    }

    /// <summary>Convenience for the composition root: open a file and read it. The returned source
    /// OWNS the opened stream and closes it on <see cref="Dispose"/>.</summary>
    public static WavAudioSource FromFile(string path, FrameParameters parameters)
        => new WavAudioSource(File.OpenRead(path), parameters, ownsStream: true);

    /// <summary>Closes the stream this source opened itself (via <see cref="FromFile"/>); a stream
    /// handed to the public constructor is owned by the caller and left open.</summary>
    public void Dispose() => _ownedStream?.Dispose();

    private static (float[] mono, SampleRate rate) Decode(Stream stream)
    {
        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            stream.CopyTo(ms);       // MVP WAV fixtures are small; read whole then parse
            bytes = ms.ToArray();
        }
        var span = new ReadOnlySpan<byte>(bytes);

        if (span.Length < 12 || !Matches(span.Slice(0, 4), "RIFF") || !Matches(span.Slice(8, 4), "WAVE"))
            throw new InvalidDataException("Not a RIFF/WAVE file.");

        int audioFormat = 0, channels = 0, sampleRate = 0, bitsPerSample = 0;
        ReadOnlySpan<byte> data = default;
        bool haveFmt = false, haveData = false;

        int pos = 12; // past "RIFF" <size> "WAVE"
        while (pos + 8 <= span.Length)
        {
            var id = span.Slice(pos, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 4, 4));
            int body = pos + 8;
            if (size < 0 || body + size > span.Length)
                size = span.Length - body; // tolerate a bad/streaming RIFF size on the final chunk

            if (Matches(id, "fmt "))
            {
                var f = span.Slice(body, size);
                audioFormat = BinaryPrimitives.ReadInt16LittleEndian(f.Slice(0, 2));
                channels = BinaryPrimitives.ReadInt16LittleEndian(f.Slice(2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(f.Slice(4, 4));
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(f.Slice(14, 2));
                haveFmt = true;
            }
            else if (Matches(id, "data"))
            {
                data = span.Slice(body, size);
                haveData = true;
            }
            // else: skip unknown chunk (LIST, fact, ...)

            pos = body + size + (size & 1); // chunks are word-aligned: pad byte after an odd size
        }

        if (!haveFmt) throw new InvalidDataException("WAV is missing its 'fmt ' chunk.");
        if (!haveData) throw new InvalidDataException("WAV is missing its 'data' chunk.");
        if (audioFormat != 1)
            throw new NotSupportedException($"Only PCM (format 1) is supported; got audio format {audioFormat}.");
        if (bitsPerSample != 16 && bitsPerSample != 24)
            throw new NotSupportedException($"Only 16-bit and 24-bit PCM are supported; got {bitsPerSample}-bit.");
        if (channels < 1)
            throw new InvalidDataException($"Channel count must be >= 1; got {channels}.");

        int bytesPerSample = bitsPerSample / 8;
        int blockAlign = channels * bytesPerSample;
        int frameCount = data.Length / blockAlign;
        double scale = System.Math.Pow(2, bitsPerSample - 1); // 32768 or 8388608

        var mono = new float[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            int baseOffset = i * blockAlign;
            double sum = 0.0;
            for (int c = 0; c < channels; c++)
            {
                int off = baseOffset + c * bytesPerSample;
                int sample = bitsPerSample == 16
                    ? BinaryPrimitives.ReadInt16LittleEndian(data.Slice(off, 2))
                    : ReadInt24LittleEndian(data.Slice(off, 3));
                sum += sample / scale;
            }
            mono[i] = (float)(sum / channels); // downmix = arithmetic mean of channels
        }

        return (mono, new SampleRate(sampleRate));
    }

    private static int ReadInt24LittleEndian(ReadOnlySpan<byte> b)
    {
        int v = b[0] | (b[1] << 8) | (b[2] << 16);
        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000); // sign-extend the 24th bit
        return v;
    }

    private static bool Matches(ReadOnlySpan<byte> b, string ascii)
        => b.Length == ascii.Length
        && b[0] == ascii[0] && b[1] == ascii[1] && b[2] == ascii[2] && b[3] == ascii[3];
}
