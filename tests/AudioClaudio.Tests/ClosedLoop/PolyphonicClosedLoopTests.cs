using System;
using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Evaluation;
using AudioClaudio.Infrastructure.Transcription;
using Xunit;
using Xunit.Abstractions;

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>
/// The <b>polyphonic closed-loop gate</b> — the property that earns polyphony its release claim (v2
/// Stage 1). Generate a fixed-seed corpus of random chord scores, synthesize each with the MeltySynth
/// oracle, transcribe with Basic Pitch, and require the micro-averaged note-level F1 to clear a
/// <b>committed threshold</b>. Unlike the monophonic <see cref="ClosedLoop"/> this is <b>not</b>
/// exact recovery — a neural engine cannot return a score bit-for-bit, so the earned guarantee is
/// ordinal: a stated statistical F1 bar over a stated seed, at a stated onset tolerance (v2 workplan
/// Principle 1). Every real improvement <i>raises</i> the bar; a drop below it is a regression, and the
/// offending cases are quarantined (WAV + reference MIDI) for promotion to <c>fixtures/regressions/polyphonic/</c>
/// (replayed by <see cref="PolyphonicRegressionCorpusTests"/>).
///
/// Committed gate (Cornelius, 2026-07-10): <b>F1 ≥ 0.75 at ±50 ms</b> on the seed-4242 corpus. Baseline
/// measured well above it; see <c>docs/CORPUS.md</c> and <c>DECISIONS.md</c> "v2 Stage 1". Runs the ONNX
/// model, so it is Slow; it runs on every CI push (ci.yml has no filter), and is deterministic per build
/// (the only cross-run variance is the recorded ONNX SIMD drift across CPU architectures — the gate's
/// headroom absorbs it).
/// </summary>
public class PolyphonicClosedLoopTests
{
    /// <summary>The committed CI gate corpus: large enough for a credible, converged aggregate (~450
    /// notes — the @±50 ms F1 is stable to ±0.2 pt vs a 24-case draw), small enough to keep CI fast
    /// (~4–5 s; each case is one ONNX inference). A deep run overrides it via <c>POLY_CLOSED_LOOP_CASES</c>
    /// (the polyphonic analogue of <c>CLOSED_LOOP_CASES</c>).</summary>
    private const int GateCaseCount = 32;

    /// <summary>The committed F1 gate and its onset tolerance (Cornelius, 2026-07-10) — the single
    /// source is <see cref="PolyphonicClosedLoop"/>, shared with the regression replay.</summary>
    private const double GateThreshold = PolyphonicClosedLoop.GateThreshold;
    private const int GateToleranceMs = PolyphonicClosedLoop.GateToleranceMs;

    private readonly ITestOutputHelper _out;

    public PolyphonicClosedLoopTests(ITestOutputHelper output) => _out = output;

    [Fact]
    [Trait("Category", "Slow")]
    public void Polyphonic_closed_loop_meets_committed_F1_gate()
    {
        int count = int.TryParse(Environment.GetEnvironmentVariable("POLY_CLOSED_LOOP_CASES"), out int n) && n > 0
            ? n
            : GateCaseCount;

        var rate = new SampleRate(PolyphonicClosedLoopGen.SampleRateHz);
        using BasicPitchTranscriber transcriber = PolyphonicClosedLoop.CreateTranscriber();
        ISynthesizer synth = PolyphonicClosedLoop.CreateSynthesizer();

        int[] tolsMs = { 50, 100, 150 };

        // Micro-average: sum confusion counts across all cases, then derive P/R/F1 (stable at small N,
        // and note-weighted rather than case-weighted).
        var tp = tolsMs.ToDictionary(t => t, _ => 0);
        var fp = tolsMs.ToDictionary(t => t, _ => 0);
        var fn = tolsMs.ToDictionary(t => t, _ => 0);
        int totalRef = 0, totalCand = 0;

        // Per-case gate-tolerance confusion, kept so a failing corpus can name and quarantine its worst cases.
        var perCase = new List<(int Index, IReadOnlyList<NoteEvent> Score, float[] Pcm, double F1)>();

        int index = 0;
        foreach (IReadOnlyList<NoteEvent> score in PolyphonicClosedLoopGen.Cases(count))
        {
            // One (expensive) inference per case; keep the pcm so a regression can be quarantined.
            (float[] pcm, IReadOnlyList<NoteEvent> candidate) =
                PolyphonicClosedLoop.RenderAndTranscribe(score, synth, transcriber, rate);

            int caseTp = 0, caseFp = 0, caseFn = 0;
            foreach (int t in tolsMs)
            {
                NoteSetEvaluation e = TranscriptionEvaluator.Evaluate(candidate, score, new NoteMatchOptions(t / 1000.0));
                tp[t] += e.TruePositives;
                fp[t] += e.FalsePositives;
                fn[t] += e.FalseNegatives;
                if (t == GateToleranceMs)
                {
                    caseTp = e.TruePositives;
                    caseFp = e.FalsePositives;
                    caseFn = e.FalseNegatives;
                }

                if (t == tolsMs[^1])
                {
                    totalRef += e.ReferenceCount;
                    totalCand += e.CandidateCount;
                }
            }

            perCase.Add((index, score, pcm, F1(caseTp, caseFp, caseFn)));
            index++;
        }

        _out.WriteLine($"Polyphonic closed-loop gate — {count} synthetic cases, {totalRef} reference notes, {totalCand} transcribed notes (seed {PolyphonicClosedLoopGen.DefaultSeed})");
        foreach (int t in tolsMs)
        {
            _out.WriteLine($"  ±{t,3} ms:  {Line(tp[t], fp[t], fn[t])}");
        }

        double gateF1 = F1(tp[GateToleranceMs], fp[GateToleranceMs], fn[GateToleranceMs]);

        // Persist the worst offenders BEFORE asserting, so a red gate leaves reproducible artifacts.
        // Only the worst few (not the whole corpus) — those are the diagnostic signal; a micro-average
        // below the gate implies at least one case is individually below it.
        if (gateF1 < GateThreshold)
        {
            const int MaxQuarantined = 8;
            var worst = perCase.OrderBy(c => c.F1).Take(MaxQuarantined).ToList();
            _out.WriteLine($"  GATE FAILED — worst {worst.Count} case(s) by F1 @±{GateToleranceMs} ms:");
            string? dir = null;
            foreach (var c in worst)
            {
                _out.WriteLine($"    case {c.Index}: F1 {c.F1,6:P1}");
                dir = PolyphonicQuarantine.Persist($"seed{PolyphonicClosedLoopGen.DefaultSeed}-case{c.Index}", c.Score, c.Pcm, rate);
            }

            _out.WriteLine($"  Quarantined {worst.Count} case(s) to {dir}");
        }

        Assert.True(
            gateF1 >= GateThreshold,
            $"polyphonic closed-loop F1 @±{GateToleranceMs} ms = {gateF1:P1} < committed gate {GateThreshold:P0} " +
            $"(seed {PolyphonicClosedLoopGen.DefaultSeed}, {count} cases, {totalRef} reference notes) — a regression, or a lowered bar. See the quarantine dir.");
    }

    private static double F1(int tp, int fp, int fn)
    {
        double p = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
        double r = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
        return p + r == 0 ? 0 : 2 * p * r / (p + r);
    }

    private static string Line(int tp, int fp, int fn)
    {
        double p = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
        double r = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
        return $"F1 {F1(tp, fp, fn),6:P1}   P {p,6:P1}   R {r,6:P1}   (TP {tp}, FP {fp}, FN {fn})";
    }
}
