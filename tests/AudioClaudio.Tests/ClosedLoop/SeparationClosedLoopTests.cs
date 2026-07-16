using System;
using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Separation;
using AudioClaudio.Infrastructure.Synthesis;
using Xunit;
using Xunit.Abstractions;

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>
/// The <b>source-separation closed-loop gate</b> (Stage 1.4) -- the synthesize -&gt; separate -&gt;
/// score analogue of <see cref="PolyphonicClosedLoopTests"/>. Generate a fixed-seed corpus of
/// three-instrument mixes (bass / piano / tenor sax, each in its own disjoint pitch band), synthesize
/// each instrument on its own MeltySynth program, sum into a mix, run the committed Spleeter ONNX
/// separator once per case, and score every recovered stem against its own ground-truth render with
/// SI-SDR.
///
/// This is deliberately <b>not</b> framed as a release-quality claim: Spleeter is trained on real
/// commercial recordings (Deezer's "Bean" catalog), not synthesized GM renders, so its absolute SI-SDR
/// on this corpus is expected to be modest. The gate exists purely as a <b>regression guard</b> --
/// catching a break in the STFT/masking/reconstruction pipeline or the committed ONNX weights, not
/// asserting separation quality is "good." Per the project's guarantee hierarchy (docs/CORPUS.md), this
/// sits strictly <i>below</i> the monophonic bit-exact loop, the polyphonic F1 gate, and the Transkun
/// parity gate -- a statistical regression floor, never flattened into "proven."
/// </summary>
public class SeparationClosedLoopTests
{
    /// <summary>The committed CI gate corpus: three instruments per case, each a handful of notes
    /// (~5-8 s of audio) -- small enough to keep the (already Slow) ONNX separation pass fast. A deep
    /// run overrides it via <c>SEPARATION_CLOSED_LOOP_CASES</c> (mirrors <c>POLY_CLOSED_LOOP_CASES</c>).</summary>
    private const int GateCaseCount = 6;

    private readonly ITestOutputHelper _out;

    public SeparationClosedLoopTests(ITestOutputHelper output) => _out = output;

    [Fact]
    [Trait("Category", "Slow")]
    public void Separation_closed_loop_meets_committed_SI_SDR_gate()
    {
        int count = int.TryParse(Environment.GetEnvironmentVariable("SEPARATION_CLOSED_LOOP_CASES"), out int n) && n > 0
            ? n
            : GateCaseCount;

        var rate = new SampleRate(SeparationClosedLoopGen.SampleRateHz);
        using SpleeterSourceSeparator separator = SeparationClosedLoop.CreateSeparator();
        IReadOnlyDictionary<string, MeltySynthSynthesizer> synths =
            SeparationClosedLoop.CreateSynthesizers(Fixtures.SoundFontPath);

        var allScores = new List<double>();
        var perStemScores = new Dictionary<string, List<double>>();
        var perCase = new List<(int Index, SeparationClosedLoop.CaseResult Result, double MedianDb)>();

        int index = 0;
        foreach (SeparationClosedLoopGen.SeparationCase testCase in SeparationClosedLoopGen.Cases(count))
        {
            SeparationClosedLoop.CaseResult result =
                SeparationClosedLoop.RenderAndSeparate(testCase, synths, separator, rate);

            foreach (SeparationClosedLoop.InstrumentScore score in result.Scores)
            {
                allScores.Add(score.SiSdrDb);
                if (!perStemScores.TryGetValue(score.TargetStem, out List<double>? list))
                {
                    perStemScores[score.TargetStem] = list = new List<double>();
                }

                list.Add(score.SiSdrDb);
            }

            double caseMedian = Median(result.Scores.Select(s => s.SiSdrDb).ToList());
            perCase.Add((index, result, caseMedian));
            index++;
        }

        double overallMedian = Median(allScores);

        _out.WriteLine(
            $"Separation closed-loop gate -- {count} synthetic 3-instrument mixes (seed {SeparationClosedLoopGen.DefaultSeed})");
        foreach (KeyValuePair<string, List<double>> kv in perStemScores.OrderBy(kv => kv.Key))
        {
            _out.WriteLine($"  {kv.Key,-8}: median SI-SDR {Median(kv.Value),7:F2} dB  (n={kv.Value.Count})");
        }

        _out.WriteLine($"  overall median SI-SDR: {overallMedian,7:F2} dB   (gate {SeparationClosedLoop.GateThresholdDb:F2} dB)");

        // Persist the worst offenders BEFORE asserting, so a red gate leaves reproducible artifacts.
        if (overallMedian < SeparationClosedLoop.GateThresholdDb)
        {
            const int MaxQuarantined = 4;
            var worst = perCase.OrderBy(c => c.MedianDb).Take(MaxQuarantined).ToList();
            _out.WriteLine($"  GATE FAILED -- worst {worst.Count} case(s) by median SI-SDR:");
            string? dir = null;
            foreach (var c in worst)
            {
                _out.WriteLine($"    case {c.Index}: median {c.MedianDb:F2} dB");
                dir = SeparationQuarantine.Persist(
                    $"seed{SeparationClosedLoopGen.DefaultSeed}-case{c.Index}", c.Result, rate);
            }

            _out.WriteLine($"  Quarantined {worst.Count} case(s) to {dir}");
        }

        Assert.True(
            overallMedian >= SeparationClosedLoop.GateThresholdDb,
            $"separation closed-loop median SI-SDR = {overallMedian:F2} dB < committed gate " +
            $"{SeparationClosedLoop.GateThresholdDb:F2} dB (seed {SeparationClosedLoopGen.DefaultSeed}, {count} cases) " +
            "-- a regression, or a lowered bar. See the quarantine dir.");
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }
}
