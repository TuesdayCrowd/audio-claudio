namespace AudioClaudio.Domain;

/// <summary>
/// A position in an audio stream as an integer count of samples from the start,
/// at a declared <see cref="SampleRate"/>. A position without its rate is a bug
/// (non-negotiable 1).
/// </summary>
public readonly record struct SamplePosition
{
    /// <summary>Samples from the start of the stream. Always non-negative.</summary>
    public long Samples { get; }

    /// <summary>The rate these samples are counted at.</summary>
    public SampleRate Rate { get; }

    public SamplePosition(long samples, SampleRate rate)
    {
        if (samples < 0)
            throw new ArgumentOutOfRangeException(nameof(samples), samples, "Sample position must be non-negative.");
        Samples = samples;
        Rate = rate;
    }

    /// <summary>Display conversion to seconds. Never used for domain arithmetic.</summary>
    public double ToSeconds() => (double)Samples / Rate.Hz;

    /// <summary>Advance a position by a duration. Rates must match.</summary>
    public static SamplePosition operator +(SamplePosition p, SampleDuration d)
    {
        SampleDuration.RequireSameRate(p.Rate, d.Rate);
        return new SamplePosition(p.Samples + d.Samples, p.Rate);
    }

    /// <summary>
    /// Elapsed duration between two positions. Rates must match; an earlier-minus-later
    /// result is negative and rejected by <see cref="SampleDuration"/>'s constructor.
    /// </summary>
    public static SampleDuration operator -(SamplePosition a, SamplePosition b)
    {
        SampleDuration.RequireSameRate(a.Rate, b.Rate);
        return new SampleDuration(a.Samples - b.Samples, a.Rate);
    }
}
