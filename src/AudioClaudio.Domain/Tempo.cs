namespace AudioClaudio.Domain;

/// <summary>
/// Musical tempo in beats per minute. In 4/4 the beat is a quarter note.
/// A positive, finite BPM; the value type carries no clock and no I/O (R1.5, R6.5-style purity).
/// </summary>
public readonly record struct Tempo
{
    /// <summary>Beats (quarter notes in 4/4) per minute.</summary>
    public double BeatsPerMinute { get; }

    public Tempo(double beatsPerMinute)
    {
        if (!(beatsPerMinute > 0) || double.IsInfinity(beatsPerMinute))
        {
            throw new ArgumentOutOfRangeException(
                nameof(beatsPerMinute), beatsPerMinute, "Tempo must be a positive, finite BPM.");
        }

        BeatsPerMinute = beatsPerMinute;
    }
}
