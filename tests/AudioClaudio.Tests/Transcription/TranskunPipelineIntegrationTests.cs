using System;
using System.IO;
using System.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Infrastructure.Transcription;
using AudioClaudio.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace AudioClaudio.Tests.Transcription;

/// <summary>
/// v2 Stage 4a–4c end-to-end, in C# with no Python: the committed <c>ref3b</c> audio → the C# mel front end
/// → the exported ONNX run in onnxruntime → the C# semi-CRF Viterbi decode → the real E4 note. Proves the
/// three pieces compose and that the committed model actually runs in the C# runtime (not just Python).
/// </summary>
public class TranskunPipelineIntegrationTests
{
    private readonly ITestOutputHelper _out;

    public TranskunPipelineIntegrationTests(ITestOutputHelper output) => _out = output;

    private static string ModelDir => RepoPaths.Fixture("models", "transkun");

    private static float[] ReadF32(string name)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(ModelDir, name));
        var arr = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, arr, 0, arr.Length * sizeof(float));
        return arr;
    }

    [Fact]
    [Trait("Category", "Slow")] // loads the 53 MB ONNX
    public void AudioToNotes_EndToEnd_RecoversTheE4Note()
    {
        TranskunBuffers buffers = TranskunBuffers.Load(ModelDir);
        var frontEnd = new TranskunMelFrontEnd(buffers, new Radix2Fft());

        float[] audio = ReadF32("ref3b_audio.f32");
        float[,,] features = frontEnd.Compute(audio);

        using var model = new TranskunModel(Path.Combine(ModelDir, "transkun.onnx"));
        float[] s = model.Run(features, out int t);
        Assert.Equal(features.GetLength(0), t);

        // The C# S (via the C# mel) tracks the reference Python S (whose mel differed by ~7e-6); the small
        // front-end drift stays small through the model — and, decisively, does not change the decode.
        float[] refS = ReadF32("ref3c_S.f32");
        Assert.Equal(refS.Length, s.Length);
        double maxAbs = 0.0;
        for (int i = 0; i < s.Length; i++)
        {
            maxAbs = Math.Max(maxAbs, Math.Abs(s[i] - refS[i]));
        }

        _out.WriteLine($"T={t}  S maxAbsDiff vs ref (Python)={maxAbs:E3}");

        var decoded = SemiCrfViterbi.Decode(s, t, buffers.Symbols.Length);
        int notes = decoded.Sum(track => track.Count);
        _out.WriteLine($"decoded {notes} interval(s); track 45 (E4) = [{string.Join(",", decoded[45])}]");

        // The whole chain recovers exactly the one note the reference did: E4 (track 45), frames 43..63.
        Assert.Equal(new[] { new SemiCrfViterbi.Interval(43, 63) }, decoded[45]);
        Assert.Equal(1, notes);
    }
}
