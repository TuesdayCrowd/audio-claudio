using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Application.UseCases;
using AudioClaudio.Domain;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.Application;

public class LiveScoreProjectorTests
{
    private static readonly SampleRate Rate = new SampleRate(44100);
    private static readonly QuantizationGrid Grid =
        new QuantizationGrid(Rate, new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth);

    private static NoteEvent Note(int midi, long onsetSamples) =>
        new NoteEvent(new Pitch(midi), new SamplePosition(onsetSamples, Rate),
                     new SampleDuration(5512, Rate), velocity: 100); // ~1/8 note at 120 BPM

    private static List<ScoreElement> Flatten(Score score) =>
        score.Measures.SelectMany(m => m.Elements).ToList();

    [Fact]
    [Trait("Category", "Fast")]
    public void AddAccumulatesAndReturnsAScoreOverAllNotesSoFar()
    {
        var projector = new LiveScoreProjector(Grid);

        Score afterFirst = projector.Add(Note(60, 0));
        Score afterSecond = projector.Add(Note(62, 5512));

        Assert.Single(Flatten(afterFirst), e => e.Kind == ElementKind.Note);
        var secondNotes = Flatten(afterSecond).Where(e => e.Kind == ElementKind.Note).ToList();
        Assert.Equal(2, secondNotes.Count);
        Assert.Equal(60, secondNotes[0].Pitch!.Value.MidiNumber);
        Assert.Equal(62, secondNotes[1].Pitch!.Value.MidiNumber);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void AddNeverMutatesAPreviouslyReturnedScore()
    {
        var projector = new LiveScoreProjector(Grid);

        Score afterFirst = projector.Add(Note(60, 0));
        int notesInFirstSnapshot = Flatten(afterFirst).Count(e => e.Kind == ElementKind.Note);

        projector.Add(Note(62, 5512));

        // The Score returned for note 1 is a snapshot -- R6.2's append-only instinct, carried
        // into the live view: the performance grows, past Scores never change under you.
        Assert.Equal(notesInFirstSnapshot, Flatten(afterFirst).Count(e => e.Kind == ElementKind.Note));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void LiveScoresYieldsOneGrowingScorePerNoteMatchingDirectQuantize()
    {
        var notes = new[] { Note(60, 0), Note(62, 5512), Note(64, 11025) };
        var projector = new LiveScoreProjector(Grid);

        List<Score> scores = projector.LiveScores(notes).ToList();

        Assert.Equal(notes.Length, scores.Count);
        for (int i = 0; i < notes.Length; i++)
        {
            Score expected = Quantizer.Quantize(notes.Take(i + 1).ToList(), Grid);
            Assert.Equal(expected, scores[i]); // Score implements IEquatable<Score> (Step 6)
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void LiveScoresPrefixPropertyMatchesDirectQuantizeForRandomNoteSequences()
    {
        var genNote =
            from midi in Gen.Int[Pitch.MinMidi, Pitch.MaxMidi]
            from gapSamples in Gen.Int[1, 20]
            select (midi, gapSamples);

        var genSequence =
            from count in Gen.Int[1, 12]
            from notes in genNote.List[count]
            select notes;

        genSequence.Sample(sequence =>
        {
            long onset = 0;
            var notes = new List<NoteEvent>();
            foreach (var (midi, gapSamples) in sequence)
            {
                notes.Add(new NoteEvent(new Pitch(midi), new SamplePosition(onset, Rate),
                                        new SampleDuration(2000, Rate), velocity: 100));
                onset += gapSamples * 2000L;
            }

            var projector = new LiveScoreProjector(Grid);
            List<Score> produced = projector.LiveScores(notes).ToList();

            for (int i = 0; i < notes.Count; i++)
            {
                Score expected = Quantizer.Quantize(notes.Take(i + 1).ToList(), Grid);
                Assert.Equal(expected, produced[i]);
            }
        }, iter: 200);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void EstimateTempo_ConvergesTheLivePreviewToTheOnsetTempo_NotTheFallback()
    {
        const long gap = 26460; // 0.6 s at 44.1 kHz -> a 100 BPM median inter-onset interval
        var projector = new LiveScoreProjector(Grid, estimateTempo: true);

        // Below TempoEstimator's 3-note floor the fallback (the grid's 120) is kept, so the first
        // couple of notes don't make the staff re-flow.
        Assert.Equal(120.0, projector.Add(Note(60, 0)).Tempo.BeatsPerMinute);
        Assert.Equal(120.0, projector.Add(Note(62, gap)).Tempo.BeatsPerMinute);

        // From the third note on, the live tempo tracks the estimate (100), so the preview lands on
        // the same tempo the batch pass estimates on stop instead of jumping from 120.
        Assert.Equal(100.0, projector.Add(Note(64, 2 * gap)).Tempo.BeatsPerMinute);

        Score last = projector.Add(Note(65, 3 * gap));
        Assert.Equal(100.0, last.Tempo.BeatsPerMinute);
        // The live tempo IS TempoEstimator over the same notes with the same fallback the batch pass
        // uses -- that identity is precisely why the live preview and the final score converge.
        Assert.Equal(TempoEstimator.Estimate(projector.Events, new Tempo(120)).BeatsPerMinute,
                     last.Tempo.BeatsPerMinute);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void WithoutEstimateTempo_TheFixedGridTempoIsKept()
    {
        const long gap = 26460; // would imply 100 BPM if estimated
        var projector = new LiveScoreProjector(Grid); // estimateTempo defaults to false

        projector.Add(Note(60, 0));
        projector.Add(Note(62, gap));
        projector.Add(Note(64, 2 * gap));
        Score s = projector.Add(Note(65, 3 * gap));

        Assert.Equal(120.0, s.Tempo.BeatsPerMinute); // unchanged from the grid -- original behavior preserved
    }
}
