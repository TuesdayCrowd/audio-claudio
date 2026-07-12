using System;
using System.Collections.Generic;
using System.Diagnostics;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Polyphony;
using AudioClaudio.Infrastructure.Transcription;
using AudioClaudio.Tests.Signals;
using AudioClaudio.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace AudioClaudio.Tests.Spike;

/// <summary>
/// THROWAWAY feasibility spike — measurement only, NOT production code, NOT committed. Answers one
/// question raised by a design review: can a streaming Basic Pitch loop (re-run inference + decode
/// every ~1.64 s hop, decoding against an ever-growing accumulated posteriorgram) keep up with real
/// time on this machine (Apple M3 Max)?
///
/// Three measurements:
///   M1 — one window's ONNX inference cost (<see cref="BasicPitchModel.Run"/>) vs the ~1.64 s hop
///        budget.
///   M2 — <see cref="BasicPitchNoteDecoder.Decode"/> cost against an accumulated grid that grows with
///        session length (30 s / 1 / 2 / 5 / 10 min) — the review's O(T²) suspect is the
///        <c>MelodiaTrick</c> full-grid ArgMax sweep (see <c>BasicPitchNoteDecoder.MelodiaSweep</c>).
///   M3 — the review's proposed fix: decode only a trailing ~4 s window of the grid regardless of
///        total session length; is decode cost then flat?
///
/// Tagged <c>Category=Spike</c> so it is excluded from the normal/Fast suite — wall-clock timing is
/// inherently non-deterministic and must never gate CI. Run explicitly:
///   dotnet test --filter "Category=Spike" -l "console;verbosity=detailed"
///
/// Realism caveat: M2/M3's grids are a single real ~2 s window's note/onset posteriorgrams (from a
/// synthetic 4-note piano-frequency chord run through the real ONNX model) TILED to the target
/// length, not a real multi-minute performance. This keeps the energy distribution model-realistic
/// (as opposed to random noise) while being honest that it is periodic, not naturalistic — see the
/// class-level remarks in the report this spike produced.
/// </summary>
public class BasicPitchStreamingSpike
{
    private readonly ITestOutputHelper _output;

    public BasicPitchStreamingSpike(ITestOutputHelper output) => _output = output;

    // The review's real-time budget: a streaming loop re-infers/re-decodes every hop.
    private const double HopBudgetMs = 1640.0;

    // The review's proposed bound for M3: decode only the last ~4 s of accumulated context.
    private const int TrailingSeconds = 4;
    private const int FramesPerSecond = 86; // per the task's own math (172 frames / ~2 s window)

    private static readonly (string Label, int Seconds)[] SessionLengths =
    {
        ("30s", 30),
        ("1min", 60),
        ("2min", 120),
        ("5min", 300),
        ("10min", 600),
    };

    // One real window's note/onset posteriorgrams from the actual ONNX model, computed once and
    // shared by M2/M3 (a fresh ONNX InferenceSession load + run per test would be wasteful and add
    // noise unrelated to the question being measured).
    private static readonly Lazy<(float[,] Frames, float[,] Onsets)> SeedGrids = new(() =>
    {
        using var model = new BasicPitchModel(RepoPaths.Fixture("models", "basic-pitch-nmp.onnx"));
        float[] window = SyntheticPolyphonicWindow();
        BasicPitchWindowOutput output = model.Run(window);
        return (output.NoteFrames, output.Onsets);
    });

    // ----------------------------------------------------------------------------------------
    // M1 — window inference latency
    // ----------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Spike")]
    public void M1_window_inference_latency_vs_hop_budget()
    {
        using var model = new BasicPitchModel(RepoPaths.Fixture("models", "basic-pitch-nmp.onnx"));
        float[] window = SyntheticPolyphonicWindow();

        // Warm-up: the first ONNX call pays graph init/JIT cost; discard it.
        model.Run(window);

        const int runs = 20;
        var timesMs = new List<double>(runs);
        for (int i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            model.Run(window);
            sw.Stop();
            timesMs.Add(sw.Elapsed.TotalMilliseconds);
        }

        timesMs.Sort();
        double min = timesMs[0];
        double max = timesMs[^1];
        double median = timesMs[runs / 2];

        _output.WriteLine($"M1: {runs} runs of BasicPitchModel.Run over one {BasicPitchModel.WindowSamples}-sample window (ms):");
        _output.WriteLine($"    min={min:F2}  median={median:F2}  max={max:F2}");
        _output.WriteLine($"    hop budget = {HopBudgetMs:F0} ms; median is " +
                           $"{(median < HopBudgetMs ? $"UNDER budget by {HopBudgetMs / median:F1}x" : "OVER budget")}.");
    }

    // ----------------------------------------------------------------------------------------
    // M2 — decode cost vs accumulated grid length (unbounded, the O(T^2) suspect)
    // ----------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Spike")]
    public void M2_unbounded_decode_cost_vs_accumulated_length()
    {
        (float[,] frames, float[,] onsets) = SeedGrids.Value;

        // JIT warm-up at a small size, discarded, before the measured sizes.
        float[,] warmFrames = Tile(frames, FramesPerSecond);
        float[,] warmOnsets = Tile(onsets, FramesPerSecond);
        BasicPitchNoteDecoder.Decode(warmFrames, warmOnsets, NoteDecoderOptions.Default);

        _output.WriteLine("M2: unbounded BasicPitchNoteDecoder.Decode cost vs accumulated grid length " +
                           $"(MelodiaTrick=true, hop budget = {HopBudgetMs:F0} ms):");

        foreach ((string label, int seconds) in SessionLengths)
        {
            int nFrames = seconds * FramesPerSecond;
            float[,] tiledFrames = Tile(frames, nFrames);
            float[,] tiledOnsets = Tile(onsets, nFrames);

            var sw = Stopwatch.StartNew();
            IReadOnlyList<BasicPitchNote> notes =
                BasicPitchNoteDecoder.Decode(tiledFrames, tiledOnsets, NoteDecoderOptions.Default);
            sw.Stop();

            double ms = sw.Elapsed.TotalMilliseconds;
            _output.WriteLine($"    {label,-6} nFrames={nFrames,6}  decode={ms,10:F1} ms  notes={notes.Count,5}  " +
                               $"{(ms < HopBudgetMs ? "under" : "OVER")} hop budget");
        }
    }

    // ----------------------------------------------------------------------------------------
    // M2b — adversarial: force EVERY note through MelodiaTrick (onsets suppressed entirely).
    // M2's tiled real onset grid re-fires a clean onset at every tile boundary, so most notes are
    // actually claimed by the FAST onset-walk loop, not by MelodiaTrick's O(T)-per-sweep ArgMax scan
    // — M2 alone may understate the review's specific O(T^2) worry. This variant zeroes the onset
    // grid and disables InferOnsets so the decoder has no onset signal at all: every recoverable note
    // must come from the MelodiaSweep while(true) loop, which is the actual O(T^2) suspect.
    // ----------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Spike")]
    public void M2b_adversarial_melodia_trick_only_decode_cost_vs_accumulated_length()
    {
        (float[,] frames, float[,] onsets) = SeedGrids.Value;
        var melodiaOnly = new NoteDecoderOptions(InferOnsets: false, MelodiaTrick: true);

        int warmFrames0 = FramesPerSecond;
        BasicPitchNoteDecoder.Decode(
            Tile(frames, warmFrames0), new float[warmFrames0, onsets.GetLength(1)], melodiaOnly);

        _output.WriteLine("M2b: ADVERSARIAL decode cost — onsets suppressed, every note forced through " +
                           $"MelodiaTrick (hop budget = {HopBudgetMs:F0} ms):");

        foreach ((string label, int seconds) in SessionLengths)
        {
            int nFrames = seconds * FramesPerSecond;
            float[,] tiledFrames = Tile(frames, nFrames);
            var zeroOnsets = new float[nFrames, onsets.GetLength(1)]; // no onset signal whatsoever

            var sw = Stopwatch.StartNew();
            IReadOnlyList<BasicPitchNote> notes =
                BasicPitchNoteDecoder.Decode(tiledFrames, zeroOnsets, melodiaOnly);
            sw.Stop();

            double ms = sw.Elapsed.TotalMilliseconds;
            _output.WriteLine($"    {label,-6} nFrames={nFrames,6}  decode={ms,10:F1} ms  notes={notes.Count,5}  " +
                               $"{(ms < HopBudgetMs ? "under" : "OVER")} hop budget");
        }
    }

    // ----------------------------------------------------------------------------------------
    // M3 — bounded (trailing-window) decode cost vs accumulated grid length
    // ----------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Spike")]
    public void M3_bounded_trailing_decode_cost_vs_accumulated_length()
    {
        (float[,] frames, float[,] onsets) = SeedGrids.Value;
        int trailingFrames = TrailingSeconds * FramesPerSecond;

        // JIT warm-up.
        BasicPitchNoteDecoder.Decode(
            Tile(frames, trailingFrames), Tile(onsets, trailingFrames), NoteDecoderOptions.Default);

        _output.WriteLine($"M3: bounded decode (trailing {trailingFrames} frames = {TrailingSeconds}s) vs " +
                           $"accumulated grid length (hop budget = {HopBudgetMs:F0} ms):");

        foreach ((string label, int seconds) in SessionLengths)
        {
            int nFrames = seconds * FramesPerSecond;
            float[,] tiledFrames = Tile(frames, nFrames);
            float[,] tiledOnsets = Tile(onsets, nFrames);

            float[,] trailingFramesGrid = TrailingSlice(tiledFrames, trailingFrames);
            float[,] trailingOnsetsGrid = TrailingSlice(tiledOnsets, trailingFrames);

            var sw = Stopwatch.StartNew();
            IReadOnlyList<BasicPitchNote> notes =
                BasicPitchNoteDecoder.Decode(trailingFramesGrid, trailingOnsetsGrid, NoteDecoderOptions.Default);
            sw.Stop();

            double ms = sw.Elapsed.TotalMilliseconds;
            _output.WriteLine($"    {label,-6} totalFrames={nFrames,6}  decodedFrames={trailingFramesGrid.GetLength(0),4}  " +
                               $"decode={ms,8:F2} ms  notes={notes.Count,4}  " +
                               $"{(ms < HopBudgetMs ? "under" : "OVER")} hop budget");
        }
    }

    // ----------------------------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------------------------

    // A synthetic polyphonic window: a sum of four piano-frequency partials (a C-major triad plus
    // octave: C4, E4, G4, C5), one full BasicPitchModel.WindowSamples window at the model's own
    // 22 050 Hz rate. Built from the existing deterministic SignalGenerator (R2.3) rather than a new
    // generator, per the spike's own scoping.
    private static float[] SyntheticPolyphonicWindow()
    {
        var rate = new SampleRate(BasicPitchModel.SampleRateHz);
        int n = BasicPitchModel.WindowSamples;
        double[] pianoFrequenciesHz = { 261.63, 329.63, 392.00, 523.25 }; // C4, E4, G4, C5

        var mix = new float[n];
        foreach (double freq in pianoFrequenciesHz)
        {
            float[] partial = SignalGenerator.Sine(freq, n, rate, amplitude: 0.2);
            for (int i = 0; i < n; i++)
            {
                mix[i] += partial[i];
            }
        }

        return mix;
    }

    // Repeats `source`'s rows to fill a [targetFrames, cols] grid (truncating the final repeat).
    private static float[,] Tile(float[,] source, int targetFrames)
    {
        int srcFrames = source.GetLength(0);
        int cols = source.GetLength(1);
        var result = new float[targetFrames, cols];
        for (int t = 0; t < targetFrames; t++)
        {
            int srcT = t % srcFrames;
            for (int c = 0; c < cols; c++)
            {
                result[t, c] = source[srcT, c];
            }
        }

        return result;
    }

    // The last `windowFrames` rows of `source` (or all of it, if shorter).
    private static float[,] TrailingSlice(float[,] source, int windowFrames)
    {
        int totalFrames = source.GetLength(0);
        int cols = source.GetLength(1);
        int start = Math.Max(0, totalFrames - windowFrames);
        int len = totalFrames - start;
        var result = new float[len, cols];
        for (int t = 0; t < len; t++)
        {
            for (int c = 0; c < cols; c++)
            {
                result[t, c] = source[start + t, c];
            }
        }

        return result;
    }
}
