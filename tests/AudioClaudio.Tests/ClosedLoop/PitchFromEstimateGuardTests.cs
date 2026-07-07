using AudioClaudio.Application;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>
/// Directly proves the YIN-to-Pitch guard inside <see cref="TranscriptionPipeline"/>: an
/// out-of-range voiced estimate must become <c>null</c> (unvoiced), never throw. Without
/// this guard, a single wild frame (e.g. an attack transient YIN briefly mis-locks on) would
/// crash the whole closed-loop suite with an unhandled <see cref="System.ArgumentOutOfRangeException"/>
/// from <see cref="Pitch"/>'s constructor.
/// </summary>
public sealed class PitchFromEstimateGuardTests
{
    [Trait("Category", "Fast")]
    [Fact]
    public void Unvoiced_estimate_maps_to_null()
    {
        Assert.Null(TranscriptionPipeline.GuardedPitchFromEstimate(PitchEstimate.Unvoiced));
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void In_range_voiced_estimate_maps_to_the_nearest_pitch()
    {
        double a4 = new Pitch(69).Frequency();
        Pitch? pitch = TranscriptionPipeline.GuardedPitchFromEstimate(PitchEstimate.Voiced(a4, 0.9));

        Assert.NotNull(pitch);
        Assert.Equal(69, pitch!.Value.MidiNumber);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void Voiced_estimate_far_above_the_88_key_range_maps_to_null_instead_of_throwing()
    {
        // 20 kHz is nowhere near a piano note (C8/MIDI108 is ~4186 Hz); this must not throw.
        Pitch? pitch = TranscriptionPipeline.GuardedPitchFromEstimate(PitchEstimate.Voiced(20000.0, 0.5));

        Assert.Null(pitch);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void Voiced_estimate_far_below_the_88_key_range_maps_to_null_instead_of_throwing()
    {
        // 1 Hz is nowhere near a piano note (A0/MIDI21 is 27.5 Hz); this must not throw.
        Pitch? pitch = TranscriptionPipeline.GuardedPitchFromEstimate(PitchEstimate.Voiced(1.0, 0.5));

        Assert.Null(pitch);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void Voiced_estimate_just_outside_the_range_maps_to_null()
    {
        // One semitone below A0 (MIDI 20) — the nearest-note rounding must not clamp into range.
        double belowA0 = new Pitch(21).Frequency() / System.Math.Pow(2.0, 1.0 / 12.0);
        Pitch? pitch = TranscriptionPipeline.GuardedPitchFromEstimate(PitchEstimate.Voiced(belowA0, 0.5));

        Assert.Null(pitch);
    }
}
