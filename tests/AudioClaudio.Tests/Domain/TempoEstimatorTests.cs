using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class TempoEstimatorTests
{
    private static readonly SampleRate Rate = new(44100);

    private static NoteEvent Note(long onsetSamples) =>
        new(new Pitch(60), new SamplePosition(onsetSamples, Rate), new SampleDuration(4410, Rate));

    [Fact]
    [Trait("Category", "Fast")]
    public void EstimatesTempoFromEvenlySpacedOnsets()
    {
        // Eight onsets exactly 0.5 s apart (22050 samples at 44.1 kHz) -> a 0.5 s beat -> 120 BPM.
        var notes = new List<NoteEvent>();
        for (int i = 0; i < 8; i++)
        {
            notes.Add(Note(i * 22050L));
        }

        Tempo estimated = TempoEstimator.Estimate(notes, new Tempo(100));

        Assert.Equal(120.0, estimated.BeatsPerMinute);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void FoldsAnOctaveHighRawEstimateBackIntoRange()
    {
        // Eight onsets 0.25 s apart (11025 samples) -> a naive 240 BPM (eighths, not quarters)
        // -> folded down to 120 (240 / 2), which lies within [50, 180].
        var notes = new List<NoteEvent>();
        for (int i = 0; i < 8; i++)
        {
            notes.Add(Note(i * 11025L));
        }

        Tempo estimated = TempoEstimator.Estimate(notes, new Tempo(100));

        Assert.Equal(120.0, estimated.BeatsPerMinute);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void ReturnsTheFallbackTempoUnchangedWhenThereAreTooFewNotes()
    {
        var notes = new List<NoteEvent> { Note(0), Note(22050) };
        var fallback = new Tempo(90);

        Tempo estimated = TempoEstimator.Estimate(notes, fallback);

        Assert.Equal(fallback, estimated);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void EstimatesWithinTheValidatedRangeOnARealRecording()
    {
        // Real onset times (seconds), detected from a real Ode-to-Joy take recorded at ~131 BPM
        // (out/20260708_151619). Pins that the estimator recovers the played tempo from real,
        // unevenly spaced onsets, not just synthetic exactly-even ones.
        double[] onsetSecs =
        {
            1.370, 1.805, 2.288, 3.274, 3.692, 4.139, 4.650, 5.149, 5.567, 6.020,
            6.577, 7.285, 7.558, 9.863, 10.339, 10.780, 11.256, 11.715, 12.615, 13.026,
            13.189, 13.432, 13.903, 14.368, 14.820, 15.279, 15.941, 16.184,
        };
        List<NoteEvent> notes = onsetSecs
            .Select(seconds => Note((long)Math.Round(seconds * Rate.Hz)))
            .ToList();

        Tempo estimated = TempoEstimator.Estimate(notes, new Tempo(100));

        Assert.InRange(estimated.BeatsPerMinute, 125.0, 137.0);
    }
}
