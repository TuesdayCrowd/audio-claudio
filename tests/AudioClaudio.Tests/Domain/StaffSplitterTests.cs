using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Polyphony;
using Xunit;

namespace AudioClaudio.Tests.Domain;

/// <summary>
/// Staff splitting: assigns each pitch of a chord to the treble or bass staff by register (middle
/// C = MIDI 60 and up → treble, below → bass), so a chord straddling both hands becomes a treble
/// fragment and a bass fragment sharing the original onset/duration/velocity.
/// </summary>
public class StaffSplitterTests
{
    private static readonly SampleRate R = new(22050);

    private static Chord ChordOf(long onset, params int[] midi) => new(
        new SamplePosition(onset, R),
        midi.Select(m => new Pitch(m)).ToList(),
        new SampleDuration(11025, R),
        80);

    [Fact]
    [Trait("Category", "Fast")]
    public void Splits_a_straddling_chord_into_treble_and_bass()
    {
        (Chord? treble, Chord? bass) = StaffSplitter.Split(ChordOf(0, 48, 60, 67)); // C3, C4, G4

        Assert.NotNull(treble);
        Assert.NotNull(bass);
        Assert.Equal(new[] { 60, 67 }, treble!.Pitches.Select(p => p.MidiNumber));
        Assert.Equal(new[] { 48 }, bass!.Pitches.Select(p => p.MidiNumber));
        Assert.Equal(0, treble.Onset.Samples);
        Assert.Equal(0, bass.Onset.Samples);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void A_chord_entirely_above_the_split_has_no_bass()
    {
        (Chord? treble, Chord? bass) = StaffSplitter.Split(ChordOf(0, 64, 67, 72));

        Assert.NotNull(treble);
        Assert.Null(bass);
        Assert.Equal(3, treble!.Pitches.Count);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void A_chord_entirely_below_the_split_has_no_treble()
    {
        (Chord? treble, Chord? bass) = StaffSplitter.Split(ChordOf(0, 40, 48, 55));

        Assert.Null(treble);
        Assert.NotNull(bass);
        Assert.Equal(3, bass!.Pitches.Count);
    }
}
