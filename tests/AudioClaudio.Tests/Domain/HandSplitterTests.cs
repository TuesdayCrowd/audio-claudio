using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Polyphony;
using AudioClaudio.Tests.Notation;
using Xunit;
using Xunit.Abstractions;

namespace AudioClaudio.Tests.Domain;

/// <summary>
/// v2 Stage 3c — temporal treble/bass hand-tracking. Scored against the fixed middle-C split on a
/// continuous-crossing corpus (<see cref="HandCrossingGen"/>), plus chord-split and determinism units.
/// </summary>
public class HandSplitterTests
{
    private readonly ITestOutputHelper _out;

    public HandSplitterTests(ITestOutputHelper output) => _out = output;

    private static readonly SampleRate Rate = new(HandCrossingGen.SampleRateHz);

    private static Chord ChordOf(long onset, params int[] midis) =>
        new(new SamplePosition(onset, Rate), midis.OrderBy(m => m).Select(m => new Pitch(m)).ToList(),
            new SampleDuration(1000, Rate), 80);

    [Fact]
    [Trait("Category", "Fast")]
    public void StraddlingChord_SplitsLowToBass_HighToTreble()
    {
        var splitter = new HandSplitter();
        (Chord? treble, Chord? bass) = splitter.SplitNext(ChordOf(0, 48, 52, 67, 72));

        Assert.NotNull(treble);
        Assert.NotNull(bass);
        Assert.Equal(new[] { 48, 52 }, bass!.Pitches.Select(p => p.MidiNumber));
        Assert.Equal(new[] { 67, 72 }, treble!.Pitches.Select(p => p.MidiNumber));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void AContinuousCrossing_KeepsTheLineWithItsHand_WhereMiddleCWouldNot()
    {
        // The left hand walks up across middle C: 55, 58, 61, 63 — the last two sit ABOVE the middle-C
        // line but continue the left line. The right hand stays high. A tracker follows the left line up.
        var splitter = new HandSplitter();
        int[] leftLine = { 55, 58, 61, 63 };
        int[] rightLine = { 72, 73, 74, 74 };
        for (int i = 0; i < leftLine.Length; i++)
        {
            (Chord? _, Chord? bass) = splitter.SplitNext(ChordOf(i * 1000, leftLine[i], rightLine[i]));
            Assert.NotNull(bass);
            Assert.Equal(leftLine[i], bass!.Pitches.Single().MidiNumber); // the left note stays in the bass
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void IsDeterministic()
    {
        var chords = new[] { ChordOf(0, 50, 70), ChordOf(1000, 61, 72), ChordOf(2000, 48, 69) };
        var a = HandSplitter.Split(chords);
        var b = HandSplitter.Split(chords);
        Assert.Equal(a.Select(x => x.Bass?.Pitches.Count ?? -1), b.Select(x => x.Bass?.Pitches.Count ?? -1));
        Assert.Equal(a.Select(x => x.Treble?.Pitches.Count ?? -1), b.Select(x => x.Treble?.Pitches.Count ?? -1));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Target_HandAccuracy_OnCrossings_BeatsTheMiddleCBaseline()
    {
        var corpus = HandCrossingGen.Cases(40).ToList();
        double baseline = NotationMetrics.HandAccuracy(corpus, NotationMetrics.MiddleCSplit);
        double tracked = NotationMetrics.HandAccuracy(corpus, TrackedHands);
        _out.WriteLine($"hand accuracy — middle-C: {baseline:P1}, temporal tracker: {tracked:P1}");

        // The tracker must clear a high bar AND clearly beat the fixed cut on continuous crossings.
        Assert.True(tracked >= 0.97, $"tracked hand accuracy {tracked:P1} below the 97% target");
        Assert.True(tracked - baseline >= 0.05, $"tracker beat the middle-C baseline by only {(tracked - baseline):P1}");
    }

    // Adapt HandSplitter to the metric's per-event delegate: group events by onset into chords, split them
    // in onset order, and label each event by which fragment (treble→Right, bass→Left) holds its pitch.
    private static IReadOnlyList<Hand> TrackedHands(IReadOnlyList<NoteEvent> events)
    {
        var byOnset = new SortedDictionary<long, List<int>>();
        foreach (NoteEvent e in events)
        {
            if (!byOnset.TryGetValue(e.Onset.Samples, out List<int>? pitches))
            {
                byOnset[e.Onset.Samples] = pitches = new List<int>();
            }

            pitches.Add(e.Pitch.MidiNumber);
        }

        var chords = byOnset
            .Select(kv => new Chord(
                new SamplePosition(kv.Key, Rate), kv.Value.OrderBy(m => m).Select(m => new Pitch(m)).ToList(),
                new SampleDuration(1000, Rate), 80))
            .ToList();
        IReadOnlyList<(Chord? Treble, Chord? Bass)> splits = HandSplitter.Split(chords);

        var handOf = new Dictionary<(long, int), Hand>();
        for (int i = 0; i < chords.Count; i++)
        {
            long onset = chords[i].Onset.Samples;
            foreach (Pitch p in splits[i].Treble?.Pitches ?? Enumerable.Empty<Pitch>())
            {
                handOf[(onset, p.MidiNumber)] = Hand.Right;
            }

            foreach (Pitch p in splits[i].Bass?.Pitches ?? Enumerable.Empty<Pitch>())
            {
                handOf[(onset, p.MidiNumber)] = Hand.Left;
            }
        }

        return events.Select(e => handOf[(e.Onset.Samples, e.Pitch.MidiNumber)]).ToList();
    }
}
