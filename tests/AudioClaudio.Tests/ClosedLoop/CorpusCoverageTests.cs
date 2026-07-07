using System.Collections.Generic;
using System.Linq;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>
/// Pins the coverage claims of the two corpora (DECISIONS.md, "Step 9 — closed-loop corpus
/// constrained to physically-audible note durations"):
///   • the UNCAPPED <see cref="ClosedLoopGen.FullRangeCases"/> spans the whole keyboard (MIDI
///     33-96) — the basis for the count/pitch/onset coverage claim;
///   • the audible-capped <see cref="ClosedLoopGen.Cases"/> narrows ONLY duration, and only where
///     the SoundFont cannot physically sustain it — every note it emits is ≥ eighth AND within its
///     pitch/tempo audible cap. Generator-only (fast).
/// </summary>
public sealed class CorpusCoverageTests
{
    private static IEnumerable<int> AllPitches =>
        Enumerable.Range(ClosedLoopGen.MidiLow, ClosedLoopGen.MidiHigh - ClosedLoopGen.MidiLow + 1);

    [Trait("Category", "Fast")]
    [Fact]
    public void Full_range_corpus_covers_every_pitch_33_to_96()
    {
        var seen = new HashSet<int>();
        ClosedLoopGen.FullRangeCases.Sample(
            c =>
            {
                foreach (var e in c.Events)
                {
                    seen.Add(e.Pitch.MidiNumber);
                }
            },
            iter: 2000,
            threads: 1); // shared HashSet accumulation — Sample parallelizes by default

        var missing = AllPitches.Where(m => !seen.Contains(m)).ToList();
        Assert.True(missing.Count == 0, $"full-range corpus never generated: [{string.Join(",", missing)}]");
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void Capped_corpus_spans_the_audible_pitch_band_and_stops_where_audibility_does()
    {
        // Union of pitches the capped corpus can emit at ANY tempo in [60,140]. Pinned so a
        // SoundFont or (settle/ratio/margin) change surfaces here for review. The band starts at
        // the lowest key (A1) and stops at MIDI 71 (B4): above that, no pitch sustains an audible
        // eighth even at the fastest tempo — those pitches are the full-range corpus's job.
        var union = new SortedSet<int>();
        for (int bpm = 60; bpm <= 140; bpm++)
        {
            foreach (var p in ClosedLoopGen.ValidPitches(bpm))
            {
                union.Add(p);
            }
        }

        Assert.Equal(ClosedLoopGen.MidiLow, union.Min);
        Assert.Equal(71, union.Max);
        Assert.True(union.Count < AllPitches.Count(), "the cap must actually exclude some (pitch,tempo) combos");

        // MIDI 72 (C5) and up cannot hold an audible eighth at any tempo in range.
        for (int m = 72; m <= ClosedLoopGen.MidiHigh; m++)
        {
            Assert.True(ClosedLoopGen.MaxDurationSub(m, 140) < 2, $"MIDI {m} unexpectedly holds an eighth at 140 BPM");
        }
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void Every_capped_note_is_at_least_an_eighth_and_within_its_audible_cap()
    {
        ClosedLoopGen.Cases.Sample(
            c =>
            {
                double sps = 60.0 / c.TempoBpm * c.Rate.Hz / ClosedLoopGen.SubdivisionsPerBeat;
                foreach (var e in c.Events)
                {
                    int durSub = (int)System.Math.Round(e.Duration.Samples / sps, System.MidpointRounding.AwayFromZero);
                    Assert.True(durSub >= 2, $"MIDI {e.Pitch.MidiNumber} dur {durSub} < eighth");
                    int cap = ClosedLoopGen.MaxDurationSub(e.Pitch.MidiNumber, c.TempoBpm);
                    Assert.True(durSub <= cap, $"MIDI {e.Pitch.MidiNumber} dur {durSub} > audible cap {cap} at {c.TempoBpm} BPM");
                    Assert.InRange(e.Pitch.MidiNumber, ClosedLoopGen.MidiLow, 71);
                }
            },
            iter: 1000);
    }
}
