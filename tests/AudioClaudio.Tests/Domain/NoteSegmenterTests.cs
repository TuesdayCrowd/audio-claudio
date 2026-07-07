using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public sealed class NoteSegmenterTests
{
    private static readonly SampleRate Rate = new(44100);
    private const long Hop = 512;

    private static FrameObservation Voiced(int index, int midi, double energy = 1.0)
        => new(new SamplePosition(index * Hop, Rate), new Pitch(midi), energy);

    private static FrameObservation Silent(int index)
        => new(new SamplePosition(index * Hop, Rate), null, 0.0);

    private static NoteSegmenter MakeSegmenter(long minDurationSamples = 1000, double decayFloor = 0.0)
        => new(new NoteSegmenterOptions
        {
            MinNoteDuration = new SampleDuration(minDurationSamples, Rate),
            StabilityFrames = 2,
            DecayFloorRatio = decayFloor,
            Velocity = 64,
        });

    [Fact]
    [Trait("Category", "Fast")]
    public void SingleNoteBecomesOneEventEndingAtUnvoiced()
    {
        var frames = new List<FrameObservation>
        {
            Silent(0), Silent(1),
            Voiced(2, 60), Voiced(3, 60), Voiced(4, 60),
            Voiced(5, 60), Voiced(6, 60), Voiced(7, 60),
            Silent(8), Silent(9),
        };

        var events = MakeSegmenter().Segment(frames, new[] { 2 });

        var e = Assert.Single(events);
        Assert.Equal(60, e.Pitch.MidiNumber);
        Assert.Equal(2 * Hop, e.Onset.Samples);          // onset at its frame start
        Assert.Equal((8 - 2) * Hop, e.Duration.Samples); // ends at the first unvoiced frame
        Assert.Equal(64, e.Velocity);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void OnsetWithoutStableVoicedPitchIsDropped()
    {
        var frames = new List<FrameObservation>
        {
            Silent(0), Silent(1), Silent(2), Silent(3), Silent(4),
        };

        var events = MakeSegmenter().Segment(frames, new[] { 2 });

        Assert.Empty(events);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void NotesShorterThanMinimumAreDroppedAsFlicker()
    {
        var frames = new List<FrameObservation>
        {
            Silent(0), Silent(1),
            Voiced(2, 60), Voiced(3, 60), Voiced(4, 60),
            Voiced(5, 60), Voiced(6, 60), Voiced(7, 60),
            Silent(8),
            Voiced(9, 67), Voiced(10, 67),   // a 1024-sample blip
            Silent(11),
        };

        // Min duration 2000 samples: the long note (3072) survives, the blip (1024) is dropped.
        var events = MakeSegmenter(minDurationSamples: 2000).Segment(frames, new[] { 2, 9 });

        var e = Assert.Single(events);
        Assert.Equal(60, e.Pitch.MidiNumber);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void BackToBackSamePitchNotesAreSplitByTheSecondOnset()
    {
        // Ten consecutive voiced C4 frames, no silence anywhere; two onsets at 2 and 7.
        var frames = new List<FrameObservation>
        {
            Silent(0), Silent(1),
            Voiced(2, 60), Voiced(3, 60), Voiced(4, 60), Voiced(5, 60), Voiced(6, 60),
            Voiced(7, 60), Voiced(8, 60), Voiced(9, 60), Voiced(10, 60), Voiced(11, 60),
        };

        var events = MakeSegmenter().Segment(frames, new[] { 2, 7 });

        Assert.Equal(2, events.Count);
        Assert.Equal(2 * Hop, events[0].Onset.Samples);
        Assert.Equal(7 * Hop, events[1].Onset.Samples);
        // The first note must stop at the second onset, not run through it.
        Assert.Equal((7 - 2) * Hop, events[0].Duration.Samples);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void RepeatedSamePitchNotesSeparatedByRestAreDistinctEvents()
    {
        var frames = new List<FrameObservation>
        {
            Silent(0), Silent(1),
            Voiced(2, 60), Voiced(3, 60), Voiced(4, 60), Voiced(5, 60),
            Silent(6), Silent(7),
            Voiced(8, 60), Voiced(9, 60), Voiced(10, 60), Voiced(11, 60),
            Silent(12),
        };

        var events = MakeSegmenter().Segment(frames, new[] { 2, 8 });

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(60, e.Pitch.MidiNumber));
        Assert.Equal(2 * Hop, events[0].Onset.Samples);
        Assert.Equal(8 * Hop, events[1].Onset.Samples);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void SegmentationIsDeterministic()
    {
        var frames = new List<FrameObservation>
        {
            Silent(0), Silent(1),
            Voiced(2, 64), Voiced(3, 64), Voiced(4, 64),
            Silent(5),
            Voiced(6, 67), Voiced(7, 67), Voiced(8, 67),
            Silent(9),
        };
        var segmenter = MakeSegmenter();

        var first = segmenter.Segment(frames, new[] { 2, 6 });
        var second = segmenter.Segment(frames, new[] { 2, 6 });

        var projFirst = first.Select(e => (e.Pitch.MidiNumber, e.Onset.Samples, e.Duration.Samples, e.Velocity));
        var projSecond = second.Select(e => (e.Pitch.MidiNumber, e.Onset.Samples, e.Duration.Samples, e.Velocity));
        Assert.Equal(projFirst, projSecond);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void NoteEndsWhenLevelDecaysBelowFloor()
    {
        // Stays voiced on C4 throughout, but the level decays; floor = 25% of peak.
        var frames = new List<FrameObservation>
        {
            Silent(0), Silent(1),
            Voiced(2, 60, 1.0), Voiced(3, 60, 0.8), Voiced(4, 60, 0.5),
            Voiced(5, 60, 0.2),                        // 0.2 < 0.25 * peak(1.0) → note ends here
            Voiced(6, 60, 0.15), Voiced(7, 60, 0.1),
            Silent(8),
        };

        var events = MakeSegmenter(decayFloor: 0.25).Segment(frames, new[] { 2 });

        var e = Assert.Single(events);
        Assert.Equal(60, e.Pitch.MidiNumber);
        Assert.Equal((5 - 2) * Hop, e.Duration.Samples);
    }
}
