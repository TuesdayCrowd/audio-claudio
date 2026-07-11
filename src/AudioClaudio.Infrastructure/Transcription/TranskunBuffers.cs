using System.IO;
using System.Text.Json;

namespace AudioClaudio.Infrastructure.Transcription;

/// <summary>
/// The frozen constants of the Transkun front end (v2 Stage 4), loaded from the committed
/// <c>fixtures/models/transkun/</c> buffers: the mel filterbank, the six analysis windows, the 90-symbol
/// track map, and the scalar params. Raw little-endian float32/int32 (matching the export's
/// <c>.tofile</c>), so the load is a byte copy on the little-endian platforms this project targets.
/// </summary>
public sealed class TranskunBuffers
{
    /// <summary>Mel filterbank, row-major <c>[rfftBins, nMels]</c> (index <c>k*nMels + m</c>).</summary>
    public float[] Freq2Mels { get; }

    /// <summary>The six analysis windows, <c>[nWindows][windowSize]</c> (row 0 Hann, 1–5 learned Gaussian).</summary>
    public float[][] Windows { get; }

    /// <summary>The 90 track symbols: <c>[-64, -67, 21..108]</c> (0 = sustain CC64, 1 = soft CC67, 2–89 = MIDI 21–108).</summary>
    public int[] Symbols { get; }

    public TranskunParams Params { get; }

    private TranskunBuffers(float[] freq2mels, float[][] windows, int[] symbols, TranskunParams p)
    {
        Freq2Mels = freq2mels;
        Windows = windows;
        Symbols = symbols;
        Params = p;
    }

    /// <summary>Load the buffers from a Transkun model directory (the one holding <c>transkun.onnx</c>).</summary>
    public static TranskunBuffers Load(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        TranskunParams p = JsonSerializer.Deserialize<TranskunParams>(
            File.ReadAllText(Path.Combine(directory, "params.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("params.json did not deserialize.");

        float[] freq2mels = ReadFloats(Path.Combine(directory, "freq2mels.f32"));
        if (freq2mels.Length != p.RfftBins * p.NMels)
        {
            throw new InvalidDataException($"freq2mels has {freq2mels.Length} floats, expected {p.RfftBins * p.NMels}.");
        }

        float[] flatWindows = ReadFloats(Path.Combine(directory, "windows.f32"));
        var windows = new float[p.NWindows][];
        for (int w = 0; w < p.NWindows; w++)
        {
            windows[w] = new float[p.WindowSize];
            System.Array.Copy(flatWindows, w * p.WindowSize, windows[w], 0, p.WindowSize);
        }

        int[] symbols = ReadInts(Path.Combine(directory, "symbols.i32"));
        return new TranskunBuffers(freq2mels, windows, symbols, p);
    }

    private static float[] ReadFloats(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var arr = new float[bytes.Length / sizeof(float)];
        System.Buffer.BlockCopy(bytes, 0, arr, 0, arr.Length * sizeof(float));
        return arr;
    }

    private static int[] ReadInts(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var arr = new int[bytes.Length / sizeof(int)];
        System.Buffer.BlockCopy(bytes, 0, arr, 0, arr.Length * sizeof(int));
        return arr;
    }
}

/// <summary>Scalar Transkun front-end parameters (from <c>params.json</c>).</summary>
public sealed record TranskunParams
{
    public int Fs { get; init; }
    public int WindowSize { get; init; }
    public int HopSize { get; init; }
    public int NMels { get; init; }
    public int NWindows { get; init; }
    public double Eps { get; init; }
    public double FMin { get; init; }
    public double FMax { get; init; }
    public int RfftBins { get; init; }
    public double SegmentSizeSeconds { get; init; }
    public double SegmentHopSeconds { get; init; }
    public int NSymbols { get; init; }
}
