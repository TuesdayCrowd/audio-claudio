using System.Collections.Generic;
using AudioClaudio.Application.Ports; // ISynthesizer
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Synthesis; // MeltySynthSynthesizer
using AudioClaudio.Infrastructure.Transcription; // BasicPitchTranscriber
using AudioClaudio.Tests.TestSupport; // InMemoryAudioSource, RepoPaths

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>
/// Shared oracle + engine for the <b>polyphonic closed-loop gate</b> (<see cref="PolyphonicClosedLoopTests"/>):
/// the MeltySynth synthesizer that renders a generated chord score to audio, and the Basic Pitch transcriber
/// that reads it back. The gate scores the transcription's honest polyphonic output
/// (<see cref="TranscriptionResult.RawEvents"/>) against the score with the note-level F1 harness. Candidate
/// (22.05 kHz) and reference (44.1 kHz) are compared in seconds by the evaluator, so the rate difference is fine.
/// </summary>
public static class PolyphonicClosedLoop
{
    /// <summary>The committed polyphonic gate (Cornelius, 2026-07-10): note-level F1 ≥ 0.75 at ±50 ms.
    /// The single source of the threshold + tolerance, shared by the gate
    /// (<see cref="PolyphonicClosedLoopTests"/>) and the regression replay
    /// (<see cref="PolyphonicRegressionCorpusTests"/>).</summary>
    public const double GateThreshold = 0.75;
    public const int GateToleranceMs = 50;

    public static ISynthesizer CreateSynthesizer() => new MeltySynthSynthesizer(Fixtures.SoundFontPath);

    public static BasicPitchTranscriber CreateTranscriber() =>
        new(RepoPaths.Fixture("models", "basic-pitch-nmp.onnx"));

    /// <summary>Render the score with the oracle and transcribe it once, returning BOTH the rendered audio
    /// (kept so a failing case can be quarantined) and the engine's honest polyphonic output (so the caller
    /// can score the same transcription at several tolerances without re-running the expensive ONNX inference).</summary>
    public static (float[] Pcm, IReadOnlyList<NoteEvent> Candidate) RenderAndTranscribe(
        IReadOnlyList<NoteEvent> score, ISynthesizer synth, BasicPitchTranscriber transcriber, SampleRate rate)
    {
        float[] pcm = synth.Render(score, rate);
        var source = new InMemoryAudioSource(pcm, rate, new FrameParameters(1024, 256));
        return (pcm, transcriber.Transcribe(source).RawEvents);
    }
}
