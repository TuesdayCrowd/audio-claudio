namespace AudioClaudio.Domain;

/// <summary>
/// Tunable parameters for adaptive onset peak-picking (R5.1). Defaults are a
/// reasonable starting point for the MVP frame/hop; the golden and property tests
/// (Task 8) validate them and may be revisited if they fail there.
/// </summary>
public sealed record OnsetDetectorOptions
{
    /// <summary>Half-width, in frames, of the window used for the adaptive local mean.</summary>
    public int ThresholdWindowFrames { get; init; } = 8;

    /// <summary>A peak's normalized novelty must exceed Multiplier · localMean + Delta.</summary>
    public double ThresholdMultiplier { get; init; } = 1.0;

    /// <summary>Absolute margin above the local mean, in normalized-novelty units [0,1].</summary>
    public double ThresholdDelta { get; init; } = 0.1;

    /// <summary>A peak must be a local maximum within ± this many frames.</summary>
    public int LocalMaxRadiusFrames { get; init; } = 1;

    /// <summary>Minimum spacing, in frames, between accepted onsets; suppresses attack double-triggers.</summary>
    public int MinGapFrames { get; init; } = 3;
}
