namespace AudioClaudio.Domain;

/// <summary>
/// One analysis window: mono PCM samples (float, nominally in [-1, 1]) together with
/// the <see cref="SamplePosition"/> of the window's first sample. The sample rate is
/// carried by <see cref="Start"/> (a position without its rate is a bug), so
/// <see cref="Rate"/> is derived rather than duplicated.
/// </summary>
public sealed class Frame
{
    /// <summary>The window's samples. Treat as read-only; downstream DSP reads it in place.</summary>
    public float[] Samples { get; }

    /// <summary>Position (in samples, at <see cref="Rate"/>) of this frame's first sample.</summary>
    public SamplePosition Start { get; }

    /// <summary>The declared sample rate, taken from <see cref="Start"/>.</summary>
    public SampleRate Rate => Start.Rate;

    public Frame(float[] samples, SamplePosition start)
    {
        Samples = samples ?? throw new ArgumentNullException(nameof(samples));
        Start = start;
    }
}
