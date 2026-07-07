using System.Collections.Generic;
using AudioClaudio.Domain;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class QuantizationProperties
{
    private static readonly SampleRate Rate = new(48000);
    private static readonly QuantizationGrid Grid =
        new(Rate, new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth); // SamplesPerTick = 6000

    /// <summary>
    /// A note = (pitch, duration index, leading gap >= 1 tick, sub-tick onset jitter).
    /// |jitter| &lt; half a tick (3000 samples) so the onset snaps back to its tick.
    /// Shared by the idempotence and bar-conservation properties so both explore the
    /// same constrained corpus.
    /// </summary>
    private static Gen<List<(int Midi, int DurationTicks, int GapTicks, int Jitter)>> NoteSequenceGen()
    {
        IReadOnlyList<int> standard = Grid.StandardValueTicks; // [1,2,3,4,6,8,12,16]

        var noteGen = Gen.Select(
            Gen.Int[33, 96],
            Gen.Int[0, standard.Count - 1],
            Gen.Int[1, 4],
            Gen.Int[-2000, 2000],
            (midi, durationIndex, gapTicks, jitter) =>
                (midi, durationTicks: standard[durationIndex], gapTicks, jitter));

        return noteGen.List[1, 6];
    }

    /// <summary>Lays a generated note sequence onto the shared <see cref="Grid"/> as raw events.</summary>
    private static List<NoteEvent> BuildEvents(
        List<(int Midi, int DurationTicks, int GapTicks, int Jitter)> sequence)
    {
        var events = new List<NoteEvent>();
        long tick = 0;
        foreach (var (midi, durationTicks, gapTicks, jitter) in sequence)
        {
            tick += gapTicks; // a rest of >= 1 tick before each note
            long onsetSamples = tick * 6000 + jitter;
            if (onsetSamples < 0)
            {
                onsetSamples = 0;
            }

            events.Add(new NoteEvent(
                new Pitch(midi),
                new SamplePosition(onsetSamples, Rate),
                new SampleDuration(durationTicks * 6000, Rate),
                100));
            tick += durationTicks;
        }

        return events;
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Quantize_is_idempotent_on_the_constrained_corpus()
    {
        NoteSequenceGen().Sample(sequence =>
        {
            List<NoteEvent> events = BuildEvents(sequence);

            Score score1 = Quantizer.Quantize(events, Grid);
            IReadOnlyList<NoteEvent> onGrid = QuantizationTestHelpers.ReifyOnGridEvents(score1, Grid);
            Score score2 = Quantizer.Quantize(onGrid, Grid);

            Assert.Equal(score1, score2);
        }, iter: 1000, seed: "cu2_IjV3Gub6");
        // Seed pinned up front for reproducible CI (matches the convention in
        // PitchMathTests.CentsBetween_IsAntisymmetric); replace with any CsCheck-reported
        // seed to reproduce a failure (see @superpowers:systematic-debugging).
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Quantize_all_measures_sum_to_one_bar_across_generated_scores()
    {
        // R6.4's bar-conservation invariant, asserted as a property (not just the single
        // worked example in QuantizerTests): for any generated note sequence, every
        // resulting measure's element lengths sum to exactly one bar.
        NoteSequenceGen().Sample(sequence =>
        {
            List<NoteEvent> events = BuildEvents(sequence);
            Score score = Quantizer.Quantize(events, Grid);

            Assert.All(score.Measures, m => Assert.Equal(Grid.TicksPerMeasure, m.TotalTicks));
        }, iter: 1000, seed: "djt0gyJx--d3");
        // Seed pinned up front for reproducible CI; replace with any CsCheck-reported seed
        // to reproduce a failure (see @superpowers:systematic-debugging).
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Quantize_is_deterministic()
    {
        var events = new[]
        {
            new NoteEvent(new Pitch(60), new SamplePosition(0, Rate), new SampleDuration(24000, Rate), 100),
            new NoteEvent(new Pitch(64), new SamplePosition(36000, Rate), new SampleDuration(12000, Rate), 100),
        };

        Assert.Equal(Quantizer.Quantize(events, Grid), Quantizer.Quantize(events, Grid));
    }
}
