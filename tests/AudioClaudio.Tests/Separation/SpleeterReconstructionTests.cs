using System;
using AudioClaudio.Domain.Separation;
using AudioClaudio.Domain.Spectral;
using Xunit;
using Xunit.Abstractions;

namespace AudioClaudio.Tests.Separation;

/// <summary>
/// Stage 1.1c — <see cref="SpleeterReconstruction"/> must be the correct inverse of
/// <see cref="SpleeterStft"/>: <c>Reconstruct(Stft(x)) ≈ x</c> in the interior.
/// <see cref="SpleeterStft"/> frames directly from sample 0 (no leading zero pad — see its
/// remarks on the MODEL_CARD deviation, empirically required to match the committed golden), so
/// 4x-overlap (hop = frame_length/4) support only becomes complete once the window has slid
/// frame_length−hop samples in; "interior" here excludes that ramp-up at the head and the
/// mirror-image ramp-down at the tail (where the analysis-side pad_end zero-padding of the final
/// partial frame also leaves a short edge transient) — both are real, expected edge effects of
/// finite-length OLA, not reconstruction bugs.
/// </summary>
public class SpleeterReconstructionTests
{
    private readonly ITestOutputHelper _out;

    public SpleeterReconstructionTests(ITestOutputHelper output) => _out = output;

    [Fact]
    [Trait("Category", "Fast")]
    public void Reconstruct_of_Analyze_recovers_a_two_tone_stereo_signal()
    {
        const double sampleRate = 44100.0;
        const int length = 4 * SpleeterStft.FrameLength; // a few x frame_length, per spec

        // Distinct tones per channel (plus a partial second tone) so the two channels are
        // genuinely independent, not just the same signal copied twice.
        var left = new double[length];
        var right = new double[length];
        for (int i = 0; i < length; i++)
        {
            left[i] = 0.6 * Math.Sin(2.0 * Math.PI * 261.63 * i / sampleRate)
                    + 0.2 * Math.Sin(2.0 * Math.PI * 440.0 * i / sampleRate);
            right[i] = 0.5 * Math.Sin(2.0 * Math.PI * 523.25 * i / sampleRate);
        }

        var fft = new Radix2Fft();
        var stft = new SpleeterStft(fft);
        SpleeterStftResult analyzed = stft.Analyze(left, right);

        var reconstruction = new SpleeterReconstruction(fft);
        double[] leftBack = reconstruction.Reconstruct(analyzed.LeftStft, length);
        double[] rightBack = reconstruction.Reconstruct(analyzed.RightStft, length);

        Assert.Equal(length, leftBack.Length);
        Assert.Equal(length, rightBack.Length);

        // Exclude the first/last (frame_length - hop) samples: the head hasn't reached full 4x
        // overlap yet, and the tail's analysis-side pad_end zero-padded the final partial frame
        // -- both are known, expected edge effects of finite-length OLA, not reconstruction bugs
        // (see class remarks).
        int edgeExclusion = SpleeterStft.FrameLength - SpleeterStft.Hop;
        int interiorStart = edgeExclusion;
        int interiorEnd = length - edgeExclusion;

        double maxAbsLeft = MaxAbsError(leftBack, left, interiorStart, interiorEnd);
        double maxAbsRight = MaxAbsError(rightBack, right, interiorStart, interiorEnd);
        _out.WriteLine($"interior [{interiorStart},{interiorEnd}) of {length}: maxAbsError left={maxAbsLeft:E3} right={maxAbsRight:E3}");

        Assert.True(maxAbsLeft < 1e-6, $"left channel max abs round-trip error {maxAbsLeft:E3} exceeds 1e-6");
        Assert.True(maxAbsRight < 1e-6, $"right channel max abs round-trip error {maxAbsRight:E3} exceeds 1e-6");
    }

    private static double MaxAbsError(double[] actual, double[] expected, int start, int end)
    {
        double max = 0.0;
        for (int i = start; i < end; i++)
        {
            max = Math.Max(max, Math.Abs(actual[i] - expected[i]));
        }

        return max;
    }
}
