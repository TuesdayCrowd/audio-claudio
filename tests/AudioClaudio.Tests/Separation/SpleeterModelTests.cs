using System;
using System.IO;
using AudioClaudio.Cli.Composition;
using AudioClaudio.Infrastructure.Separation;
using AudioClaudio.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace AudioClaudio.Tests.Separation;

/// <summary>
/// Stage 1.3 Component A — <see cref="SpleeterModel.RunLogits"/> in isolation from the STFT front
/// end. Uses the committed Stage-1.0 parity fixture (<c>fixtures/models/spleeter/parity/</c>): a
/// directly-committed input magnitude (no STFT needed to produce it) plus each branch's
/// TF-graph-traced reference logit, truncated to num_splits=1. This isolates "is the channel-first
/// (2, num_splits, 512, 1024) transpose right" from "is the DSP right" — the latter is what the
/// golden-WAV test in <see cref="SpleeterSourceSeparatorTests"/> covers.
/// </summary>
public class SpleeterModelTests
{
    private readonly ITestOutputHelper _out;

    public SpleeterModelTests(ITestOutputHelper output) => _out = output;

    private static string ParityDir => RepoPaths.Fixture("models", "spleeter", "parity");

    private static readonly string[] Stems = { "vocals", "piano", "drums", "bass", "other" };

    private static float[] ReadF32(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var arr = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, arr, 0, arr.Length * sizeof(float));
        return arr;
    }

    private static float[,,,] ToNhwc(float[] flat, int d0, int d1, int d2, int d3)
    {
        var result = new float[d0, d1, d2, d3];
        int idx = 0;
        for (int a = 0; a < d0; a++)
            for (int b = 0; b < d1; b++)
                for (int c = 0; c < d2; c++)
                    for (int d = 0; d < d3; d++)
                        result[a, b, c, d] = flat[idx++];
        return result;
    }

    [Fact]
    [Trait("Category", "Slow")] // loads all 5 ~37 MB ONNX models
    public void RunLogits_matches_the_committed_parity_fixture_for_all_five_stems()
    {
        float[] flatMagnitude = ReadF32(Path.Combine(ParityDir, "input_magnitude_nhwc.f32"));
        float[,,,] magnitude = ToNhwc(flatMagnitude, 1, 512, 1024, 2); // [num_splits=1, T=512, F=1024, C=2]

        using var model = new SpleeterModel(SeparatorModelLocator.Resolve(null));
        var logits = model.RunLogits(magnitude);

        Assert.Equal(Stems.Length, logits.Count);

        foreach ((string stem, int i) in Indexed(Stems))
        {
            float[] expectedFlat = ReadF32(Path.Combine(ParityDir, $"{stem}_logit_nhwc.f32"));
            float[,,,] actual = logits[i];
            Assert.Equal(1 * 512 * 1024 * 2, expectedFlat.Length);

            double maxAbs = 0.0;
            double sumSq = 0.0;
            int idx = 0;
            for (int t = 0; t < 512; t++)
                for (int f = 0; f < 1024; f++)
                    for (int ch = 0; ch < 2; ch++)
                    {
                        double diff = Math.Abs(actual[0, t, f, ch] - expectedFlat[idx++]);
                        maxAbs = Math.Max(maxAbs, diff);
                        sumSq += diff * diff;
                    }

            double rms = Math.Sqrt(sumSq / expectedFlat.Length);
            _out.WriteLine($"{stem}: maxAbsDiff={maxAbs:E3} rms={rms:E3}");

            // MODEL_CARD.md documents ~6.3e-4 max-abs ONNX-vs-TF logit parity over a value range of
            // roughly +-1000 (~5-6e-7 relative); this fixture's own manifest.json separately reports
            // per-branch onnx_vs_torch max_abs_err up to ~6.5e-4. Measured here: ~4.7e-4-6.4e-4 across
            // all 5 stems -- matches the documented bound almost exactly. Gate with modest margin.
            Assert.True(maxAbs < 2e-3, $"{stem}: max abs logit diff {maxAbs:E3} exceeds 2e-3");
        }
    }

    private static (string, int)[] Indexed(string[] items)
    {
        var result = new (string, int)[items.Length];
        for (int i = 0; i < items.Length; i++) result[i] = (items[i], i);
        return result;
    }
}
