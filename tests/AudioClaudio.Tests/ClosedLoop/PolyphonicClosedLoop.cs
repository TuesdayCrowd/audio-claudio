using System.Collections.Generic;
using AudioClaudio.Application.Ports; // ISynthesizer
using AudioClaudio.Domain;
using AudioClaudio.Domain.Evaluation;
using AudioClaudio.Infrastructure.Synthesis; // MeltySynthSynthesizer
using AudioClaudio.Infrastructure.Transcription; // BasicPitchTranscriber
using AudioClaudio.Tests.TestSupport; // InMemoryAudioSource, RepoPaths

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>
/// The <b>polyphonic</b> closed loop: synthesize a generated chord score with the MeltySynth oracle,
/// transcribe the audio through the Basic Pitch engine, and score the transcription's honest polyphonic
/// output (<see cref="TranscriptionResult.RawEvents"/>) against the score with the note-level F1 harness.
///
/// Unlike the monophonic <see cref="ClosedLoop"/> this is a <b>diagnostic, not a pass/fail property</b>:
/// the neural engine does not recover a score exactly, and the point is to measure how much it recovers
/// on CLEAN audio — no OMR-reference error, no rubato — isolating the engine's intrinsic fidelity from
/// the confounds that depress the real-world number. The candidate (22.05 kHz) and reference (44.1 kHz)
/// are compared in seconds by <see cref="TranscriptionEvaluator"/>, so the rate difference is fine.
/// </summary>
public static class PolyphonicClosedLoop
{
    public static ISynthesizer CreateSynthesizer() => new MeltySynthSynthesizer(Fixtures.SoundFontPath);

    public static BasicPitchTranscriber CreateTranscriber() =>
        new(RepoPaths.Fixture("models", "basic-pitch-nmp.onnx"));

    /// <summary>Synthesize the score and transcribe it once, returning the engine's honest polyphonic
    /// output — so a caller can score the same transcription at several tolerances without re-running
    /// the (expensive) ONNX inference.</summary>
    public static IReadOnlyList<NoteEvent> Transcribe(
        IReadOnlyList<NoteEvent> score, ISynthesizer synth, BasicPitchTranscriber transcriber, SampleRate rate)
    {
        float[] pcm = synth.Render(score, rate);
        var source = new InMemoryAudioSource(pcm, rate, new FrameParameters(1024, 256));
        return transcriber.Transcribe(source).RawEvents;
    }
}
