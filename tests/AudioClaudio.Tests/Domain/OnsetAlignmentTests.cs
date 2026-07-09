using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Evaluation;
using Xunit;

namespace AudioClaudio.Tests.Domain;

/// <summary>
/// Global time-alignment for scoring: a performance (candidate) and a score (reference) sit on
/// different time bases (rubato + overall tempo). Rescaling the candidate's onset span to the
/// reference's before matching removes the gross tempo difference, so the note-F1 reflects pitch
/// recovery rather than tempo drift. (A first-order fix; DTW is the later refinement.)
/// </summary>
public class OnsetAlignmentTests
{
    private static readonly SampleRate R = new(44100);

    private static NoteEvent Note(int midi, long onset, long duration = 4410) =>
        new(new Pitch(midi), new SamplePosition(onset, R), new SampleDuration(duration, R));

    [Fact]
    [Trait("Category", "Fast")]
    public void Rescales_the_candidate_onset_span_to_the_reference_span()
    {
        var candidate = new List<NoteEvent> { Note(60, 0), Note(62, 50), Note(64, 100) }; // span 100
        var reference = new List<NoteEvent> { Note(60, 0), Note(64, 200) };                // span 200

        IReadOnlyList<NoteEvent> aligned = OnsetAlignment.GlobalScale(candidate, reference);

        Assert.Equal(0, aligned[0].Onset.Samples);   // start anchored
        Assert.Equal(100, aligned[1].Onset.Samples); // midpoint scales 2x
        Assert.Equal(200, aligned[2].Onset.Samples); // end anchored to the reference span
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Pitches_and_ordering_are_preserved()
    {
        var candidate = new List<NoteEvent> { Note(60, 0), Note(67, 80) };
        var reference = new List<NoteEvent> { Note(60, 0), Note(67, 40) };

        IReadOnlyList<NoteEvent> aligned = OnsetAlignment.GlobalScale(candidate, reference);

        Assert.Equal(new[] { 60, 67 }, aligned.Select(e => e.Pitch.MidiNumber));
        Assert.True(aligned[1].Onset.Samples < candidate[1].Onset.Samples); // compressed toward the shorter reference
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Aligning_makes_a_tempo_scaled_transcription_match()
    {
        // Same notes, candidate played at half the reference's tempo (onsets 2x, seconds apart).
        // Raw matching at a tight tolerance fails; after global alignment they line up.
        var reference = new List<NoteEvent> { Note(60, 0), Note(64, 44100), Note(67, 88200) };  // 0s, 1s, 2s
        var candidate = new List<NoteEvent> { Note(60, 0), Note(64, 88200), Note(67, 176400) }; // 0s, 2s, 4s

        var opts = new NoteMatchOptions(0.01); // 10 ms — tight
        NoteSetEvaluation raw = TranscriptionEvaluator.Evaluate(candidate, reference, opts);
        NoteSetEvaluation aligned = TranscriptionEvaluator.Evaluate(
            OnsetAlignment.GlobalScale(candidate, reference), reference, opts);

        Assert.True(raw.TruePositives < 3);      // the middle/late notes drift out of tolerance
        Assert.Equal(3, aligned.TruePositives);  // alignment recovers all three
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Empty_candidate_is_returned_unchanged()
    {
        Assert.Empty(OnsetAlignment.GlobalScale(new List<NoteEvent>(), new List<NoteEvent> { Note(60, 0) }));
    }
}
