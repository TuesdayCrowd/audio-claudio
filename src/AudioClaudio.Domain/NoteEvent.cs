namespace AudioClaudio.Domain;

/// <summary>
/// A single detected/quantized note: a pitch sounding from an onset for a duration,
/// at a given MIDI velocity. Immutable and value-equal. The unit the whole pipeline
/// exchanges once audio has become notes.
/// </summary>
public readonly record struct NoteEvent
{
    /// <summary>Minimum MIDI velocity.</summary>
    public const int MinVelocity = 0;

    /// <summary>Maximum MIDI velocity.</summary>
    public const int MaxVelocity = 127;

    /// <summary>Constant velocity the MVP may emit when it does not estimate dynamics (R1.4).</summary>
    public const int DefaultVelocity = 64;

    public Pitch Pitch { get; }
    public SamplePosition Onset { get; }
    public SampleDuration Duration { get; }

    /// <summary>MIDI velocity in 0..127.</summary>
    public int Velocity { get; }

    public NoteEvent(Pitch pitch, SamplePosition onset, SampleDuration duration, int velocity = DefaultVelocity)
    {
        if (velocity < MinVelocity || velocity > MaxVelocity)
            throw new ArgumentOutOfRangeException(
                nameof(velocity), velocity, $"Velocity must be in {MinVelocity}..{MaxVelocity}.");
        if (onset.Rate != duration.Rate)
            throw new InvalidOperationException(
                $"Sample-rate mismatch between onset ({onset.Rate.Hz} Hz) and duration ({duration.Rate.Hz} Hz).");
        Pitch = pitch;
        Onset = onset;
        Duration = duration;
        Velocity = velocity;
    }
}
