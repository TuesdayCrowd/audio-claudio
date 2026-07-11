using System;
using System.IO;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Infrastructure.Transcription;
using AudioClaudio.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace AudioClaudio.Tests.Transcription;

/// <summary>
/// v2 Stage 4b — the C# Transkun mel front end matches PyTorch's <c>featuresBatch</c> (the committed
/// <c>ref3b</c> fixture: 1.5 s of the two-bar render → its features). Tolerance reflects cross-implementation
/// FFT drift (hand-rolled double radix-2 vs torch float32 rfft), the same precedent as the render golden.
/// </summary>
public class TranskunMelFrontEndTests
{
    private readonly ITestOutputHelper _out;

    public TranskunMelFrontEndTests(ITestOutputHelper output) => _out = output;

    private static string ModelDir => RepoPaths.Fixture("models", "transkun");

    private static float[] ReadF32(string name)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(ModelDir, name));
        var arr = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, arr, 0, arr.Length * sizeof(float));
        return arr;
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void FeaturesMatchTheRef3bFixture()
    {
        TranskunBuffers buffers = TranskunBuffers.Load(ModelDir);
        var frontEnd = new TranskunMelFrontEnd(buffers, new Radix2Fft());

        float[] audio = ReadF32("ref3b_audio.f32");
        float[] expected = ReadF32("ref3b_features.f32"); // [nFrame, 229, 6] row-major

        float[,,] actual = frontEnd.Compute(audio);
        int nFrame = actual.GetLength(0), nMels = actual.GetLength(1), nWin = actual.GetLength(2);
        _out.WriteLine($"features [{nFrame},{nMels},{nWin}] ({expected.Length} expected)");
        Assert.Equal(expected.Length, nFrame * nMels * nWin);

        double maxAbs = 0.0, sumSq = 0.0;
        int idx = 0;
        for (int f = 0; f < nFrame; f++)
        {
            for (int m = 0; m < nMels; m++)
            {
                for (int w = 0; w < nWin; w++)
                {
                    double diff = actual[f, m, w] - expected[idx++];
                    maxAbs = Math.Max(maxAbs, Math.Abs(diff));
                    sumSq += diff * diff;
                }
            }
        }

        double rms = Math.Sqrt(sumSq / expected.Length);
        _out.WriteLine($"maxAbsDiff={maxAbs:E3}  rms={rms:E3}");

        // Achieved maxAbs ~7e-6 / rms ~4e-7 (hand-rolled double FFT vs torch float32); gate with margin for
        // cross-platform (ARM64 dev vs x64 CI) drift.
        Assert.True(maxAbs < 1e-4, $"max abs diff {maxAbs:E3} exceeds 1e-4");
        Assert.True(rms < 1e-5, $"rms {rms:E3} exceeds 1e-5");
    }
}
