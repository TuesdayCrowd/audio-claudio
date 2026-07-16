using System;
using System.Collections.Generic;
using System.IO;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Separation;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace AudioClaudio.Tests.Separation;

/// <summary>
/// Stage 1.1b — the Spleeter STFT front end must reproduce Deezer Spleeter's exact analysis
/// (44100 Hz, n_fft=4096, hop=1024, periodic Hann, a leading full-frame zero pad, rfft to 2049
/// bins cropped to F=1024, time padded/partitioned to T=512) closely enough that the committed
/// golden magnitude (from the real TF graph) matches within a tight tolerance. This is THE
/// arbiter for whether every DSP parameter is right; a wrong parameter (window periodicity, the
/// leading pad, the bin crop, the partition scheme) should blow the tolerance open, not sneak
/// through as "close-ish".
/// </summary>
public class SpleeterStftTests
{
    private readonly ITestOutputHelper _out;

    public SpleeterStftTests(ITestOutputHelper output) => _out = output;

    private static string GoldenDir => RepoPaths.Fixture("models", "spleeter", "golden");

    private static float[] ReadF32(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var arr = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, arr, 0, arr.Length * sizeof(float));
        return arr;
    }

    // The golden fixture is a committed synthetic WAV of exactly 2.0 s at 44100 Hz = 88200
    // mono samples (verified against its own 'data' chunk size) -- one frame sized to the exact
    // sample count reads the whole file with no Framing zero-pad tail to account for.
    private static double[] ReadMonoWavAsDouble(string path, int expectedSamples)
    {
        using var source = WavAudioSource.FromFile(path, new FrameParameters(expectedSamples, expectedSamples));
        IReadOnlyList<Frame> frames = AudioSources.Collect(source);
        Assert.Single(frames);
        float[] samples = frames[0].Samples;
        Assert.Equal(expectedSamples, samples.Length);

        var result = new double[expectedSamples];
        for (int i = 0; i < expectedSamples; i++) result[i] = samples[i];
        return result;
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void CroppedMagnitude_matches_the_committed_TF_golden()
    {
        double[] mono = ReadMonoWavAsDouble(Path.Combine(GoldenDir, "test_input_mono.wav"), expectedSamples: 88200);

        var stft = new SpleeterStft(new Radix2Fft());
        SpleeterStftResult result = stft.Analyze(left: mono, right: mono); // mono upmixed to L=R per manifest

        float[] expectedFlat = ReadF32(Path.Combine(GoldenDir, "magnitude_nhwc.f32")); // [1,512,1024,2] C-order

        int numSplits = result.CroppedMagnitude.GetLength(0);
        int t = result.CroppedMagnitude.GetLength(1);
        int f = result.CroppedMagnitude.GetLength(2);
        int c = result.CroppedMagnitude.GetLength(3);
        _out.WriteLine($"produced shape [{numSplits},{t},{f},{c}]; golden flat length {expectedFlat.Length}");

        Assert.Equal(1, numSplits);
        Assert.Equal(512, t);
        Assert.Equal(1024, f);
        Assert.Equal(2, c);
        Assert.Equal(expectedFlat.Length, numSplits * t * f * c);

        double peak = 0.0;
        for (int i = 0; i < expectedFlat.Length; i++) peak = Math.Max(peak, Math.Abs(expectedFlat[i]));

        double maxAbs = 0.0;
        double maxRel = 0.0;
        double sumSq = 0.0;
        // A fixed-point relative floor (like 1e-3) is dominated by near-silent bins that are
        // physically meaningless (e.g. actual=0.0011 vs expected=0.0005 reads as ~50% "relative"
        // error despite both being noise floor). Floor the denominator at 1% of the golden's own
        // peak magnitude instead (a -40 dB noise floor) so the relative metric reflects
        // perceptually/numerically meaningful bins, while a real scale bug (the original
        // leading-pad mistake measured maxRel ~2.4e5 against this same floor) still blows it open.
        double relFloor = peak * 0.01;
        int idx = 0;
        for (int s = 0; s < numSplits; s++)
        {
            for (int ti = 0; ti < t; ti++)
            {
                for (int fi = 0; fi < f; fi++)
                {
                    for (int ci = 0; ci < c; ci++)
                    {
                        double actual = result.CroppedMagnitude[s, ti, fi, ci];
                        double expected = expectedFlat[idx++];
                        double diff = Math.Abs(actual - expected);
                        maxAbs = Math.Max(maxAbs, diff);
                        double rel = diff / (Math.Abs(expected) + relFloor);
                        maxRel = Math.Max(maxRel, rel);
                        sumSq += diff * diff;
                    }
                }
            }
        }

        double rms = Math.Sqrt(sumSq / expectedFlat.Length);
        _out.WriteLine($"golden peak magnitude={peak:F3}");
        _out.WriteLine($"maxAbsDiff={maxAbs:E3}  maxRelDiff (1%-of-peak floor)={maxRel:E3}  rms={rms:E3}");

        // Achieved (double-precision C# FFT vs TF's float32 STFT, no leading pad -- see
        // SpleeterStft's remarks): maxAbs ~1.6e-3, rms ~2.5e-5, against a peak magnitude ~448 --
        // float32-precision-level agreement. Gate with margin for cross-platform FFT drift.
        Assert.True(maxAbs < 1e-2, $"max abs diff {maxAbs:E3} exceeds 1e-2 -- a DSP parameter is likely wrong");
        Assert.True(rms < 1e-3, $"rms {rms:E3} exceeds 1e-3 -- a DSP parameter is likely wrong");
        Assert.True(maxRel < 1e-2, $"max relative diff {maxRel:E3} (1%-of-peak floor) exceeds 1e-2 -- a DSP parameter is likely wrong");
    }
}
