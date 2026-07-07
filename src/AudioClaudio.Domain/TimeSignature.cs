namespace AudioClaudio.Domain;

/// <summary>
/// A time signature such as 4/4. The MVP uses 4/4 only; the type is written a
/// little more generally (any positive numerator, any power-of-two denominator)
/// so the grid math has a single honest source for beats-per-measure.
/// </summary>
public readonly record struct TimeSignature
{
    /// <summary>Numerator — beats in one measure.</summary>
    public int BeatsPerMeasure { get; }

    /// <summary>Denominator — the note value that gets one beat (4 = quarter).</summary>
    public int BeatUnit { get; }

    public TimeSignature(int beatsPerMeasure, int beatUnit)
    {
        if (beatsPerMeasure <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(beatsPerMeasure), beatsPerMeasure, "Numerator (beats per measure) must be positive.");
        }

        if (beatUnit <= 0 || (beatUnit & (beatUnit - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(beatUnit), beatUnit, "Denominator (beat unit) must be a positive power of two.");
        }

        BeatsPerMeasure = beatsPerMeasure;
        BeatUnit = beatUnit;
    }

    /// <summary>The MVP time signature, 4/4.</summary>
    public static TimeSignature FourFour => new(4, 4);
}
