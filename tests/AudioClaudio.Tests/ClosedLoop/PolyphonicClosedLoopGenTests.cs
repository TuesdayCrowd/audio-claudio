using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>
/// The polyphonic closed-loop generator: random <b>chord</b> sequences (notes sharing an onset),
/// well-separated and in a range this SoundFont can actually sustain, so the closed loop measures the
/// engine's intrinsic fidelity rather than the SoundFont's decay. These pin the structural invariants
/// the gate relies on; the gate itself (synthesize → transcribe → F1) is a Slow test.
/// </summary>
public class PolyphonicClosedLoopGenTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Build_makes_chord_notes_that_share_one_onset()
    {
        IReadOnlyList<NoteEvent> notes = PolyphonicClosedLoopGen.Build(
            bpm: 120,
            chordPitches: new[] { new[] { 60, 64, 67 }, new[] { 62, 65 } },
            durs: new[] { 4, 4 },
            gaps: new[] { 2, 2 });

        // Chord 0 = three notes at the same onset.
        var chord0 = notes.Where(n => new[] { 60, 64, 67 }.Contains(n.Pitch.MidiNumber)).ToList();
        Assert.Equal(3, chord0.Count);
        Assert.Single(chord0.Select(n => n.Onset.Samples).Distinct()); // one shared onset

        // Chord 1 starts strictly after chord 0 ends (a real gap between onsets).
        long chord0End = chord0[0].Onset.Samples + chord0[0].Duration.Samples;
        long chord1Onset = notes.First(n => n.Pitch.MidiNumber == 62).Onset.Samples;
        Assert.True(chord1Onset >= chord0End, "chords must be separated by the rest gap");
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Cases_are_in_the_sustainable_range_distinct_per_chord_and_polyphonic()
    {
        var cases = PolyphonicClosedLoopGen.Cases(count: 6).ToList();
        Assert.Equal(6, cases.Count);

        var allNotes = cases.SelectMany(c => c).ToList();
        Assert.All(allNotes, n => Assert.InRange(n.Pitch.MidiNumber, PolyphonicClosedLoopGen.MidiLow, PolyphonicClosedLoopGen.MidiCeiling));
        Assert.All(allNotes, n => Assert.Equal(PolyphonicClosedLoopGen.Velocity, n.Velocity));

        // At least one real chord (≥2 notes sharing an onset) somewhere in the corpus.
        // Grouped WITHIN each case — every case starts at onset 0, so cross-case grouping would conflate chords.
        bool anyChord = cases.Any(c => c.GroupBy(n => n.Onset.Samples).Any(g => g.Count() >= 2));
        Assert.True(anyChord, "a polyphonic corpus must contain at least one multi-note chord");

        // Within a single chord (one case, one onset), pitches are distinct (no duplicate note in a chord).
        foreach (var c in cases)
        {
            foreach (var chord in c.GroupBy(n => n.Onset.Samples))
            {
                var pitches = chord.Select(n => n.Pitch.MidiNumber).ToList();
                Assert.Equal(pitches.Count, pitches.Distinct().Count());
            }
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Cases_are_deterministic_for_a_given_seed()
    {
        static IEnumerable<(int, long)> Flatten(IEnumerable<IReadOnlyList<NoteEvent>> cases) =>
            cases.SelectMany(c => c.Select(n => (n.Pitch.MidiNumber, n.Onset.Samples)));

        Assert.Equal(
            Flatten(PolyphonicClosedLoopGen.Cases(5, seed: 99)),
            Flatten(PolyphonicClosedLoopGen.Cases(5, seed: 99)));
    }
}
