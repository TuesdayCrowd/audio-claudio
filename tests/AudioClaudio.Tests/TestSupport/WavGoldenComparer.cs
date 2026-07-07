using System;
using Xunit;

namespace AudioClaudio.Tests.TestSupport;

/// <summary>
/// Tolerance-based comparison between two canonical 16-bit-PCM-mono WAV byte buffers
/// (as produced by <c>AudioClaudio.Infrastructure.Audio.WavFileWriter</c>), used by the
/// Step 8 synthesis golden instead of a byte-exact SHA-256.
/// </summary>
/// <remarks>
/// <para>
/// Synthesis is float DSP, and MeltySynth's mixing (<c>ArrayMath.MultiplyAdd</c>) is
/// vectorized via <c>System.Numerics.Vector&lt;float&gt;</c>, whose SIMD width varies by
/// CPU architecture (e.g. 4 lanes on ARM64/NEON vs. potentially 8 on x64/AVX2). Because
/// float addition is not associative, a different vector width changes the accumulation
/// order and can shift the least-significant bits of the result — so a byte-exact hash
/// pinned on one architecture is not guaranteed to reproduce on another, even though the
/// synthesizer is fully deterministic on a single build/machine (proved separately by
/// <c>SynthesisDeterminismTests</c>). This comparer instead bounds the worst-case
/// per-sample drift.
/// </para>
/// <para>
/// Threshold: <see cref="MaxAbsoluteSampleDifference"/> = 1e-3 (normalized to the
/// [-1, 1] float domain; about -60 dBFS, i.e. roughly 33 int16 codes out of 32767).
/// Cross-architecture float jitter from SIMD-width differences accumulates over a modest
/// number of per-block mix operations and is expected to be many orders of magnitude
/// smaller than this (single-ULP-scale per operation); a genuine synthesis regression
/// (wrong note, wrong timing, wrong envelope) would instead produce a completely
/// different waveform with a max difference on the order of the signal's own amplitude
/// (tens of thousands of int16 codes), not a handful. This machine is ARM64 (macOS); the
/// threshold has not been exercised against an actual x64 CI run yet — the first CI run
/// is the real cross-architecture check (see DECISIONS.md).
/// </para>
/// </remarks>
public static class WavGoldenComparer
{
    public const double MaxAbsoluteSampleDifference = 1e-3;

    /// <summary>Canonical WavFileWriter output has no extra chunks: 44-byte header, then 16-bit samples.</summary>
    private const int HeaderBytes = 44;

    public static void AssertWithinTolerance(
        byte[] expectedWav, byte[] actualWav, double maxAbsoluteDifference = MaxAbsoluteSampleDifference)
    {
        Assert.Equal(expectedWav.Length, actualWav.Length);

        short[] expected = ReadInt16Samples(expectedWav);
        short[] actual = ReadInt16Samples(actualWav);

        double maxDiff = 0.0;
        int maxDiffIndex = -1;
        for (int i = 0; i < expected.Length; i++)
        {
            double diff = Math.Abs(expected[i] - actual[i]) / 32767.0;
            if (diff > maxDiff)
            {
                maxDiff = diff;
                maxDiffIndex = i;
            }
        }

        Assert.True(maxDiff <= maxAbsoluteDifference,
            $"max normalized sample difference {maxDiff:E3} at sample {maxDiffIndex} exceeds " +
            $"tolerance {maxAbsoluteDifference:E3} (expected {expected[Math.Max(maxDiffIndex, 0)]}, " +
            $"actual {actual[Math.Max(maxDiffIndex, 0)]}).");
    }

    private static short[] ReadInt16Samples(byte[] wav)
    {
        int count = (wav.Length - HeaderBytes) / 2;
        var samples = new short[count];
        for (int i = 0; i < count; i++)
            samples[i] = BitConverter.ToInt16(wav, HeaderBytes + i * 2);
        return samples;
    }
}
