namespace AudioClaudio.Domain;

/// <summary>
/// Maps a MIDI velocity (1–127) to a notation dynamic mark (<c>pp</c>…<c>ff</c>). Boundaries follow
/// MuseScore's velocity ranges, so a note's loudness lands on the dynamic a musician would expect.
/// Pure; the six-level quantization is what keeps score dynamics from flickering on small velocity noise.
/// </summary>
public static class DynamicMarks
{
    public static string From(int velocity) => velocity switch
    {
        < 33 => "pp",
        < 49 => "p",
        < 64 => "mp",
        < 80 => "mf",
        < 96 => "f",
        _ => "ff",
    };
}
