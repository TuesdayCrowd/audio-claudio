using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Polyphony;
using Xunit;

namespace AudioClaudio.Tests.Domain;

/// <summary>
/// The polyphonic quantizer: overlapping notes → a grand-staff score of chords on two staves.
/// At 120 BPM / 4-4 / sixteenths and 22 050 Hz, one bar is 2 s = 44 100 samples and 16 ticks.
/// The load-bearing invariant is bar conservation PER STAFF: every measure's treble ticks and bass
/// ticks each sum to a full bar, independently.
/// </summary>
public class PolyphonicQuantizerTests
{
    private static readonly SampleRate R = new(22050);
    private static readonly QuantizationGrid Grid =
        new(R, new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth);
    private static readonly SampleDuration Window = new(1000, R);
    private const int TicksPerBar = 16;

    private static NoteEvent Note(int midi, long onset, long duration) =>
        new(new Pitch(midi), new SamplePosition(onset, R), new SampleDuration(duration, R));

    [Fact]
    [Trait("Category", "Fast")]
    public void A_whole_bar_chord_splits_across_the_two_staves()
    {
        var events = new List<NoteEvent> { Note(48, 0, 44100), Note(64, 0, 44100), Note(67, 0, 44100) };

        GrandStaffScore score = PolyphonicQuantizer.Quantize(events, Grid, Window);

        GrandStaffMeasure m = Assert.Single(score.Measures);
        ChordElement treble = Assert.Single(m.Treble);
        Assert.Equal(ElementKind.Note, treble.Kind);
        Assert.Equal(new[] { 64, 67 }, treble.Pitches.Select(p => p.MidiNumber)); // E4, G4 up top
        ChordElement bass = Assert.Single(m.Bass);
        Assert.Equal(new[] { 48 }, bass.Pitches.Select(p => p.MidiNumber));       // C3 in the bass
        Assert.Equal(TicksPerBar, m.Treble.Sum(e => e.LengthTicks));
        Assert.Equal(TicksPerBar, m.Bass.Sum(e => e.LengthTicks));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void An_empty_staff_is_filled_with_rests_to_a_full_bar()
    {
        var events = new List<NoteEvent> { Note(72, 0, 44100) }; // treble only

        GrandStaffScore score = PolyphonicQuantizer.Quantize(events, Grid, Window);

        GrandStaffMeasure m = Assert.Single(score.Measures);
        Assert.All(m.Bass, e => Assert.Equal(ElementKind.Rest, e.Kind));
        Assert.Equal(TicksPerBar, m.Bass.Sum(e => e.LengthTicks));
        Assert.Equal(TicksPerBar, m.Treble.Sum(e => e.LengthTicks));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Bar_conservation_holds_on_every_measure_of_both_staves()
    {
        var events = new List<NoteEvent>
        {
            Note(60, 0, 11025), Note(64, 0, 11025),  // bar 1, beat 1 chord (treble)
            Note(40, 0, 44100),                       // bar 1 bass whole note
            Note(72, 44100, 11025),                   // bar 2 treble
            Note(43, 44100, 44100),                   // bar 2 bass whole note
        };

        GrandStaffScore score = PolyphonicQuantizer.Quantize(events, Grid, Window);

        Assert.True(score.Measures.Count >= 2);
        foreach (GrandStaffMeasure m in score.Measures)
        {
            Assert.Equal(TicksPerBar, m.Treble.Sum(e => e.LengthTicks));
            Assert.Equal(TicksPerBar, m.Bass.Sum(e => e.LengthTicks));
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void No_events_yields_no_measures()
    {
        GrandStaffScore score = PolyphonicQuantizer.Quantize(new List<NoteEvent>(), Grid, Window);
        Assert.Empty(score.Measures);
    }
}
