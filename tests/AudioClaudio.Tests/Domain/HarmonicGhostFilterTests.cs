using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Polyphony;
using Xunit;

namespace AudioClaudio.Tests.Domain;

/// <summary>
/// Sub-octave harmonic-ghost suppression: neural polyphonic transcribers over-generate a weak note one
/// octave below a real one (its own sub-harmonic). <see cref="HarmonicGhostFilter"/> drops a note when a
/// note an octave ABOVE it overlaps in time and is meaningfully louder — the artifact is weaker than the
/// note that spawned it — while keeping genuine bass octaves, where the lower note is comparably loud.
/// A precision fix that (validated on the synthetic closed loop) does not cost real recall.
/// </summary>
public class HarmonicGhostFilterTests
{
    private static readonly SampleRate R = new(22050);

    private static NoteEvent Note(int midi, long onset, long duration, int velocity) =>
        new(new Pitch(midi), new SamplePosition(onset, R), new SampleDuration(duration, R), velocity);

    [Fact]
    [Trait("Category", "Fast")]
    public void Weak_suboctave_ghost_under_a_louder_octave_is_removed()
    {
        var notes = new List<NoteEvent>
        {
            Note(60, 0, 1000, 100), // C4, strong
            Note(48, 0, 1000, 40),  // C3, weak — an octave below, overlapping: a sub-harmonic ghost
        };

        IReadOnlyList<NoteEvent> kept = HarmonicGhostFilter.Suppress(notes);

        Assert.Equal(new[] { 60 }, kept.Select(n => n.Pitch.MidiNumber));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void A_comparably_loud_octave_is_kept_as_a_real_bass_octave()
    {
        var notes = new List<NoteEvent>
        {
            Note(60, 0, 1000, 100),
            Note(48, 0, 1000, 95), // nearly as loud → a real octave, not a ghost
        };

        IReadOnlyList<NoteEvent> kept = HarmonicGhostFilter.Suppress(notes);

        Assert.Equal(new[] { 48, 60 }, kept.Select(n => n.Pitch.MidiNumber).OrderBy(m => m));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void A_non_overlapping_octave_below_is_kept()
    {
        var notes = new List<NoteEvent>
        {
            Note(60, 0, 500, 100),
            Note(48, 1000, 500, 40), // weak, but does not overlap the C4 → not its ghost
        };

        IReadOnlyList<NoteEvent> kept = HarmonicGhostFilter.Suppress(notes);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void A_note_with_no_octave_above_is_kept()
    {
        var notes = new List<NoteEvent> { Note(48, 0, 1000, 40), Note(55, 0, 1000, 100) }; // G3 is a fifth, not an octave
        Assert.Equal(2, HarmonicGhostFilter.Suppress(notes).Count);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Input_is_not_mutated()
    {
        var notes = new List<NoteEvent> { Note(60, 0, 1000, 100), Note(48, 0, 1000, 40) };
        HarmonicGhostFilter.Suppress(notes);
        Assert.Equal(2, notes.Count); // original list untouched
    }
}
