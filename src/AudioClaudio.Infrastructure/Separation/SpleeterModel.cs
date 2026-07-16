using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AudioClaudio.Infrastructure.Separation;

/// <summary>
/// Owns the 5 per-branch Spleeter ONNX <see cref="InferenceSession"/>s (one per stem: vocals,
/// piano, drums, bass, other), mirroring how <c>TranskunModel</c>/<c>TranskunTranscriber</c> load
/// multiple ONNX files from one directory. Each branch's raw softmax input logit is emitted
/// separately (no sigmoid, no softmax, no x magnitude baked into the graph — MODEL_CARD.md); the
/// cross-branch softmax and masking are lifted OUT of the graph into C# (<see cref="SpleeterMasking"/>),
/// exactly like the STFT and ratio-mask already are.
///
/// <para><b>Tensor layout (pinned by the export, MODEL_CARD.md §I/O contract):</b> the ONNX input
/// <c>x</c> is channel-first, shape <c>(2, num_splits, 512, 1024)</c> = (audio channel, time-split,
/// T, F) — NOT the NHWC layout <see cref="RunLogits"/> is given (<c>[num_splits, 512, 1024, 2]</c>).
/// <see cref="RunLogits"/> transposes on the way in (<c>x[ch, split, t, f] = magnitude[split, t, f,
/// ch]</c>) and back out (<c>logit[split, t, f, ch] = y[ch, split, t, f]</c>), so callers never see
/// the channel-first layout — it is purely an ONNX-graph implementation detail.</para>
/// </summary>
public sealed class SpleeterModel : IDisposable
{
    /// <summary>The 5 stems, in the fixed order the exported ONNX + MODEL_CARD.md declare them
    /// (graph-confirmed from the <c>stack</c>→<c>softmax</c> op feeding each branch's <c>BiasAdd</c>).
    /// <see cref="RunLogits"/> returns its 5 logits in this same order.</summary>
    public static readonly IReadOnlyList<string> StemNames = new[] { "vocals", "piano", "drums", "bass", "other" };

    /// <summary>Spleeter is stereo-only (its two audio channels), never a batch dimension here.</summary>
    private const int Channels = 2;

    private const string InputName = "x";
    private const string OutputName = "y";

    private readonly IReadOnlyList<InferenceSession> _sessions; // one per StemNames entry, same order

    /// <param name="modelDir">Directory containing <c>{vocals,piano,drums,bass,other}.onnx</c>
    /// (e.g. the path <c>SeparatorModelLocator.Resolve</c> returns).</param>
    public SpleeterModel(string modelDir)
    {
        ArgumentNullException.ThrowIfNull(modelDir);

        // Open one session per stem, disposing any already opened if a later stem's ONNX fails to load:
        // the constructor would otherwise throw mid-stream, leaving a partially-built object that never
        // reaches Dispose() and leaks the sessions for the earlier stems (a corrupt/incomplete model dir).
        var sessions = new List<InferenceSession>(StemNames.Count);
        try
        {
            foreach (string stem in StemNames)
            {
                sessions.Add(new InferenceSession(Path.Combine(modelDir, stem + ".onnx")));
            }
        }
        catch
        {
            foreach (InferenceSession session in sessions)
            {
                session.Dispose();
            }

            throw;
        }

        _sessions = sessions;
    }

    /// <summary>
    /// Runs all 5 branches over one magnitude tensor <c>[num_splits, 512, 1024, 2]</c> (NHWC: split,
    /// T, F, channel), returning each branch's raw pre-softmax logit in the same NHWC shape, in
    /// <see cref="StemNames"/> order. Pure aside from the ONNX call itself: no sigmoid, softmax, or
    /// magnitude multiply happens here (see <see cref="SpleeterMasking"/> for those).
    /// </summary>
    public IReadOnlyList<float[,,,]> RunLogits(float[,,,] magnitudeNhwc)
    {
        ArgumentNullException.ThrowIfNull(magnitudeNhwc);
        int numSplits = magnitudeNhwc.GetLength(0);
        int t = magnitudeNhwc.GetLength(1);
        int f = magnitudeNhwc.GetLength(2);
        int channels = magnitudeNhwc.GetLength(3);
        if (channels != Channels)
        {
            throw new ArgumentException(
                $"Expected {Channels} channels (Spleeter is stereo-only); got {channels}.", nameof(magnitudeNhwc));
        }

        // x[ch, split, t, f] = magnitude[split, t, f, ch] -- the channel-first transpose the
        // exported graph requires (class remarks).
        var input = new DenseTensor<float>(new[] { Channels, numSplits, t, f });
        Span<float> xSpan = input.Buffer.Span;
        for (int ch = 0; ch < Channels; ch++)
        {
            for (int s = 0; s < numSplits; s++)
            {
                for (int ti = 0; ti < t; ti++)
                {
                    int rowBase = ((ch * numSplits + s) * t + ti) * f;
                    for (int fi = 0; fi < f; fi++)
                    {
                        xSpan[rowBase + fi] = magnitudeNhwc[s, ti, fi, ch];
                    }
                }
            }
        }

        var logits = new List<float[,,,]>(_sessions.Count);
        foreach (InferenceSession session in _sessions)
        {
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
                session.Run(new[] { NamedOnnxValue.CreateFromTensor(InputName, input) });
            Tensor<float> y = results.First(r => r.Name == OutputName).AsTensor<float>();
            float[] flat = y.ToArray(); // row-major (ch, split, t, f), per the declared output shape

            // logit[split, t, f, ch] = y[ch, split, t, f] -- transpose back to the caller's NHWC layout.
            var logit = new float[numSplits, t, f, Channels];
            for (int ch = 0; ch < Channels; ch++)
            {
                for (int s = 0; s < numSplits; s++)
                {
                    for (int ti = 0; ti < t; ti++)
                    {
                        int rowBase = ((ch * numSplits + s) * t + ti) * f;
                        for (int fi = 0; fi < f; fi++)
                        {
                            logit[s, ti, fi, ch] = flat[rowBase + fi];
                        }
                    }
                }
            }

            logits.Add(logit);
        }

        return logits;
    }

    public void Dispose()
    {
        foreach (InferenceSession session in _sessions)
        {
            session.Dispose();
        }
    }
}
