using AudioClaudio.Domain;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class PitchTests
{
    // R1.1 — the three anchors from Section 6 Step 1 "Verify".
    [Fact]
    [Trait("Category", "Fast")]
    public void Frequency_MatchesKnownAnchors()
    {
        Assert.True(System.Math.Abs(new Pitch(69).Frequency() - 440.0) <= 0.5, "A4 = 440 Hz");
        Assert.True(System.Math.Abs(new Pitch(21).Frequency() - 27.5) <= 0.5, "A0 = 27.5 Hz");
        Assert.True(System.Math.Abs(new Pitch(108).Frequency() - 4186.0) <= 0.5, "C8 ≈ 4186 Hz");
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(20)]   // below A0
    [InlineData(109)]  // above C8
    [InlineData(0)]
    public void Constructor_RejectsOutOfRangeMidi(int midi)
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new Pitch(midi));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Constructor_AcceptsFullPianoRange()
    {
        var low = new Pitch(Pitch.MinMidi);
        var high = new Pitch(Pitch.MaxMidi);
        Assert.Equal(21, low.MidiNumber);
        Assert.Equal(108, high.MidiNumber);
    }

    // R1.1 — the headline round-trip, exhaustive over all 88 pitches (deterministic).
    [Fact]
    [Trait("Category", "Fast")]
    public void FromFrequency_RoundTripsEvery88Pitches()
    {
        for (int midi = Pitch.MinMidi; midi <= Pitch.MaxMidi; midi++)
        {
            var p = new Pitch(midi);
            var back = Pitch.FromFrequency(p.Frequency());
            Assert.Equal(p, back);
        }
    }

    // R1.1 — nearest-note mapping is correct for any frequency within ±49 cents of a piano pitch.
    [Fact]
    [Trait("Category", "Fast")]
    public void FromFrequency_MapsToNearestNoteWithin49Cents()
    {
        // For every pitch, and any detuning strictly inside ±49 cents, FromFrequency
        // must return that same pitch. 49 (not 50) stays clear of the exact tie.
        CsCheck.Gen.Select(
                CsCheck.Gen.Int[Pitch.MinMidi, Pitch.MaxMidi],
                CsCheck.Gen.Double[-49.0, 49.0])
            .Sample((midi, cents) =>
            {
                double f0 = new Pitch(midi).Frequency();
                double detuned = f0 * System.Math.Pow(2.0, cents / 1200.0);
                return Pitch.FromFrequency(detuned).MidiNumber == midi;
            }, iter: 10_000, seed: "0N0XvlID3sJ2");
        // The seed is pinned up front — the Foundation convention is "Fix CsCheck seeds
        // for reproducibility," so every CI run explores the same 10 000 cases bit-for-bit
        // rather than seeding a fresh sequence each time. If CsCheck ever reports a
        // counterexample it prints a replacement `seed:` string; paste it in to reproduce,
        // per @superpowers:systematic-debugging. (Any CsCheck-generated seed literal is valid.)
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(0.0)]
    [InlineData(-100.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void FromFrequency_RejectsNonPositiveOrNonFinite(double hz)
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => Pitch.FromFrequency(hz));
    }

    // R1.1 / non-negotiable 3 — the tie-break the Approach singles out as load-bearing.
    // A frequency exactly ±50 cents from a pitch lands on the midpoint between two notes,
    // and MidpointRounding.AwayFromZero resolves it deterministically toward the larger
    // magnitude — i.e. upward, since MIDI numbers are positive. So +50 cents from note n is
    // the midpoint of (n, n+1) and rounds to n+1; −50 cents from note n is the midpoint of
    // (n−1, n) and rounds to n. This exact-tie behavior is what makes the mapping reproducible
    // on every machine; the ±49-cent property above deliberately stays clear of the tie, so
    // the tie itself is pinned here (otherwise the one behavior the prose calls load-bearing
    // for determinism would never be asserted).
    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(69, +50.0, 70)]  // A4 sharp by a half-semitone → A♯4 (midpoint 69↔70 rounds up)
    [InlineData(69, -50.0, 69)]  // A4 flat  by a half-semitone → still A4 (midpoint 68↔69 rounds up)
    [InlineData(60, +50.0, 61)]  // middle C sharp by a half-semitone → C♯4
    [InlineData(60, -50.0, 60)]  // middle C flat  by a half-semitone → still C4
    public void FromFrequency_MidpointTie_RoundsAwayFromZero(int midi, double cents, int expectedMidi)
    {
        double detuned = new Pitch(midi).Frequency() * System.Math.Pow(2.0, cents / 1200.0);
        Assert.Equal(expectedMidi, Pitch.FromFrequency(detuned).MidiNumber);
    }
}
