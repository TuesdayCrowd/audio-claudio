using System;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AudioClaudio.Infrastructure.Transcription;

/// <summary>
/// Runs the two Transkun attribute heads (v2 Stage 4e) — the exported <c>transkun-heads.onnx</c>: gathered
/// interval features <c>attr</c> <c>[N, 768]</c> (<c>[ctx_a, ctx_b, ctx_a·ctx_b]</c>) → the velocity logits
/// <c>[N, 128]</c> (<c>velocityPredictor</c>) and the raw onset/offset values <c>[N, 4]</c>
/// (<c>refinedOFPredictor</c>: two sub-frame value logits + two presence logits). Small MLPs (Linear→GELU→
/// Linear); this adds real velocity and sub-frame timing on top of the core-first frame decode.
/// </summary>
public sealed class TranskunHeads : IDisposable
{
    public const int AttrDim = 768;
    public const int VelocityClasses = 128;

    private readonly InferenceSession _session;

    public TranskunHeads(string onnxPath)
    {
        ArgumentNullException.ThrowIfNull(onnxPath);
        _session = new InferenceSession(onnxPath);
    }

    /// <summary>Run the heads on <paramref name="n"/> gathered rows (<paramref name="attr"/> flat
    /// <c>[n*768]</c>). Returns velocity logits flat <c>[n*128]</c> and the raw OF values flat <c>[n*4]</c>.</summary>
    public (float[] VelocityLogits, float[] OfRaw) Run(float[] attr, int n)
    {
        ArgumentNullException.ThrowIfNull(attr);
        if (attr.Length != (long)n * AttrDim)
        {
            throw new ArgumentException($"attr has {attr.Length} floats, expected {(long)n * AttrDim}.", nameof(attr));
        }

        var input = new DenseTensor<float>(new[] { n, AttrDim });
        attr.CopyTo(input.Buffer.Span);

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
            _session.Run(new[] { NamedOnnxValue.CreateFromTensor("attr", input) });
        float[] vel = results.First(r => r.Name == "velLogits").AsTensor<float>().ToArray();
        float[] of = results.First(r => r.Name == "ofRaw").AsTensor<float>().ToArray();
        return (vel, of);
    }

    public void Dispose() => _session.Dispose();
}
