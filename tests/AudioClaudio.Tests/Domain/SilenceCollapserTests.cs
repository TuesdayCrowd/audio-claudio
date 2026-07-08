using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class SilenceCollapserTests
{
    private static readonly SampleRate Rate = new(44100);

    [Fact]
    [Trait("Category", "Fast")]
    public void Collapse_ShrinksLongGapsInBothAudioAndNotes_KeepingThemAligned()
    {
        var threshold = new SampleDuration(100, Rate);
        var audio = new float[1000];
        for (int i = 0; i < audio.Length; i++) audio[i] = i;

        var notes = new[]
        {
            new NoteEvent(new Pitch(60), new SamplePosition(0, Rate), new SampleDuration(50, Rate)),
            new NoteEvent(new Pitch(62), new SamplePosition(400, Rate), new SampleDuration(50, Rate)),
            new NoteEvent(new Pitch(64), new SamplePosition(500, Rate), new SampleDuration(50, Rate)),
        };

        SilenceCollapser.Result collapsed = SilenceCollapser.Collapse(notes, audio, Rate, threshold, fadeSamples: 0);

        Assert.Equal(400, collapsed.Audio.Length);
        Assert.Equal(3, collapsed.Notes.Count);
        Assert.Equal(new long[] { 0, 150, 250 }, new[]
        {
            collapsed.Notes[0].Onset.Samples, collapsed.Notes[1].Onset.Samples, collapsed.Notes[2].Onset.Samples,
        });
        Assert.All(collapsed.Notes, n => Assert.Equal(50, n.Duration.Samples));
        Assert.Equal(new[] { 60, 62, 64 }, new[]
        {
            collapsed.Notes[0].Pitch.MidiNumber, collapsed.Notes[1].Pitch.MidiNumber, collapsed.Notes[2].Pitch.MidiNumber,
        });

        // Alignment: the retimed onsets still index the ORIGINAL sample values in the collapsed audio.
        Assert.Equal(149f, collapsed.Audio[149]);
        Assert.Equal(400f, collapsed.Audio[150]);
        Assert.Equal(649f, collapsed.Audio[399]);
        Assert.Equal(500f, collapsed.Audio[(int)collapsed.Notes[2].Onset.Samples]);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Collapse_LeavesGapsWithinThresholdUntouched()
    {
        var threshold = new SampleDuration(100, Rate);
        var audio = new float[200];
        for (int i = 0; i < audio.Length; i++) audio[i] = i;

        // Gap between notes is 30 (<= 100); trailing gap after B ends (at 130) is 70 (<= 100).
        var notes = new[]
        {
            new NoteEvent(new Pitch(60), new SamplePosition(0, Rate), new SampleDuration(50, Rate)),
            new NoteEvent(new Pitch(62), new SamplePosition(80, Rate), new SampleDuration(50, Rate)),
        };

        SilenceCollapser.Result collapsed = SilenceCollapser.Collapse(notes, audio, Rate, threshold, fadeSamples: 0);

        Assert.Equal(audio, collapsed.Audio);
        Assert.Equal(new long[] { 0, 80 }, new[] { collapsed.Notes[0].Onset.Samples, collapsed.Notes[1].Onset.Samples });
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Collapse_ShrinksLeadingSilenceToTheThreshold()
    {
        var threshold = new SampleDuration(100, Rate);
        var audio = new float[600];
        for (int i = 0; i < audio.Length; i++) audio[i] = i;

        // Trailing gap after the note ends (at 550) is 50 (<= 100): no trailing cut.
        var notes = new[]
        {
            new NoteEvent(new Pitch(60), new SamplePosition(500, Rate), new SampleDuration(50, Rate)),
        };

        SilenceCollapser.Result collapsed = SilenceCollapser.Collapse(notes, audio, Rate, threshold, fadeSamples: 0);

        Assert.Equal(200, collapsed.Audio.Length);
        Assert.Equal(100, collapsed.Notes[0].Onset.Samples);
        Assert.Equal(500f, collapsed.Audio[100]);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Collapse_WithNoNotes_TrimsAllSilenceAudioToTheThreshold()
    {
        var threshold = new SampleDuration(100, Rate);
        var audio = new float[1000];

        SilenceCollapser.Result collapsed = SilenceCollapser.Collapse(
            Array.Empty<NoteEvent>(), audio, Rate, threshold, fadeSamples: 0);

        Assert.Empty(collapsed.Notes);
        Assert.Equal(100, collapsed.Audio.Length);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Collapse_FadesTheTailOfEachKeptSegmentBeforeASplice()
    {
        var threshold = new SampleDuration(100, Rate);
        var audio = new float[400];
        for (int i = 0; i < audio.Length; i++) audio[i] = 1.0f;

        // Two notes with a single gap between them that exceeds the threshold, and a trailing gap
        // that does not, so `cuts` has exactly one entry -- an INTERIOR cut, not a trailing one:
        //   gap = onset(300) - prevEnd(50) = 250 > threshold(100)
        //     => cuts = [(Start: 50 + 100 = 150, Count: 250 - 100 = 150)]
        //   prevEnd after note B = 300 + 50 = 350; trailing = audio.Length(400) - 350 = 50 <= threshold
        //     => no trailing cut.
        // RemoveSpans keeps audio[0, 150) (the `keep = 150 - 0 = 150` samples before the cut) at
        // result[0, 150), so the splice lands at result index 150; FadeOutTail ramps the segment's
        // last 4 samples, result[146..149], from 1 down to 0.
        var notes = new[]
        {
            new NoteEvent(new Pitch(60), new SamplePosition(0, Rate), new SampleDuration(50, Rate)),
            new NoteEvent(new Pitch(62), new SamplePosition(300, Rate), new SampleDuration(50, Rate)),
        };

        SilenceCollapser.Result collapsed = SilenceCollapser.Collapse(notes, audio, Rate, threshold, fadeSamples: 4);

        Assert.Equal(250, collapsed.Audio.Length);
        Assert.Equal(1.0f, collapsed.Audio[50]); // well before the splice: unfaded
        Assert.Equal(0.75f, collapsed.Audio[146]);
        Assert.Equal(0.5f, collapsed.Audio[147]);
        Assert.Equal(0.25f, collapsed.Audio[148]);
        Assert.Equal(0f, collapsed.Audio[149]);
    }
}
