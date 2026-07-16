using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AudioClaudio.Application.Ports;
using AudioClaudio.Cli.Composition;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Separation;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Infrastructure.Separation;
using AudioClaudio.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace AudioClaudio.Tests.Separation;

/// <summary>
/// Stage 1.3 — the Spleeter ONNX adapter (<see cref="SpleeterModel"/> + <see cref="SpleeterSourceSeparator"/>).
/// The golden test is the load-bearing one: it validates the full ONNX + cross-branch-softmax + mask
/// path (the pieces this stage wires together) against a real TensorFlow reference
/// (<c>golden/masked_{stem}_nhwc.f32</c> = TF's <c>{stem}_spectrogram/mul</c>). Contract/determinism/
/// disposal round out the adapter's basic behavioral guarantees, mirroring how
/// <c>BasicPitchModelTests</c>/<c>TranskunParityTests</c> cover their own ONNX adapters.
/// </summary>
public class SpleeterSourceSeparatorTests
{
    private readonly ITestOutputHelper _out;

    public SpleeterSourceSeparatorTests(ITestOutputHelper output) => _out = output;

    private static string GoldenDir => RepoPaths.Fixture("models", "spleeter", "golden");
    private static string ModelDir => SeparatorModelLocator.Resolve(null);
    private static readonly string[] StemOrder = { "vocals", "piano", "drums", "bass", "other" };
    private const int ExpectedSamples = 88200; // 2.0 s @ 44100 Hz, per golden/manifest.json
    private const int StemFrameSize = 4096; // the FrameParameters SpleeterSourceSeparator wraps each stem in

    private static float[] ReadF32(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var arr = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, arr, 0, arr.Length * sizeof(float));
        return arr;
    }

    // Mirrors SpleeterStftTests' reader: the golden WAV is exactly one frame's worth of samples, so
    // a single Framing.Split call with size==hop==sample count returns the whole file untouched (no
    // tail zero-padding to account for).
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

    private static float[,,,] ToFloat(double[,,,] source)
    {
        int d0 = source.GetLength(0), d1 = source.GetLength(1), d2 = source.GetLength(2), d3 = source.GetLength(3);
        var result = new float[d0, d1, d2, d3];
        for (int a = 0; a < d0; a++)
            for (int b = 0; b < d1; b++)
                for (int c = 0; c < d2; c++)
                    for (int d = 0; d < d3; d++)
                        result[a, b, c, d] = (float)source[a, b, c, d];
        return result;
    }

    [Fact]
    [Trait("Category", "Slow")] // loads all 5 ONNX models + runs inference
    public void MaskedMagnitude_matches_the_committed_TF_golden_for_all_stems()
    {
        double[] mono = ReadMonoWavAsDouble(Path.Combine(GoldenDir, "test_input_mono.wav"), ExpectedSamples);

        var stft = new SpleeterStft(new Radix2Fft());
        SpleeterStftResult stftResult = stft.Analyze(left: mono, right: mono); // mono upmixed to L=R per manifest

        using var model = new SpleeterModel(ModelDir);
        IReadOnlyList<float[,,,]> logits = model.RunLogits(ToFloat(stftResult.CroppedMagnitude));
        IReadOnlyList<double[,,,]> estimated = SpleeterMasking.Softmax(logits, stftResult.CroppedMagnitude);

        int numSplits = stftResult.CroppedMagnitude.GetLength(0);
        int t = stftResult.CroppedMagnitude.GetLength(1);
        int f = stftResult.CroppedMagnitude.GetLength(2);
        int c = stftResult.CroppedMagnitude.GetLength(3);

        for (int k = 0; k < StemOrder.Length; k++)
        {
            float[] expectedFlat = ReadF32(Path.Combine(GoldenDir, $"masked_{StemOrder[k]}_nhwc.f32"));
            Assert.Equal(expectedFlat.Length, numSplits * t * f * c);

            double peak = 0.0;
            for (int i = 0; i < expectedFlat.Length; i++) peak = Math.Max(peak, Math.Abs(expectedFlat[i]));
            // Same 1%-of-golden-peak relative floor as SpleeterStftTests -- avoids a near-silent-bin
            // "50% relative error" false alarm while still catching a real scale bug.
            double relFloor = peak * 0.01;

            double maxAbs = 0.0, maxRel = 0.0, sumSq = 0.0;
            int idx = 0;
            for (int s = 0; s < numSplits; s++)
                for (int ti = 0; ti < t; ti++)
                    for (int fi = 0; fi < f; fi++)
                        for (int ci = 0; ci < c; ci++)
                        {
                            double actual = estimated[k][s, ti, fi, ci];
                            double expected = expectedFlat[idx++];
                            double diff = Math.Abs(actual - expected);
                            maxAbs = Math.Max(maxAbs, diff);
                            maxRel = Math.Max(maxRel, diff / (Math.Abs(expected) + relFloor));
                            sumSq += diff * diff;
                        }

            double rms = Math.Sqrt(sumSq / expectedFlat.Length);
            _out.WriteLine(
                $"{StemOrder[k]}: peak={peak:F3} maxAbs={maxAbs:E3} maxRel(1%-of-peak floor)={maxRel:E3} rms={rms:E3}");

            // MODEL_CARD.md documents mean ~1e-6 with max spikes up to ~0.05 at softmax "near-ties"
            // (float32 logit noise locally amplified, ~0.37% of pixels) -- intrinsic to softmax
            // reconstruction, not a bug. Measured here (this deterministic-tones fixture): maxAbs
            // 3.4e-4 to 3.8e-3, maxRel 2.8e-4 to 1.1e-3, rms 2.5e-6 to 2.1e-5 across all 5 stems --
            // comfortably inside the documented ceiling. Gate with margin above both the documented
            // near-tie ceiling and what's actually measured.
            Assert.True(maxAbs < 0.1, $"{StemOrder[k]}: max abs diff {maxAbs:E3} exceeds 0.1");
            Assert.True(maxRel < 0.05, $"{StemOrder[k]}: max relative diff {maxRel:E3} (1%-of-peak floor) exceeds 0.05");
            Assert.True(rms < 1e-3, $"{StemOrder[k]}: rms {rms:E3} exceeds 1e-3");
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Separate_returns_five_named_stems_with_reconstructed_length_approximately_matching_the_input()
    {
        using var source = WavAudioSource.FromFile(
            Path.Combine(GoldenDir, "test_input_mono.wav"), new FrameParameters(ExpectedSamples, ExpectedSamples));
        using var separator = new SpleeterSourceSeparator(ModelDir, new Radix2Fft());

        IReadOnlyList<SeparatedStem> stems = separator.Separate(source);

        Assert.Equal(5, stems.Count);
        Assert.Equal(StemOrder, stems.Select(s => s.Name).ToArray());

        foreach (SeparatedStem stem in stems)
        {
            float[] pcm = Framing.ReconstructMono(stem.Audio.Frames.ToList());
            Assert.NotEmpty(pcm);
            // Each stem is wrapped in a PcmAudioSource(FrameParameters(4096,1024)); reading it back
            // out via Framing.Split -> ReconstructMono zero-pads the final (hop < size) frame's tail,
            // so the round-tripped length can exceed the true reconstructed length by up to one frame
            // -- an artifact of this length-recovery method, not of SpleeterReconstruction itself
            // (which truncates to the exact original sample count internally). Hence "approximately."
            Assert.InRange(pcm.Length, ExpectedSamples, ExpectedSamples + StemFrameSize);
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Separate_is_deterministic_on_the_same_machine()
    {
        // ONNX Runtime's SIMD mixdown is a same-machine guarantee, not cross-architecture
        // bit-exactness -- the same caveat PolyphonicClosedLoopTests documents for Basic Pitch.
        using var separator = new SpleeterSourceSeparator(ModelDir, new Radix2Fft());

        IReadOnlyList<SeparatedStem> Run()
        {
            using var source = WavAudioSource.FromFile(
                Path.Combine(GoldenDir, "test_input_mono.wav"), new FrameParameters(4096, 1024));
            return separator.Separate(source);
        }

        IReadOnlyList<SeparatedStem> first = Run();
        IReadOnlyList<SeparatedStem> second = Run();

        Assert.Equal(first.Count, second.Count);
        for (int k = 0; k < first.Count; k++)
        {
            float[] a = Framing.ReconstructMono(first[k].Audio.Frames.ToList());
            float[] b = Framing.ReconstructMono(second[k].Audio.Frames.ToList());
            Assert.Equal(a.Length, b.Length);
            Assert.True(a.AsSpan().SequenceEqual(b), $"stem {first[k].Name} was not bit-identical across two runs");
        }
    }

    [Fact]
    [Trait("Category", "Slow")] // loads all 5 ONNX models
    public void Dispose_releases_the_sessions_without_error()
    {
        var separator = new SpleeterSourceSeparator(ModelDir, new Radix2Fft());
        separator.Dispose();
    }
}
