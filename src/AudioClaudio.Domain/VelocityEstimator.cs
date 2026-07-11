using System;

namespace AudioClaudio.Domain;

/// <summary>
/// Maps a note's attack energy (the peak RMS just after its onset) to a MIDI velocity (1..127) on a
/// <b>perceptual</b> (dBFS) scale, so a louder attack reads as a louder dynamic (pp..ff via
/// <see cref="DynamicMarks"/>). Absolute, causal, and per-note — no whole-piece normalization — which keeps
/// it usable in the causal live path and consistent with the detector. Pure and deterministic
/// (non-negotiable 3).
///
/// The calibration below is tuned so a typical synthesized/normalized piano attack spans pp..ff. Absolute
/// microphone gain shifts where a performance lands on that scale, but the <i>relative</i> ordering — a
/// crescendo, a soft passage — is recovered regardless, which is what notation dynamics express.
/// </summary>
public static class VelocityEstimator
{
    // Calibrated against the committed GeneralUser GS SoundFont at a mid register: a ~ff attack renders
    // at ~-29 dBFS RMS and a ~p attack at ~-53 dBFS, so this ~24 dB window (~5 velocity/dB) spreads a
    // performance's dynamics across pp..ff. Absolute microphone gain or a different instrument shifts
    // where a performance lands; the RELATIVE ordering is what survives (see the class remarks).

    /// <summary>Attack RMS at or above this level (in dBFS) reads as the loudest dynamic.</summary>
    public const double CeilDbfs = -29.0;

    /// <summary>Attack RMS at or below this level (in dBFS) reads as the softest dynamic.</summary>
    public const double FloorDbfs = -53.0;

    /// <summary>Velocity floor for the softest audible attack (never 0 — that is a MIDI note-off).</summary>
    public const int MinOut = 8;

    /// <summary>Velocity ceiling for the loudest attack.</summary>
    public const int MaxOut = 127;

    /// <summary>
    /// The MIDI velocity for an attack whose peak RMS is <paramref name="attackRms"/> (mono PCM, so
    /// RMS ∈ [0, 1]). Silence/degenerate input maps to <see cref="MinOut"/>. Monotonic non-decreasing
    /// in the attack level, so louder always reads as at-least-as-loud.
    /// </summary>
    public static int FromAttackEnergy(double attackRms)
    {
        if (attackRms <= 0.0 || double.IsNaN(attackRms))
        {
            return MinOut;
        }

        double dbfs = 20.0 * Math.Log10(Math.Min(1.0, attackRms)); // <= 0 dBFS
        double t = Math.Clamp((dbfs - FloorDbfs) / (CeilDbfs - FloorDbfs), 0.0, 1.0);
        int velocity = (int)Math.Round(MinOut + (t * (MaxOut - MinOut)), MidpointRounding.AwayFromZero);
        return Math.Clamp(velocity, MinOut, MaxOut);
    }
}
