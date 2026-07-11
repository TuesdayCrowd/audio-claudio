namespace AudioClaudio.Domain;

/// <summary>The quantization grid resolution: the note value one grid tick represents.</summary>
public enum Subdivision
{
    Quarter,
    Eighth,
    Sixteenth,

    /// <summary>Twelve ticks per quarter — the combined straight + triplet grid (v2 Stage 3d). It is the
    /// least common multiple of the sixteenth grid (4/quarter) and the eighth-triplet grid (3/quarter), so
    /// both sixteenths (3 ticks) and eighth-triplets (4 ticks) land on integer ticks. Used by the
    /// polyphonic engine when triplets are wanted; the straight-only paths keep Sixteenth.</summary>
    Twelfth,
}

public static class SubdivisionExtensions
{
    /// <summary>How many grid ticks fill one quarter note (Quarter=1, Eighth=2, Sixteenth=4, Twelfth=12).</summary>
    public static int TicksPerQuarter(this Subdivision subdivision) => subdivision switch
    {
        Subdivision.Quarter => 1,
        Subdivision.Eighth => 2,
        Subdivision.Sixteenth => 4,
        Subdivision.Twelfth => 12,
        _ => throw new ArgumentOutOfRangeException(nameof(subdivision), subdivision, "Unknown subdivision."),
    };
}
