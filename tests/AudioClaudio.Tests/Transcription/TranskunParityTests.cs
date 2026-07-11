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

        NoteSetEvaluation eval = TranscriptionEvaluator.Evaluate(csNotes, nativeNotes, new NoteMatchOptions(0.050));
        _out.WriteLine(
            $"{System.IO.Path.GetFileName(wavPath)}: C# {csNotes.Count} vs native {nativeNotes.Count} notes -> " +
            $"P={eval.Precision:P1} R={eval.Recall:P1} F1={eval.F1:P1}");

        Assert.True(eval.F1 >= 0.99, $"parity F1 {eval.F1:P1} below the 99% gate on {nativeMidi}");
    }
}
