using System.Collections.Generic;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Evaluation;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Infrastructure.Midi;
using AudioClaudio.Infrastructure.Transcription;
using AudioClaudio.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace AudioClaudio.Tests.Transcription;

/// <summary>
/// v2 Stage 4d — the PyTorch≡ONNX parity gate. The C# Transkun engine must agree with the native
/// <c>transkun</c> CLI (PyTorch) at ≥ 99 % note-level F1. The native transcriptions are committed as
/// reference MIDIs (run once in the transkun venv; there is no venv in CI), so this gate runs in CI by
/// comparing the C# output to that frozen PyTorch reference. Onset tolerance is 50 ms — comfortably above
/// the ≤ 11.6 ms sub-frame refinement the core-first engine deliberately omits (Stage 4e), so a real port
/// bug (≥ 1 frame ≈ 23 ms, or a pitch/count error) shows up while the deferred refinement does not.
/// </summary>
public class TranskunParityTests
{
    private readonly ITestOutputHelper _out;

    public TranskunParityTests(ITestOutputHelper output) => _out = output;

    private static string ModelDir => RepoPaths.Fixture("models", "transkun");
    private static readonly SampleRate Rate = new(44100);

    public static IEnumerable<object[]> Clips => new[]
    {
        new object[] { RepoPaths.Fixture("golden", "two-bar.wav"), "two-bar.native.mid" },      // 3 segments, monophonic scale
        new object[] { RepoPaths.Fixture("models", "transkun", "parity", "two-bar-x4.wav"), "two-bar-x4.native.mid" }, // 21.8 s, cross-boundary stitching
    };

    [Theory]
    [Trait("Category", "Slow")] // runs the 53 MB ONNX over every 16 s segment
    [MemberData(nameof(Clips))]
    public void MatchesNativeTranskun_AtParity(string wavPath, string nativeMidi)
    {
        using var source = WavAudioSource.FromFile(wavPath, new FrameParameters(1024, 256));
        using var engine = new TranskunTranscriber(ModelDir, new Radix2Fft());
        (IReadOnlyList<NoteEvent> csNotes, _) = engine.TranscribeDetailed(source);

        IReadOnlyList<NoteEvent> nativeNotes =
            MidiFileReader.ReadFile(RepoPaths.Fixture("models", "transkun", "parity", nativeMidi), Rate, flattenPedal: false).Events;

        // Stage 4e added sub-frame refinement, so C# onsets now match native to ~1 ms — tolerance 25 ms.
        NoteSetEvaluation eval = TranscriptionEvaluator.Evaluate(csNotes, nativeNotes, new NoteMatchOptions(0.025));

        // Velocity agreement: match each native note to the same-pitch C# note nearest in onset, and compare
        // velocities (both are the argmax of the same head, so exact modulo rare ctx-drift argmax flips).
        double velSum = 0.0;
        int matched = 0, velExact = 0;
        foreach (NoteEvent nat in nativeNotes)
        {
            NoteEvent? best = null;
            double bestDelta = 0.026;
            foreach (NoteEvent cs in csNotes)
            {
                if (cs.Pitch.MidiNumber != nat.Pitch.MidiNumber)
                {
                    continue;
                }

                double delta = System.Math.Abs((cs.Onset.Samples - nat.Onset.Samples) / 44100.0);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = cs;
                }
            }

            if (best is { } m)
            {
                matched++;
                int d = System.Math.Abs(m.Velocity - nat.Velocity);
                velSum += d;
                if (d == 0)
                {
                    velExact++;
                }
            }
        }

        double meanVelDelta = matched == 0 ? 0.0 : velSum / matched;
        _out.WriteLine(
            $"{System.IO.Path.GetFileName(wavPath)}: C# {csNotes.Count} vs native {nativeNotes.Count} notes -> " +
            $"P={eval.Precision:P1} R={eval.Recall:P1} F1={eval.F1:P1}; velocity exact {velExact}/{matched} (mean |Δ|={meanVelDelta:F2})");

        Assert.True(eval.F1 >= 0.99, $"parity F1 {eval.F1:P1} below the 99% gate on {nativeMidi}");
        Assert.True(meanVelDelta <= 2.0, $"mean velocity delta {meanVelDelta:F2} exceeds 2 on {nativeMidi}");
    }
}
