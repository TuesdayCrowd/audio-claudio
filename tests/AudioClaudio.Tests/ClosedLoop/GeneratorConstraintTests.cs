using System;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.ClosedLoop;

public sealed class GeneratorConstraintTests
{
    [Trait("Category", "Fast")]
    [Fact]
    public void Generated_cases_satisfy_the_R9_1_constraints()
    {
        ClosedLoopGen.Cases.Sample(c =>
        {
            Assert.InRange(c.TempoBpm, 60, 140);
            Assert.True(c.Events.Count >= 3, "at least three notes");

            double sps = 60.0 / c.TempoBpm * c.Rate.Hz / ClosedLoopGen.SubdivisionsPerBeat;
            long prevEnd = -1;
            int prevEndSub = -1;
            foreach (var e in c.Events)
            {
                Assert.InRange(e.Pitch.MidiNumber, 33, 96); // MIDI 33..96
                Assert.Equal(ClosedLoopGen.Velocity, e.Velocity);

                // Re-derivation uses AwayFromZero throughout, matching ClosedLoopGen.Build's own
                // rounding convention (and the codebase-wide one: QuantizationGrid.SamplesToTick,
                // Pitch.FromFrequency). The CLR's Math.Round default is ToEven (banker's rounding),
                // which disagrees with AwayFromZero exactly on a half-sample grid (e.g. bpm=72 gives
                // sps=9187.5) — using the default here was a latent test-side bug, not a generator bug.
                int durSub = (int)Math.Round(e.Duration.Samples / sps, MidpointRounding.AwayFromZero);
                Assert.True(durSub >= 2, $"duration {durSub} sub < eighth"); // notes >= eighth

                // Audible-duration cap (Cornelius's decision, DECISIONS.md): every declared
                // duration stays within the pitch's audible window at this tempo. This is the ONE
                // R9.1 refinement — it never relaxes anything (min eighth, MIDI range, rest, tempo
                // all still hold above); it only forbids declaring a note longer than the SoundFont
                // physically sustains it.
                int maxDurSub = ClosedLoopGen.MaxDurationSub(e.Pitch.MidiNumber, c.TempoBpm);
                Assert.True(durSub <= maxDurSub,
                    $"duration {durSub} sub exceeds audible cap {maxDurSub} for MIDI {e.Pitch.MidiNumber} at {c.TempoBpm} BPM");

                Assert.True(e.Onset.Samples >= prevEnd, "notes overlap (not monophonic)");

                int onsetSub = (int)Math.Round(e.Onset.Samples / sps, MidpointRounding.AwayFromZero);
                if (prevEndSub >= 0)
                {
                    Assert.True(onsetSub - prevEndSub >= 1, "no grid rest between notes");
                }

                // grid-exact: onset is exactly the rounded grid position
                Assert.Equal((long)Math.Round(onsetSub * sps, MidpointRounding.AwayFromZero), e.Onset.Samples);

                prevEnd = e.Onset.Samples + e.Duration.Samples;
                prevEndSub = onsetSub + durSub;
            }
        }, iter: 500);
    }
}
