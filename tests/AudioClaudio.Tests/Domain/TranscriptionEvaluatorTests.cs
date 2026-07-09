using System.Collections.Generic;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Evaluation;
using Xunit;

namespace AudioClaudio.Tests.Domain;

/// <summary>
/// The measurement harness: note-level precision/recall/F1 between a candidate
/// transcription and a reference note-set, the yardstick for "does the audio
/// transcription match the score?". A predicted note matches a reference note
/// when the pitch is identical and the onset falls within a stated tolerance;
/// matching is one-to-one (bipartite), deterministic, and offset-agnostic by
/// default (a performance's durations need not match a score's).
/// </summary>
public class TranscriptionEvaluatorTests
{
    private static readonly SampleRate R44 = new(44100);

    private static NoteEvent Note(int midi, double onsetSeconds, double durationSeconds = 0.5)
    {
        long onset = (long)(onsetSeconds * R44.Hz);
        long dur = System.Math.Max(1, (long)(durationSeconds * R44.Hz));
        return new NoteEvent(new Pitch(midi), new SamplePosition(onset, R44), new SampleDuration(dur, R44));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void IdenticalNoteSets_ScorePerfectF1()
    {
        var reference = new List<NoteEvent> { Note(60, 0.0), Note(64, 0.5), Note(67, 1.0) };
        var candidate = new List<NoteEvent> { Note(60, 0.0), Note(64, 0.5), Note(67, 1.0) };

        NoteSetEvaluation e = TranscriptionEvaluator.Evaluate(candidate, reference, NoteMatchOptions.Default);

        Assert.Equal(3, e.TruePositives);
        Assert.Equal(0, e.FalsePositives);
        Assert.Equal(0, e.FalseNegatives);
        Assert.Equal(1.0, e.Precision);
        Assert.Equal(1.0, e.Recall);
        Assert.Equal(1.0, e.F1);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void DisjointPitches_ScoreZero()
    {
        var reference = new List<NoteEvent> { Note(60, 0.0), Note(64, 0.5) };
        var candidate = new List<NoteEvent> { Note(61, 0.0), Note(65, 0.5) };

        NoteSetEvaluation e = TranscriptionEvaluator.Evaluate(candidate, reference, NoteMatchOptions.Default);

        Assert.Equal(0, e.TruePositives);
        Assert.Equal(2, e.FalsePositives);
        Assert.Equal(2, e.FalseNegatives);
        Assert.Equal(0.0, e.F1);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void OnsetWithinTolerance_Matches_ButOutsideDoesNot()
    {
        var reference = new List<NoteEvent> { Note(60, 1.000) };
        var withinTol = TranscriptionEvaluator.Evaluate(
            new List<NoteEvent> { Note(60, 1.040) }, reference, NoteMatchOptions.Default); // 40 ms < 50 ms
        var outsideTol = TranscriptionEvaluator.Evaluate(
            new List<NoteEvent> { Note(60, 1.080) }, reference, NoteMatchOptions.Default); // 80 ms > 50 ms

        Assert.Equal(1, withinTol.TruePositives);
        Assert.Equal(0, outsideTol.TruePositives);
        Assert.Equal(1, outsideTol.FalsePositives);
        Assert.Equal(1, outsideTol.FalseNegatives);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void OctaveError_IsAWrongNote_NotAMatch()
    {
        var reference = new List<NoteEvent> { Note(60, 0.0) };
        var candidate = new List<NoteEvent> { Note(72, 0.0) }; // one octave up, same onset

        NoteSetEvaluation e = TranscriptionEvaluator.Evaluate(candidate, reference, NoteMatchOptions.Default);

        Assert.Equal(0, e.TruePositives);
        Assert.Equal(1, e.FalsePositives);
        Assert.Equal(1, e.FalseNegatives);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void MatchingIsOneToOne_OneCandidateCannotCoverTwoReferences()
    {
        // Two reference C4s a beat apart; a single candidate C4 can satisfy only one of them.
        var reference = new List<NoteEvent> { Note(60, 0.0), Note(60, 0.5) };
        var candidate = new List<NoteEvent> { Note(60, 0.0) };

        NoteSetEvaluation e = TranscriptionEvaluator.Evaluate(candidate, reference, NoteMatchOptions.Default);

        Assert.Equal(1, e.TruePositives);
        Assert.Equal(0, e.FalsePositives);
        Assert.Equal(1, e.FalseNegatives); // the second C4 goes unmatched
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void DuplicatedCandidates_CannotInflateScore()
    {
        var reference = new List<NoteEvent> { Note(60, 0.0) };
        var candidate = new List<NoteEvent> { Note(60, 0.0), Note(60, 0.0), Note(60, 0.0) };

        NoteSetEvaluation e = TranscriptionEvaluator.Evaluate(candidate, reference, NoteMatchOptions.Default);

        Assert.Equal(1, e.TruePositives);
        Assert.Equal(2, e.FalsePositives); // the two extra C4s are false positives
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Result_IsIndependentOfInputOrder()
    {
        var reference = new List<NoteEvent> { Note(67, 1.0), Note(60, 0.0), Note(64, 0.5) };
        var candidate = new List<NoteEvent> { Note(64, 0.5), Note(60, 0.02), Note(67, 1.10) };

        var a = TranscriptionEvaluator.Evaluate(candidate, reference, NoteMatchOptions.Default);
        var shuffled = new List<NoteEvent> { candidate[2], candidate[0], candidate[1] };
        var b = TranscriptionEvaluator.Evaluate(shuffled, reference, NoteMatchOptions.Default);

        Assert.Equal(a, b); // same confusion counts regardless of list order (determinism)
        Assert.Equal(2, a.TruePositives); // C4 (+20ms) and E4 match; G4 is +100ms, out of tol
    }
}
