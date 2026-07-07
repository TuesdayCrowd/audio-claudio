namespace AudioClaudio.Domain;

/// <summary>
/// A duration as an integer count of samples at a declared <see cref="SampleRate"/>.
/// Integer time only — seconds are a display conversion at the edge (non-negotiable 1).
/// </summary>
public readonly record struct SampleDuration
{
    /// <summary>Number of samples. Always non-negative.</summary>
    public long Samples { get; }

    /// <summary>The rate these samples are counted at.</summary>
    public SampleRate Rate { get; }

    public SampleDuration(long samples, SampleRate rate)
    {
        if (samples < 0)
            throw new ArgumentOutOfRangeException(nameof(samples), samples, "Sample duration must be non-negative.");
        Samples = samples;
        Rate = rate;
    }

    /// <summary>Display conversion to seconds. Never used for domain arithmetic.</summary>
    public double ToSeconds() => (double)Samples / Rate.Hz;

    public static SampleDuration operator +(SampleDuration a, SampleDuration b)
    {
        RequireSameRate(a.Rate, b.Rate);
        return new SampleDuration(a.Samples + b.Samples, a.Rate);
    }

    /// <summary>
    /// The currency-mismatch guard (R1.3): differing rates throw, never coerce.
    /// Internal so <see cref="SamplePosition"/> shares one definition.
    /// </summary>
    internal static void RequireSameRate(SampleRate x, SampleRate y)
    {
        if (x != y)
            throw new InvalidOperationException(
                $"Sample-rate mismatch: {x.Hz} Hz vs {y.Hz} Hz. Rates must match; values are never coerced.");
    }
}
