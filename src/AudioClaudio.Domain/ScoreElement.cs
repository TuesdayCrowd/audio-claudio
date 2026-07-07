namespace AudioClaudio.Domain;

/// <summary>Whether a <see cref="ScoreElement"/> is a sounding note or a rest.</summary>
public enum ElementKind
{
    Note,
    Rest,
}

/// <summary>
/// One note or rest within a measure, its length measured in grid ticks.
///
/// A note that crosses a barline is stored as several elements whose lengths sum
/// to the note's quantized duration; every element except the last of such a run
/// carries <see cref="TiedToNext"/> = true. Spelling a multi-tick run as tied
/// standard note values / dotted rests is the notation writer's job (Step 11);
/// this type only guarantees the ticks are correct.
/// </summary>
public readonly record struct ScoreElement(
    ElementKind Kind, Pitch? Pitch, int Velocity, int LengthTicks, bool TiedToNext)
{
    /// <summary>A sounding note of the given pitch, velocity and tick length.</summary>
    public static ScoreElement Note(Pitch pitch, int velocity, int lengthTicks, bool tiedToNext = false)
    {
        if (lengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthTicks), lengthTicks, "Length must be positive.");
        }

        if (velocity is < 0 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(velocity), velocity, "Velocity must be 0..127.");
        }

        return new ScoreElement(ElementKind.Note, pitch, velocity, lengthTicks, tiedToNext);
    }

    /// <summary>A rest of the given tick length.</summary>
    public static ScoreElement Rest(int lengthTicks)
    {
        if (lengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthTicks), lengthTicks, "Length must be positive.");
        }

        return new ScoreElement(ElementKind.Rest, null, 0, lengthTicks, false);
    }
}
