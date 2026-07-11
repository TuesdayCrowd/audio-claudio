using System;
using System.Linq;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class QuantizerTests
{
    private static readonly SampleRate Rate = new(48000);
    private static readonly QuantizationGrid Grid48 =
        new(Rate, new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth); // SamplesPerTick = 6000

    private static NoteEvent Event(int midi, long onsetTicks, int durationTicks, int velocity = 100) =>
        new(new Pitch(midi),
            new SamplePosition(onsetTicks * 6000, Rate),
            new SampleDuration(durationTicks * 6000, Rate),
            velocity);

    [Fact]
    [Trait("Category", "Fast")]
    public void Quantize_empty_events_yields_a_score_with_no_measures()
    {
        Score score = Quantizer.Quantize(Array.Empty<NoteEvent>(), Grid48);
        Assert.Empty(score.Measures);
        Assert.Equal(120.0, score.Tempo.BeatsPerMinute);
        Assert.Equal(TimeSignature.FourFour, score.TimeSignature);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Quantize_reproduces_on_grid_events_exactly_and_fills_gaps_with_rests()
    {
        // Quarter at tick 0, eighth at tick 6, inside one 16-tick measure.
        var events = new[] { Event(60, onsetTicks: 0, durationTicks: 4), Event(62, onsetTicks: 6, durationTicks: 2) };

        Score score = Quantizer.Quantize(events, Grid48);

        var expected = new Score(Grid48.Tempo, Grid48.TimeSignature, Grid48.Subdivision, new[]
        {
            new Measure(new[]
            {
                ScoreElement.Note(new Pitch(60), 100, 4),
                ScoreElement.Rest(2),
                ScoreElement.Note(new Pitch(62), 100, 2),
                ScoreElement.Rest(8),
            }),
        });

        Assert.Equal(expected, score);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Quantize_inserts_a_leading_rest_when_the_first_note_is_late()
    {
        var events = new[] { Event(64, onsetTicks: 4, durationTicks: 4) };

        Score score = Quantizer.Quantize(events, Grid48);

        var expected = new Score(Grid48.Tempo, Grid48.TimeSignature, Grid48.Subdivision, new[]
        {
            new Measure(new[]
            {
                ScoreElement.Rest(4),
                ScoreElement.Note(new Pitch(64), 100, 4),
                ScoreElement.Rest(8),
            }),
        });

        Assert.Equal(expected, score);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Quantize_does_not_mutate_input()
    {
        var events = new[] { Event(60, 0, 4), Event(62, 6, 2) };
        NoteEvent[] snapshot = events.ToArray();

        _ = Quantizer.Quantize(events, Grid48);

        Assert.Equal(snapshot, events); // NoteEvent has value equality; the list is unchanged
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Quantize_rejects_sample_rate_mismatch()
    {
        // Onset carries 44.1 kHz but the grid is 48 kHz — the currency-mismatch rule (non-negotiable 1).
        var mismatched = new NoteEvent(
            new Pitch(60),
            new SamplePosition(0, new SampleRate(44100)),
            new SampleDuration(24000, new SampleRate(44100)),
            100);

        Assert.Throws<ArgumentException>(() => Quantizer.Quantize(new[] { mismatched }, Grid48));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Quantize_splits_note_across_barline_with_tie()
    {
        // Quarter (4 ticks) starting at tick 14 crosses the barline at 16 -> 2 + 2, tied.
        var events = new[] { Event(60, onsetTicks: 14, durationTicks: 4) };

        Score score = Quantizer.Quantize(events, Grid48);

        var expected = new Score(Grid48.Tempo, Grid48.TimeSignature, Grid48.Subdivision, new[]
        {
            new Measure(new[]
            {
                ScoreElement.Rest(14),
                ScoreElement.Note(new Pitch(60), 100, 2, tiedToNext: true),
            }),
            new Measure(new[]
            {
                ScoreElement.Note(new Pitch(60), 100, 2, tiedToNext: false),
                ScoreElement.Rest(14),
            }),
        });

        Assert.Equal(expected, score);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Quantize_measures_each_sum_to_one_bar()
    {
        var events = new[]
        {
            Event(60, onsetTicks: 1, durationTicks: 4),
            Event(64, onsetTicks: 9, durationTicks: 8),
            Event(67, onsetTicks: 20, durationTicks: 4),
        };

        Score score = Quantizer.Quantize(events, Grid48);

        Assert.NotEmpty(score.Measures);
        Assert.All(score.Measures, m => Assert.Equal(Grid48.TicksPerMeasure, m.TotalTicks));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Quantize_honours_declared_tempo_same_events_two_tempos_differ()
    {
        // R6.3: tempo is a declared input, never estimated. The identical event stream
        // quantized at two tempos lands on different grids and yields different scores.
        var onsetHalfSecond = new SamplePosition(24000, Rate); // 0.5 s at 48 kHz
        var quarterSecond = new SampleDuration(12000, Rate);   // 0.25 s
        var events = new[] { new NoteEvent(new Pitch(60), onsetHalfSecond, quarterSecond, 100) };

        var grid120 = new QuantizationGrid(Rate, new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth);
        var grid60 = new QuantizationGrid(Rate, new Tempo(60), TimeSignature.FourFour, Subdivision.Sixteenth);

        Score at120 = Quantizer.Quantize(events, grid120);
        Score at60 = Quantizer.Quantize(events, grid60);

        Assert.NotEqual(at120, at60);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void CoarseRhythm_rounds_a_sixteenth_up_to_an_eighth()
    {
        var note = new[] { Event(60, onsetTicks: 0, durationTicks: 1) }; // a sixteenth (1 tick)

        int fine = FirstNoteLen(Quantizer.Quantize(note, Grid48));
        int coarse = FirstNoteLen(Quantizer.Quantize(note, Grid48, coarseGridTicks: Grid48.TicksPerBeat / 2));

        Assert.Equal(1, fine);   // default keeps the sixteenth
        Assert.Equal(2, coarse); // coarse-grid note-off rounds it up to an eighth

        static int FirstNoteLen(Score s) =>
            s.Measures.SelectMany(m => m.Elements).First(e => e.Kind == ElementKind.Note).LengthTicks;
    }
}
