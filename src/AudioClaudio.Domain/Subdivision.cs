namespace AudioClaudio.Domain;

/// <summary>The quantization grid resolution: the note value one grid tick represents.</summary>
public enum Subdivision
{
    Quarter,
    Eighth,
    Sixteenth,
}

public static class SubdivisionExtensions
{
    /// <summary>How many grid ticks fill one quarter note (Quarter=1, Eighth=2, Sixteenth=4).</summary>
    public static int TicksPerQuarter(this Subdivision subdivision) => subdivision switch
    {
        Subdivision.Quarter => 1,
        Subdivision.Eighth => 2,
        Subdivision.Sixteenth => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(subdivision), subdivision, "Unknown subdivision."),
    };
}
