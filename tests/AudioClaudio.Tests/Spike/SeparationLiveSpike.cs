using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AudioClaudio.Application.Ports; // SeparatedStem
using AudioClaudio.Application.UseCases; // StemRoute, StemTranscription, MultiStemTranscriber
using AudioClaudio.Cli.Composition; // SeparatorModelLocator, MultiStemRouting
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral; // Radix2Fft
using AudioClaudio.Infrastructure.Audio; // WavAudioSource, PcmAudioSource
using AudioClaudio.Infrastructure.Separation; // SpleeterSourceSeparator
using AudioClaudio.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace AudioClaudio.Tests.Spike;

/// <summary>
/// THROWAWAY feasibility spike — measurement only, NOT production code, NOT committed. Mirrors
/// <see cref="BasicPitchStreamingSpike"/>'s spike-first discipline (see DECISIONS.md "Live
/// polyphonic capture", Decision 2), but answers the NEW question raised by inserting source
/// separation into the existing <c>listen --view</c> live loop
/// (<c>docs/plans/2026-07-12-live-polyphony-design.md</c>): that loop already re-transcribes the
/// WHOLE captured buffer every ~1.64 s with a single Basic Pitch pass; the proposal is to route that
/// same growing buffer through <see cref="SpleeterSourceSeparator"/>'s 5 U-Nets FIRST and then
/// transcribe several of the resulting stems (piano via Transkun; bass/other/vocals via one shared
/// Basic Pitch instance) via <see cref="MultiStemTranscriber"/> — strictly more work per tick, over a
/// buffer that only grows. Before building that prototype (open task: "Live-separated `listen
/// --view` prototype"), this spike measures the real per-tick wall-clock cost on this machine (Apple
/// M3 Max) so any shipped length/cadence limit is measured, not guessed.
///
/// One measurement, three buffer lengths (5 s / 15 s / 30 s @ 44100 Hz mono — a live take's captured
/// buffer only grows, so longer lengths are strictly worse-case, not just "more data"): for each,
/// times (a) <see cref="SpleeterSourceSeparator.Separate"/> alone, (b) <see cref="MultiStemTranscriber.Transcribe"/>
/// alone (piano/bass/other/vocals, run back-to-back with no other work between them — drums is
/// dropped by design, see <c>MultiStemRouting</c>), and (c) their sum, i.e. what ONE live tick would
/// cost end to end. The separator and the routing table (which owns the Transkun + shared Basic
/// Pitch ONNX sessions) are constructed exactly ONCE, before any timed measurement, exactly as the
/// live path would load its models once at startup — never per tick.
///
/// Realism caveat: the input buffer is the committed golden Spleeter fixture
/// (<c>fixtures/models/spleeter/golden/test_input_mono.wav</c>, ~2 s of real mixed audio) TILED
/// (looped) to the target length, not a real multi-second performance — reproducible and
/// deterministic (no RNG), and it keeps the energy/spectral content realistic (as opposed to random
/// noise), but the looping means a 30 s buffer is 15 exact repeats of a 2 s clip, not naturalistic
/// variation. This is the same honesty tradeoff <see cref="BasicPitchStreamingSpike"/> already
/// documents for its own tiled grids.
///
/// Tagged <c>Category=Spike</c> so it is excluded from the normal/Fast suite — wall-clock timing is
/// inherently non-deterministic and must never gate CI. Run explicitly:
///   dotnet test --filter "Category=Spike" -l "console;verbosity=detailed"
/// </summary>
public class SeparationLiveSpike
{
    private readonly ITestOutputHelper _output;

    public SeparationLiveSpike(ITestOutputHelper output) => _output = output;

    // The live-view loop's real-time budget: one re-processing pass per hop.
    private const double HopBudgetMs = 1640.0;

    private static readonly (string Label, int Seconds)[] BufferLengths =
    {
        ("5s", 5),
        ("15s", 15),
        ("30s", 30),
    };

    [Fact]
    [Trait("Category", "Spike")]
    public void SeparateAndTranscribe_cost_vs_buffer_length()
    {
        var rate44 = new SampleRate(44100);
        var mixFrameParameters = new FrameParameters(4096, 4096);

        string goldenWav = RepoPaths.Fixture("models", "spleeter", "golden", "test_input_mono.wav");
        float[] goldenMono;
        using (var goldenSource = WavAudioSource.FromFile(goldenWav, mixFrameParameters))
        {
            goldenMono = Framing.ReconstructMono(goldenSource.Frames.ToList());
        }

        // Models loaded exactly once, precisely as the live path would at startup — never per tick.
        using var separator = new SpleeterSourceSeparator(SeparatorModelLocator.Resolve(null), new Radix2Fft());
        (IReadOnlyList<StemRoute> routing, IDisposable transcribers) = MultiStemRouting.Build();
        try
        {
            var multiStem = new MultiStemTranscriber(routing, rate44);

            // Warm-up: absorb ONNX session graph-init/first-call JIT cost on the raw ~2s golden clip,
            // discarded, before any timed measurement (mirrors BasicPitchStreamingSpike's M1 warm-up).
            var warmMix = new PcmAudioSource(goldenMono, rate44, mixFrameParameters);
            IReadOnlyList<SeparatedStem> warmStems = separator.Separate(warmMix);
            multiStem.Transcribe(warmStems);

            _output.WriteLine(
                "SeparationLiveSpike: separate + multi-stem transcribe cost vs buffer length " +
                $"(hop budget = {HopBudgetMs:F0} ms; RTF = total / budget, >1x means the tick misses budget):");

            foreach ((string label, int seconds) in BufferLengths)
            {
                int targetSamples = seconds * rate44.Hz;
                float[] tiled = TileMono(goldenMono, targetSamples);
                var mix = new PcmAudioSource(tiled, rate44, mixFrameParameters);

                var swSeparate = Stopwatch.StartNew();
                IReadOnlyList<SeparatedStem> stems = separator.Separate(mix);
                swSeparate.Stop();

                var swTranscribe = Stopwatch.StartNew();
                IReadOnlyList<StemTranscription> transcriptions = multiStem.Transcribe(stems);
                swTranscribe.Stop();

                double separateMs = swSeparate.Elapsed.TotalMilliseconds;
                double transcribeMs = swTranscribe.Elapsed.TotalMilliseconds;
                // One live tick is exactly this sequence — separate, then transcribe the very stems
                // just produced, nothing else in between — so the sum IS the combined per-tick cost.
                double totalMs = separateMs + transcribeMs;
                double rtf = totalMs / HopBudgetMs;
                int noteCount = transcriptions.Sum(t => t.Notes.Count);

                _output.WriteLine(
                    $"    {label,-4} separate={separateMs,9:F1} ms  transcribe={transcribeMs,9:F1} ms  " +
                    $"total={totalMs,9:F1} ms  RTF={rtf,6:F2}x  notes={noteCount,4}  " +
                    $"{(totalMs < HopBudgetMs ? "under" : "OVER")} hop budget");
            }
        }
        finally
        {
            transcribers.Dispose();
        }

        // Measurement-only spike: never gates CI (Category=Spike is excluded from the normal run).
        // The printed table above is the actual result; this assertion only confirms the spike ran.
        Assert.True(true);
    }

    // Repeats `source`'s samples to fill a `targetSamples`-length mono buffer (truncating the final
    // repeat) — a 1-D analogue of BasicPitchStreamingSpike's grid Tile helper.
    private static float[] TileMono(float[] source, int targetSamples)
    {
        var result = new float[targetSamples];
        int srcLen = source.Length;
        for (int i = 0; i < targetSamples; i++)
        {
            result[i] = source[i % srcLen];
        }

        return result;
    }
}
