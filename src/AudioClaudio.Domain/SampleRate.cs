namespace AudioClaudio.Domain;

/// <summary>
/// A sampling rate in Hz (e.g. 44100). The unit that a <see cref="SamplePosition"/>
/// or <see cref="SampleDuration"/> is denominated in — a sample count without its
/// rate is a bug (non-negotiable 1).
/// </summary>
public readonly record struct SampleRate
{
    /// <summary>Samples per second. Always positive.</summary>
    public int Hz { get; }

    public SampleRate(int hz)
    {
        if (hz <= 0)
            throw new ArgumentOutOfRangeException(nameof(hz), hz, "Sample rate must be positive.");
        Hz = hz;
    }

    public override string ToString() => $"{Hz} Hz";
}
