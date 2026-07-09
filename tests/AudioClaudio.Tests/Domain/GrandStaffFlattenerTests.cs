using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Polyphony;
using Xunit;

namespace AudioClaudio.Tests.Domain;

/// <summary>
/// Flattening a grand-staff score back to notes, with tie-merging: a chord split across a barline
/// must come back as ONE note per pitch, not two, and the polyphony must survive the round trip.
/// </summary>
public class GrandStaffFlattenerTests
{
    private static readonly SampleRate R = new(22050);
    private static readonly QuantizationGrid Grid =
        new(R, new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth);
    private static readonly SampleDuration Window = new(1000, R);

    private static NoteEvent Note(int midi, long onset, long duration) =>
        new(new Pitch(midi), new SamplePosition(onset, R), new SampleDuration(duration, R));

    [Fact]
    [Trait("Category", "Fast")]
    public void A_note_crossing_a_barline_flattens_to_one_event()
    {
        // Onset at tick 8 (mid-bar), a dotted-half (12 ticks) reaches to tick 20 — across the barline
        // at 16 — so the quantizer stores it as two tied elements. Flattening must re-merge them.
        var score = PolyphonicQuantizer.Quantize(
            new List<NoteEvent> { Note(72, 22050, 33075) }, Grid, Window); // 22050=tick8, 33075≈12 ticks

        IReadOnlyList<NoteEvent> events = GrandStaffFlattener.ToNoteEvents(score, Grid);

        NoteEvent e = Assert.Single(events);
        Assert.Equal(72, e.Pitch.MidiNumber);
        Assert.Equal(22050, e.Onset.Samples);                 // tick 8
        Assert.InRange(e.Duration.Samples, 33000, 33100);     // ~12 ticks, spanning the barline as one note
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Polyphony_survives_the_round_trip()
    {
        var events = new List<NoteEvent> { Note(48, 0, 44100), Note(64, 0, 44100), Note(67, 0, 44100) };
        var score = PolyphonicQuantizer.Quantize(events, Grid, Window);

        IReadOnlyList<NoteEvent> flat = GrandStaffFlattener.ToNoteEvents(score, Grid);

        // All three pitches come back, all sounding together at onset 0.
        Assert.Equal(new[] { 48, 64, 67 }, flat.Select(e => e.Pitch.MidiNumber).OrderBy(m => m));
        Assert.All(flat, e => Assert.Equal(0, e.Onset.Samples));
    }
}
