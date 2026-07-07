using AudioClaudio.Domain;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class PitchMathTests
{
    // R1.2 — known intervals: an octave is 1200 cents, a semitone is 100.
    [Fact]
    [Trait("Category", "Fast")]
    public void CentsBetween_MatchesKnownIntervals()
    {
        Assert.True(System.Math.Abs(PitchMath.CentsBetween(440.0, 880.0) - 1200.0) < 1e-9, "octave up");
        Assert.True(System.Math.Abs(PitchMath.CentsBetween(880.0, 440.0) + 1200.0) < 1e-9, "octave down");
        double semitone = PitchMath.CentsBetween(new Pitch(69).Frequency(), new Pitch(70).Frequency());
        Assert.True(System.Math.Abs(semitone - 100.0) < 1e-9, "one semitone = 100 cents");
    }

    // R1.2 — distance from a frequency to itself is exactly zero.
    [Fact]
    [Trait("Category", "Fast")]
    public void CentsBetween_SameFrequencyIsExactlyZero()
    {
        Assert.Equal(0.0, PitchMath.CentsBetween(261.63, 261.63));
        Assert.Equal(0.0, PitchMath.CentsBetween(27.5, 27.5));
    }

    // R1.2 — antisymmetry: cents(f1,f2) == -cents(f2,f1) across the piano range.
    [Fact]
    [Trait("Category", "Fast")]
    public void CentsBetween_IsAntisymmetric()
    {
        Gen.Select(Gen.Double[20.0, 5000.0], Gen.Double[20.0, 5000.0])
            .Sample((f1, f2) =>
            {
                double forward = PitchMath.CentsBetween(f1, f2);
                double backward = PitchMath.CentsBetween(f2, f1);
                return System.Math.Abs(forward + backward) < 1e-7;
            }, iter: 10_000, seed: "0N0XvlID3sJ2");
        // Seed pinned up front for reproducible CI (Foundation: "Fix CsCheck seeds for
        // reproducibility"); replace with any CsCheck-reported seed to reproduce a failure.
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(0.0, 440.0)]
    [InlineData(440.0, 0.0)]
    [InlineData(-1.0, 440.0)]
    [InlineData(440.0, double.NaN)]
    public void CentsBetween_RejectsNonPositiveFrequencies(double f1, double f2)
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => PitchMath.CentsBetween(f1, f2));
    }
}
