using System.Collections.Generic;

namespace AudioClaudio.Domain.Polyphony;

/// <summary>
/// The polyphonic analogue of <see cref="ScoreElement"/>: one chord (one or more pitches sounding
/// together) or a rest, its length in grid ticks. A chord crossing a barline is stored as several
/// elements whose lengths sum to its quantized duration, each but the last carrying
/// <see cref="TiedToNext"/>. Pitches are in ascending MIDI order. The monophonic
/// <see cref="ScoreElement"/> is left untouched; this type lives beside it.
/// </summary>
public readonly record struct ChordElement(
    ElementKind Kind, IReadOnlyList<Pitch> Pitches, int Velocity, int LengthTicks, bool TiedToNext)
{
    /// <summary>A sounding chord of the given pitches, velocity and tick length.</summary>
    public static ChordElement Note(IReadOnlyList<Pitch> pitches, int velocity, int lengthTicks, bool tiedToNext = false)
    {
        ArgumentNullException.ThrowIfNull(pitches);
        if (pitches.Count == 0)
        {
            throw new ArgumentException("A note chord must have at least one pitch.", nameof(pitches));
        }

        if (lengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthTicks), lengthTicks, "Length must be positive.");
        }

        if (velocity is < 0 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(velocity), velocity, "Velocity must be 0..127.");
        }

        return new ChordElement(ElementKind.Note, pitches, velocity, lengthTicks, tiedToNext);
    }

    /// <summary>A rest of the given tick length.</summary>
    public static ChordElement Rest(int lengthTicks)
    {
        if (lengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthTicks), lengthTicks, "Length must be positive.");
        }

        return new ChordElement(ElementKind.Rest, Array.Empty<Pitch>(), 0, lengthTicks, false);
    }
}
