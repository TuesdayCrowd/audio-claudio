using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Polyphony;
using Xunit;

namespace AudioClaudio.Tests.Domain;

/// <summary>
/// Chord grouping: the first step of polyphonic score-building. Notes whose onsets fall within a
/// small window collapse into one chord (a vertical sonority); notes further apart stay separate.
/// Deterministic — pitches within a chord are sorted, chords ordered by onset.
/// </summary>
public class ChordGrouperTests
{
    private static readonly SampleRate R = new(22050);

    private static NoteEvent Note(int midi, long onset, long duration = 11025) =>
        new(new Pitch(midi), new SamplePosition(onset, R), new SampleDuration(duration, R));

    [Fact]
    [Trait("Category", "Fast")]
    public void Notes_within_the_window_group_into_one_chord()
    {
        var window = new SampleDuration(1000, R);
        var events = new List<NoteEvent> { Note(60, 0), Note(67, 900), Note(64, 500) };

        IReadOnlyList<Chord> chords = ChordGrouper.Group(events, window);

        Chord chord = Assert.Single(chords);
        Assert.Equal(new[] { 60, 64, 67 }, chord.Pitches.Select(p => p.MidiNumber)); // sorted
        Assert.Equal(0, chord.Onset.Samples);                                        // earliest onset
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Notes_beyond_the_window_form_separate_chords()
    {
        var window = new SampleDuration(1000, R);
        var events = new List<NoteEvent> { Note(60, 0), Note(64, 5000) };

        IReadOnlyList<Chord> chords = ChordGrouper.Group(events, window);

        Assert.Equal(2, chords.Count);
        Assert.Equal(0, chords[0].Onset.Samples);
        Assert.Equal(5000, chords[1].Onset.Samples);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void The_window_is_measured_from_the_chord_anchor_not_chained()
    {
        // 0 and 900 group (within 1000 of anchor 0); 1800 is >1000 from the anchor, so it starts a
        // new chord even though it is within 1000 of 900 — a defined, non-chaining rule.
        var window = new SampleDuration(1000, R);
        var events = new List<NoteEvent> { Note(60, 0), Note(62, 900), Note(64, 1800) };

        IReadOnlyList<Chord> chords = ChordGrouper.Group(events, window);

        Assert.Equal(2, chords.Count);
        Assert.Equal(2, chords[0].Pitches.Count); // {60, 62}
        Assert.Single(chords[1].Pitches);         // {64}
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void A_chord_duration_is_the_longest_of_its_notes()
    {
        var window = new SampleDuration(1000, R);
        var events = new List<NoteEvent> { Note(60, 0, 5000), Note(64, 100, 22050) };

        Chord chord = Assert.Single(ChordGrouper.Group(events, window));

        Assert.Equal(22050, chord.Duration.Samples); // the sonority lasts as long as its longest note
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Empty_input_yields_no_chords()
    {
        Assert.Empty(ChordGrouper.Group(new List<NoteEvent>(), new SampleDuration(1000, R)));
    }
}
