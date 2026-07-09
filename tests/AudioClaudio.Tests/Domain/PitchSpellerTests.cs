using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

/// <summary>
/// Key-signature-aware enharmonic spelling (Stage 4c). A MIDI number is 12-fold ambiguous in
/// notation — MIDI 61 is C♯ in D major but D♭ in A♭ major — so a bare pitch class cannot be
/// engraved correctly without a key. <see cref="PitchSpeller"/> resolves it by the line-of-fifths
/// "nearest to the key centre" rule: diatonic notes spell naturally, chromatics spell in the key's
/// accidental direction. These tests pin the flat-key (A♭) and sharp-key (D) cases plus the
/// round-trip invariant that a spelling always reconstructs its MIDI number.
/// </summary>
public class PitchSpellerTests
{
    // Natural semitone of a step letter (C=0 … B=11) — the anchor for the round-trip check.
    private static int NaturalSemitone(string step) => step switch
    {
        "C" => 0,
        "D" => 2,
        "E" => 4,
        "F" => 5,
        "G" => 7,
        "A" => 9,
        "B" => 11,
        _ => throw new System.ArgumentException($"bad step {step}"),
    };

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(61, "D", -1, 4)] // D♭4, not C♯
    [InlineData(63, "E", -1, 4)] // E♭4
    [InlineData(66, "G", -1, 4)] // G♭4
    [InlineData(68, "A", -1, 4)] // A♭4
    [InlineData(70, "B", -1, 4)] // B♭4
    [InlineData(60, "C", 0, 4)]  // naturals unaffected
    [InlineData(64, "E", 0, 4)]
    [InlineData(69, "A", 0, 4)]
    public void A_flat_major_spells_chromatics_as_flats(int midi, string step, int alter, int octave)
    {
        (string Step, int Alter, int Octave) s = PitchSpeller.Spell(midi, fifths: -4);
        Assert.Equal((step, alter, octave), s);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(61, "C", 1, 4)] // C♯4, not D♭
    [InlineData(66, "F", 1, 4)] // F♯4
    [InlineData(68, "G", 1, 4)] // G♯4
    [InlineData(62, "D", 0, 4)] // naturals unaffected
    public void D_major_spells_chromatics_as_sharps(int midi, string step, int alter, int octave)
    {
        (string Step, int Alter, int Octave) s = PitchSpeller.Spell(midi, fifths: 2);
        Assert.Equal((step, alter, octave), s);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void C_major_uses_sharps_for_chromatics_by_convention()
    {
        Assert.Equal(("F", 1, 4), PitchSpeller.Spell(66, fifths: 0)); // F♯, not G♭
        Assert.Equal(("C", 1, 4), PitchSpeller.Spell(61, fifths: 0)); // C♯, not D♭
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Every_spelling_reconstructs_its_midi_number_across_all_keys()
    {
        for (int fifths = -7; fifths <= 7; fifths++)
        {
            for (int midi = Pitch.MinMidi; midi <= Pitch.MaxMidi; midi++)
            {
                (string Step, int Alter, int Octave) s = PitchSpeller.Spell(midi, fifths);
                int reconstructed = (12 * (s.Octave + 1)) + NaturalSemitone(s.Step) + s.Alter;
                Assert.Equal(midi, reconstructed);
            }
        }
    }
}
