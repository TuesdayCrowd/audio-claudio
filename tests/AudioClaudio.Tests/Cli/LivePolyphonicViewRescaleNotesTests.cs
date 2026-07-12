using System.Collections.Generic;
using AudioClaudio.Cli.Commands;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Cli;

/// <summary>
/// The one piece of the polyphonic `listen` engine's --record/--skip-silence restoration
/// (<see cref="LivePolyphonicView.RescaleNotes"/>) that is pure and device-free: converting a note
/// list declared at one <see cref="SampleRate"/> (the poly engine's own, internally-resampled rate)
/// into another (the mic's) by an exact ratio, so rate-sensitive helpers downstream (SilenceCollapser,
/// ISynthesizer.Render) that require notes and audio to share one declared rate can be reused unchanged.
/// </summary>
public class LivePolyphonicViewRescaleNotesTests
{
    private static readonly SampleRate PolyRate = new(22050);
    private static readonly SampleRate MicRate = new(44100);

    private static NoteEvent MakeNote(SampleRate rate, long onset, long duration, int midi = 60, int velocity = 100) =>
        new(new Pitch(midi), new SamplePosition(onset, rate), new SampleDuration(duration, rate), velocity);

    [Fact]
    [Trait("Category", "Fast")]
    public void Doubling_the_rate_exactly_doubles_onset_and_duration_sample_counts()
    {
        var note = MakeNote(PolyRate, onset: 1000, duration: 500);

        var rescaled = LivePolyphonicView.RescaleNotes(new[] { note }, MicRate);

        Assert.Single(rescaled);
        Assert.Equal(2000, rescaled[0].Onset.Samples);
        Assert.Equal(1000, rescaled[0].Duration.Samples);
        Assert.Equal(MicRate, rescaled[0].Onset.Rate);
        Assert.Equal(MicRate, rescaled[0].Duration.Rate);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Pitch_and_velocity_are_preserved_across_the_rescale()
    {
        var note = MakeNote(PolyRate, onset: 0, duration: 100, midi: 72, velocity: 88);

        var rescaled = LivePolyphonicView.RescaleNotes(new[] { note }, MicRate);

        Assert.Equal(72, rescaled[0].Pitch.MidiNumber);
        Assert.Equal(88, rescaled[0].Velocity);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Rescaling_to_the_same_rate_is_a_no_op_on_values()
    {
        var note = MakeNote(PolyRate, onset: 12345, duration: 678);

        var rescaled = LivePolyphonicView.RescaleNotes(new[] { note }, PolyRate);

        Assert.Equal(12345, rescaled[0].Onset.Samples);
        Assert.Equal(678, rescaled[0].Duration.Samples);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void A_sub_ratio_duration_clamps_to_at_least_one_sample()
    {
        // Halving the rate can round a short duration down to zero; the domain requires a sounding
        // note to have positive length (mirrors NoteEvent's own downstream consumers, e.g. DryWetMidiWriter).
        var note = MakeNote(MicRate, onset: 0, duration: 1);

        var rescaled = LivePolyphonicView.RescaleNotes(new[] { note }, PolyRate);

        Assert.True(rescaled[0].Duration.Samples >= 1);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Empty_input_returns_empty_without_throwing()
    {
        var rescaled = LivePolyphonicView.RescaleNotes(Array.Empty<NoteEvent>(), MicRate);

        Assert.Empty(rescaled);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Ordering_of_multiple_notes_is_preserved()
    {
        var notes = new List<NoteEvent>
        {
            MakeNote(PolyRate, onset: 100, duration: 50, midi: 60),
            MakeNote(PolyRate, onset: 200, duration: 50, midi: 64),
            MakeNote(PolyRate, onset: 300, duration: 50, midi: 67),
        };

        var rescaled = LivePolyphonicView.RescaleNotes(notes, MicRate);

        Assert.Equal(3, rescaled.Count);
        Assert.Equal(new[] { 200L, 400L, 600L }, new[] { rescaled[0].Onset.Samples, rescaled[1].Onset.Samples, rescaled[2].Onset.Samples });
        Assert.Equal(new[] { 60, 64, 67 }, new[] { rescaled[0].Pitch.MidiNumber, rescaled[1].Pitch.MidiNumber, rescaled[2].Pitch.MidiNumber });
    }
}
