using System;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AudioClaudio.Infrastructure.Transcription;

/// <summary>
/// Runs the exported Transkun transformer+scorer ONNX (v2 Stage 4): <c>featuresBatch</c>
/// <c>[nFrame, nMels, nWindows]</c> → the semi-CRF score matrix <c>S</c> <c>[T, T, 90]</c>, in-process via
/// <see cref="Microsoft.ML.OnnxRuntime"/> (the same runtime that ships Basic Pitch — no Python). The mel
/// front end (<see cref="TranskunMelFrontEnd"/>) and the Viterbi decode (<c>SemiCrfViterbi</c>) sit either
/// side of this. Returns <c>S</c> flattened row-major so it feeds the decoder directly.
/// </summary>
public sealed class TranskunModel : IDisposable
{
    private const string InputName = "featuresBatch";
    private const string OutputName = "S";

    private readonly InferenceSession _session;

    public TranskunModel(string onnxPath)
    {
        ArgumentNullException.ThrowIfNull(onnxPath);
        _session = new InferenceSession(onnxPath);
    }

    /// <summary>Run the model on one segment's features, returning <c>S</c> flat <c>[T*T*90]</c>
    /// (<c>S[e,b,k] = result[(e*T + b)*90 + k]</c>) and the frame count <c>T</c>.</summary>
    public float[] Run(float[,,] featuresBatch, out int t)
    {
        ArgumentNullException.ThrowIfNull(featuresBatch);
        int nFrame = featuresBatch.GetLength(0);
        int nMels = featuresBatch.GetLength(1);
        int nWin = featuresBatch.GetLength(2);

        var input = new DenseTensor<float>(new[] { 1, nFrame, nMels, nWin });
        int idx = 0;
        for (int f = 0; f < nFrame; f++)
        {
            for (int m = 0; m < nMels; m++)
            {
                for (int w = 0; w < nWin; w++)
                {
                    input.Buffer.Span[idx++] = featuresBatch[f, m, w];
                }
            }
        }

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
            _session.Run(new[] { NamedOnnxValue.CreateFromTensor(InputName, input) });
        Tensor<float> s = results.First(r => r.Name == OutputName).AsTensor<float>();
        t = s.Dimensions[0];
        return s.ToArray();
    }

    public void Dispose() => _session.Dispose();
}
