namespace AudioClaudio.Domain;

/// <summary>Pure pitch/frequency helpers. No state, no I/O, no clock.</summary>
public static class PitchMath
{
    /// <summary>
    /// Perceptual distance in cents (1/100 of a semitone): 1200 · log2(f2 / f1).
    /// Positive when <paramref name="f2"/> is higher than <paramref name="f1"/>.
    /// </summary>
    /// <param name="f1">Reference frequency in Hz (must be positive and finite).</param>
    /// <param name="f2">Target frequency in Hz (must be positive and finite).</param>
    public static double CentsBetween(double f1, double f2)
    {
        RequirePositiveFinite(f1, nameof(f1));
        RequirePositiveFinite(f2, nameof(f2));
        return 1200.0 * Math.Log2(f2 / f1);
    }

    private static void RequirePositiveFinite(double f, string name)
    {
        if (f <= 0.0 || double.IsNaN(f) || double.IsInfinity(f))
            throw new ArgumentOutOfRangeException(name, f, "Frequency must be a positive, finite value.");
    }
}
