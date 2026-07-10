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
/// The polyphonic closed-loop <b>fidelity diagnostic</b> (the verdict on "how good is the engine,
/// really?"). It measures the Basic Pitch engine's note-level F1 on clean, synthesized chord audio —
/// where there is no OMR-reference error and no rubato — so the number reflects the engine itself, not
/// the confounds that pull the real-world "Death" figure down to ~15–22%. If synthetic F1 is much
/// higher than that, the real-world gap is reference-error + rubato + post-processing, and the effort
/// belongs in timing/precision, not in swapping the model. Runs the ONNX model, so it is Slow.
/// </summary>
public class PolyphonicClosedLoopTests
{
    private readonly ITestOutputHelper _out;

    public PolyphonicClosedLoopTests(ITestOutputHelper output) => _out = output;

    [Fact]
    [Trait("Category", "Slow")]
    public void Engine_intrinsic_fidelity_on_clean_synthetic_polyphony()
    {
        var rate = new SampleRate(PolyphonicClosedLoopGen.SampleRateHz);
        using BasicPitchTranscriber transcriber = PolyphonicClosedLoop.CreateTranscriber();
        ISynthesizer synth = PolyphonicClosedLoop.CreateSynthesizer();

        int[] tolsMs = { 50, 100, 150 };
        var cases = PolyphonicClosedLoopGen.Cases(count: 8).ToList();

        // Micro-average: sum confusion counts across all cases, then derive P/R/F1 (stable at small N).
        var rawTp = new Dictionary<int, int>();
        var rawFp = new Dictionary<int, int>();
        var rawFn = new Dictionary<int, int>();
        foreach (int t in tolsMs)
        {
            rawTp[t] = rawFp[t] = rawFn[t] = 0;
        }

        int alignTp = 0, alignFp = 0, alignFn = 0; // aligned @150 ms
        int totalRef = 0, totalCand = 0;

        foreach (IReadOnlyList<NoteEvent> score in cases)
        {
            // One (expensive) inference per case; score the same candidate every way.
            IReadOnlyList<NoteEvent> candidate = PolyphonicClosedLoop.Transcribe(score, synth, transcriber, rate);
            foreach (int t in tolsMs)
            {
                NoteSetEvaluation e = TranscriptionEvaluator.Evaluate(candidate, score, new NoteMatchOptions(t / 1000.0));
                rawTp[t] += e.TruePositives;
                rawFp[t] += e.FalsePositives;
                rawFn[t] += e.FalseNegatives;
                if (t == tolsMs[^1])
                {
                    totalRef += e.ReferenceCount;
                    totalCand += e.CandidateCount;
                }
            }

            NoteSetEvaluation a = TranscriptionEvaluator.Evaluate(
                OnsetAlignment.GlobalScale(candidate, score), score, new NoteMatchOptions(0.150));
            alignTp += a.TruePositives;
            alignFp += a.FalsePositives;
            alignFn += a.FalseNegatives;
        }

        _out.WriteLine($"Polyphonic closed loop — {cases.Count} synthetic cases, {totalRef} reference notes, {totalCand} transcribed notes");
        foreach (int t in tolsMs)
        {
            _out.WriteLine($"  raw   ±{t,3} ms:  {Line(rawTp[t], rawFp[t], rawFn[t])}");
        }

        _out.WriteLine($"  align ±150 ms:  {Line(alignTp, alignFp, alignFn)}");
        _out.WriteLine("  (align is EXPECTED to be far lower here: GlobalScale only helps when there is real");
        _out.WriteLine("   tempo drift to remove — clean synthetic audio has none, and rescaling a near-perfect");
        _out.WriteLine("   candidate onto the reference span is fragile to outlier false positives.)");

        // Regression floor calibrated from the observed verdict: clean-synthetic fidelity is far above the
        // ~15–22% real-world figure. (Guards against an engine/decoder regression, not a spec of the ceiling.)
        double f1At150 = F1(rawTp[150], rawFp[150], rawFn[150]);
        Assert.True(f1At150 >= 0.55, $"synthetic note-level F1 @150 ms regressed: {f1At150:P1} (< 55%)");
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
