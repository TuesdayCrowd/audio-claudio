using System;

namespace AudioClaudio.Domain;

/// <summary>
/// Named tuning parameters for <see cref="YinPitchDetector"/>. The
/// <see cref="Threshold"/> is the R4.1 voiced/unvoiced cutoff on the
/// cumulative-mean-normalized difference; the frequency window bounds the lag
/// search. All values are validated on construction (fail fast).
/// </summary>
public sealed class YinOptions
{
    /// <summary>Aperiodicity cutoff in (0, 1): the first lag whose d' dips below this is a voiced candidate.</summary>
    public double Threshold { get; }

    /// <summary>Lowest fundamental to search for, in Hz. Sets the maximum lag.</summary>
    public double MinFrequencyHz { get; }

    /// <summary>Highest fundamental to search for, in Hz. Sets the minimum lag.</summary>
    public double MaxFrequencyHz { get; }

    /// <summary>
    /// pYIN-lite (v2 Stage 2): how close to a full octave (in cents) YIN's estimate must jump from the
    /// previous frame's pitch before the causal continuity check engages. A within-note octave jump is
    /// the octave-error signature; genuine octave leaps in music carry a new onset (which resets the
    /// previous pitch across the intervening rest), so this does not fight real playing.
    /// </summary>
    public double OctaveContinuityCents { get; }

    /// <summary>How near the previous pitch (in cents) a candidate must sit to count as the continuity
    /// candidate that overrides an octave jump.</summary>
    public double ContinuityMatchCents { get; }

    /// <summary>The aperiodicity (d') a continuity candidate must be below to be trusted over YIN's octave
    /// jump — looser than <see cref="Threshold"/> (the candidate is corroborated by continuity).</summary>
    public double ContinuityThreshold { get; }

    public YinOptions(
        double threshold = 0.15, double minFrequencyHz = 45.0, double maxFrequencyHz = 2500.0,
        double octaveContinuityCents = 100.0, double continuityMatchCents = 100.0, double continuityThreshold = 0.5)
    {
        if (threshold is <= 0.0 or >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "Threshold must lie in the open interval (0, 1).");
        if (minFrequencyHz <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(minFrequencyHz), minFrequencyHz, "Minimum frequency must be positive.");
        if (maxFrequencyHz <= minFrequencyHz)
            throw new ArgumentOutOfRangeException(nameof(maxFrequencyHz), maxFrequencyHz, "Maximum frequency must exceed the minimum.");
        if (continuityThreshold is <= 0.0 or >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(continuityThreshold), continuityThreshold, "Continuity threshold must lie in (0, 1).");

        Threshold = threshold;
        MinFrequencyHz = minFrequencyHz;
        MaxFrequencyHz = maxFrequencyHz;
        OctaveContinuityCents = octaveContinuityCents;
        ContinuityMatchCents = continuityMatchCents;
        ContinuityThreshold = continuityThreshold;
    }

    /// <summary>Defaults covering MIDI 33–96 with head-room at both ends (see the plan's sizing note).</summary>
    public static YinOptions Default { get; } = new();
}
