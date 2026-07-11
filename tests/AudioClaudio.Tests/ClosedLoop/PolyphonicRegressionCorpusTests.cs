using System.Collections.Generic;
using System.IO;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Evaluation;
using AudioClaudio.Infrastructure.Midi;
using AudioClaudio.Infrastructure.Transcription;
using Xunit;

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>
/// The polyphonic regression ratchet: every <c>*.mid</c> promoted into
/// <c>fixtures/regressions/polyphonic/</c> (a chord case that once dragged the gate below threshold, now
/// fixed) is re-synthesized and re-transcribed, and must clear the committed gate's per-case F1 bar. Kept
/// in a <b>subdirectory</b> so the monophonic <see cref="RegressionCorpusTests"/> — which scans only the
/// top level of <c>fixtures/regressions/</c> and runs each <c>.mid</c> through the one-note-at-a-time YIN
/// pipeline — never picks up a chord fixture (which cannot survive an exact monophonic comparator).
/// Mirrors the mono corpus: only the <c>.mid</c> is committed; the audio is regenerated deterministically
/// by the synth. The corpus only ever grows, so the suite only ever gets harder.
/// </summary>
public sealed class PolyphonicRegressionCorpusTests
{
    [Trait("Category", "Slow")] // re-runs the ONNX engine
    [Fact]
    public void All_polyphonic_regression_fixtures_meet_the_gate()
    {
        string dir = Path.Combine(Fixtures.RegressionsDir, "polyphonic");
        if (!Directory.Exists(dir))
        {
            return;
        }

        var rate = new SampleRate(PolyphonicClosedLoopGen.SampleRateHz);
        using BasicPitchTranscriber transcriber = PolyphonicClosedLoop.CreateTranscriber();
        ISynthesizer synth = PolyphonicClosedLoop.CreateSynthesizer();
        var options = new NoteMatchOptions(PolyphonicClosedLoop.GateToleranceMs / 1000.0);

        foreach (string mid in Directory.GetFiles(dir, "*.mid"))
        {
            IReadOnlyList<NoteEvent> reference = MidiFileReader.ReadFile(mid, rate).Events;
            (_, IReadOnlyList<NoteEvent> candidate) =
                PolyphonicClosedLoop.RenderAndTranscribe(reference, synth, transcriber, rate);
            NoteSetEvaluation e = TranscriptionEvaluator.Evaluate(candidate, reference, options);
            Assert.True(
                e.F1 >= PolyphonicClosedLoop.GateThreshold,
                $"polyphonic regression fixture {Path.GetFileName(mid)} F1 @±{PolyphonicClosedLoop.GateToleranceMs} ms = {e.F1:P1} " +
                $"< gate {PolyphonicClosedLoop.GateThreshold:P0} — it regressed.");
        }
    }
}
