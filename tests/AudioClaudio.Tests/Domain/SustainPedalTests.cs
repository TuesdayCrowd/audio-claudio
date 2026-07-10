using System;
using System.Linq;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

/// <summary>
/// Sustain-pedal flattening: a MIDI that models the pedal as CC64 events (short notes + a held pedal,
/// as a good piano transcriber emits) plays <i>dry</i> if the synth ignores the pedal. Folding each
/// pedal-down span into the durations of the notes it sustains makes the notes ring as they should,
/// through the existing note→synth path. A note whose key-release falls while the pedal is down is
/// extended to the pedal's release. Pure and deterministic; input not mutated.
/// </summary>
public class SustainPedalTests
{
    private static readonly SampleRate R = new(44100);

    private static NoteEvent Note(int midi, long onset, long duration, int velocity = 80) =>
        new(new Pitch(midi), new SamplePosition(onset, R), new SampleDuration(duration, R), velocity);

    [Fact]
    [Trait("Category", "Fast")]
    public void A_note_released_while_the_pedal_is_down_extends_to_the_pedal_release()
    {
        var notes = new[] { Note(60, 0, 100) }; // key up at sample 100
        var pedal = new[] { new SustainPedal.Change(50, true), new SustainPedal.Change(300, false) };

        var flattened = SustainPedal.Flatten(notes, pedal);

        Assert.Equal(300, flattened[0].Duration.Samples); // rings until the pedal lifts at 300
        Assert.Equal(0, flattened[0].Onset.Samples);       // onset untouched
        Assert.Equal(60, flattened[0].Pitch.MidiNumber);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void A_note_released_after_the_pedal_is_already_up_is_unchanged()
    {
        var notes = new[] { Note(60, 0, 400) }; // key up at 400, pedal already released at 300
        var pedal = new[] { new SustainPedal.Change(50, true), new SustainPedal.Change(300, false) };

        Assert.Equal(400, SustainPedal.Flatten(notes, pedal)[0].Duration.Samples);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void No_pedal_leaves_every_note_unchanged()
    {
        var notes = new[] { Note(60, 0, 100), Note(64, 50, 120) };

        var flattened = SustainPedal.Flatten(notes, Array.Empty<SustainPedal.Change>());

        Assert.Equal(new long[] { 100, 120 }, flattened.Select(n => n.Duration.Samples));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void A_pedal_press_with_no_later_release_leaves_the_note()
    {
        var notes = new[] { Note(60, 0, 100) };
        var pedal = new[] { new SustainPedal.Change(50, true) }; // never lifts

        Assert.Equal(100, SustainPedal.Flatten(notes, pedal)[0].Duration.Samples);
    }
}
