using System;

namespace AudioClaudio.Domain;

/// <summary>
/// The result of running the pitch detector on a single analysis frame:
/// either a voiced fundamental-frequency estimate with a confidence in [0, 1],
/// or <see cref="Unvoiced"/> (silence or noise). A pure domain value; carries
/// no clock, no I/O. (R4.1)
/// </summary>
public readonly record struct PitchEstimate
{
    /// <summary>True when a fundamental was found; false for silence/noise.</summary>
    public bool IsVoiced { get; }

    /// <summary>Estimated fundamental in Hz. Meaningful only when <see cref="IsVoiced"/>.</summary>
    public double FrequencyHz { get; }

    /// <summary>Detection confidence in [0, 1]; 1 − aperiodicity at the chosen lag. 0 when unvoiced.</summary>
    public double Confidence { get; }

    private PitchEstimate(bool isVoiced, double frequencyHz, double confidence)
    {
        IsVoiced = isVoiced;
        FrequencyHz = frequencyHz;
        Confidence = confidence;
    }

    /// <summary>A voiced estimate. Frequency must be positive; confidence must lie in [0, 1].</summary>
    public static PitchEstimate Voiced(double frequencyHz, double confidence)
    {
        if (frequencyHz <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), frequencyHz, "Voiced frequency must be positive.");
        if (confidence is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "Confidence must lie in [0, 1].");
        return new PitchEstimate(true, frequencyHz, confidence);
    }

    /// <summary>The canonical "no pitch here" result (silence or noise).</summary>
    public static readonly PitchEstimate Unvoiced = new(false, 0.0, 0.0);
}
