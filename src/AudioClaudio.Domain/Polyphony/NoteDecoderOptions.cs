namespace AudioClaudio.Domain.Polyphony;

/// <summary>
/// Thresholds for <see cref="BasicPitchNoteDecoder"/>, matching Basic Pitch's own defaults.
/// </summary>
/// <param name="OnsetThreshold">Minimum onset activation (at a time-peak) to start a note.</param>
/// <param name="FrameThreshold">Minimum frame activation for a note to keep sounding.</param>
/// <param name="MinNoteLenFrames">Notes shorter than this many frames are discarded (flicker floor).</param>
/// <param name="InferOnsets">Add onsets inferred from large frame-energy jumps, not just predicted ones.</param>
/// <param name="MelodiaTrick">Sweep up leftover sustained energy that had no clear onset.</param>
/// <param name="EnergyTolerance">Frames of sub-threshold energy tolerated before a note is ended.</param>
public sealed record NoteDecoderOptions(
    double OnsetThreshold = 0.5,
    double FrameThreshold = 0.3,
    int MinNoteLenFrames = 11,
    bool InferOnsets = true,
    bool MelodiaTrick = true,
    int EnergyTolerance = 11)
{
    /// <summary>Basic Pitch's stock decoding thresholds.</summary>
    public static NoteDecoderOptions Default { get; } = new();
}
