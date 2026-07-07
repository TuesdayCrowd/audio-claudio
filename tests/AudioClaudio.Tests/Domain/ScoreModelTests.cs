using System;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class ScoreModelTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Note_element_carries_pitch_velocity_length_and_tie()
    {
        var note = ScoreElement.Note(new Pitch(60), velocity: 100, lengthTicks: 4, tiedToNext: true);
        Assert.Equal(ElementKind.Note, note.Kind);
        Assert.Equal(60, note.Pitch!.Value.MidiNumber);
        Assert.Equal(100, note.Velocity);
        Assert.Equal(4, note.LengthTicks);
        Assert.True(note.TiedToNext);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Rest_element_has_no_pitch_and_no_tie()
    {
        var rest = ScoreElement.Rest(2);
        Assert.Equal(ElementKind.Rest, rest.Kind);
        Assert.Null(rest.Pitch);
        Assert.Equal(2, rest.LengthTicks);
        Assert.False(rest.TiedToNext);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Elements_reject_non_positive_length()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScoreElement.Rest(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScoreElement.Note(new Pitch(60), 100, 0));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Measure_reports_total_ticks()
    {
        var measure = new Measure(new[] { ScoreElement.Note(new Pitch(60), 100, 4), ScoreElement.Rest(12) });
        Assert.Equal(16, measure.TotalTicks);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Score_carries_tempo_timesignature_and_measures_with_value_equality()
    {
        var a = new Score(new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth,
            new[] { new Measure(new[] { ScoreElement.Rest(16) }) });
        var b = new Score(new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth,
            new[] { new Measure(new[] { ScoreElement.Rest(16) }) });

        Assert.Equal(120.0, a.Tempo.BeatsPerMinute);
        Assert.Equal(TimeSignature.FourFour, a.TimeSignature);
        Assert.Single(a.Measures);
        Assert.Equal(a, b); // structural equality over tempo, signature, subdivision, measures
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Scores_differ_when_a_measure_element_differs()
    {
        var a = new Score(new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth,
            new[] { new Measure(new[] { ScoreElement.Rest(16) }) });
        var b = new Score(new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth,
            new[] { new Measure(new[] { ScoreElement.Note(new Pitch(60), 100, 16) }) });
        Assert.NotEqual(a, b);
    }
}
