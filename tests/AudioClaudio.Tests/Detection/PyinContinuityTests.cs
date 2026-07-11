using System.Collections.Generic;
using AudioClaudio.Domain;
using AudioClaudio.Tests.Signals;
using Xunit;

namespace AudioClaudio.Tests;

/// <summary>
/// pYIN-lite (v2 Stage 2): the causal continuity correction of YIN's rare octave error. Tests the pure
/// <see cref="YinPitchDetector.ApplyContinuity"/> directly (crafting a signal YIN reliably octave-errors
/// on is impractical — YIN is robust), plus the no-op guarantee on clean tones.
/// </summary>
public class PyinContinuityTests
{
    private static readonly YinOptions Options = YinOptions.Default;

    private static PitchEstimate Voiced(double hz, double confidence = 0.85) => PitchEstimate.Voiced(hz, confidence);

    [Fact]
    [Trait("Category", "Fast")]
    public void OctaveDownError_IsCorrectedToTheContinuityCandidate()
    {
        // YIN latched onto a shallow octave-down artifact (220 Hz, aperiodicity 0.30) an octave BELOW the
        // previous 440, but the true fundamental at 440 is a DEEPER dip (0.12) — so it wins.
        PitchEstimate result = YinPitchDetector.ApplyContinuity(
            Voiced(220.0, confidence: 0.70), Voiced(440.0),
            new List<PitchCandidate> { new(220.0, 0.30), new(440.0, 0.12) }, Options);

        Assert.InRange(result.FrequencyHz, 435.0, 445.0);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void OctaveUpError_IsCorrectedToTheContinuityCandidate()
    {
        // YIN picked a shallow 880 (aperiodicity 0.30) an octave ABOVE previous 440; the deeper 440
        // candidate (0.15) wins.
        PitchEstimate result = YinPitchDetector.ApplyContinuity(
            Voiced(880.0, confidence: 0.70), Voiced(440.0),
            new List<PitchCandidate> { new(880.0, 0.30), new(440.0, 0.15) }, Options);

        Assert.InRange(result.FrequencyHz, 435.0, 445.0);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void ARealOctaveLeapNextToARingingNote_IsNotPulledBack()
    {
        // The closed-loop failure case: a genuine octave leap up to a STRONG new note (880, deep dip 0.10)
        // while the previous note (440) is still ringing through a short rest — so a 440 candidate exists,
        // but it is a FADING, shallower dip (0.25). The new note must NOT be pulled back down an octave,
        // because the continuity candidate is less periodic than YIN's (correct) pick.
        PitchEstimate result = YinPitchDetector.ApplyContinuity(
            Voiced(880.0, confidence: 0.90), Voiced(440.0),
            new List<PitchCandidate> { new(880.0, 0.10), new(440.0, 0.25) }, Options);

        Assert.InRange(result.FrequencyHz, 870.0, 890.0); // stays on the real 880 leap
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void ANonOctaveJump_LeavesYinsPickAlone()
    {
        // 466 vs the previous 440 is a semitone, not an octave — YIN's pick stands even with a 440 candidate.
        PitchEstimate result = YinPitchDetector.ApplyContinuity(
            Voiced(466.16), Voiced(440.0),
            new List<PitchCandidate> { new(466.16, 0.10), new(440.0, 0.10) }, Options);

        Assert.InRange(result.FrequencyHz, 460.0, 470.0);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void NoCandidateNearThePreviousPitch_LeavesYinsPickAlone()
    {
        // An octave jump, but nothing near 440 — there is no fundamental to recover, so trust YIN.
        PitchEstimate result = YinPitchDetector.ApplyContinuity(
            Voiced(220.0), Voiced(440.0),
            new List<PitchCandidate> { new(220.0, 0.10), new(147.0, 0.20) }, Options);

        Assert.InRange(result.FrequencyHz, 218.0, 222.0);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void ACandidateNearThePreviousPitchButTooAperiodic_IsNotTrusted()
    {
        // Octave jump; a candidate sits at 440 but its d' (0.70) is above ContinuityThreshold (0.50) — a
        // spurious dip, not a real fundamental — so YIN's pick stands.
        PitchEstimate result = YinPitchDetector.ApplyContinuity(
            Voiced(220.0), Voiced(440.0),
            new List<PitchCandidate> { new(220.0, 0.10), new(440.0, 0.70) }, Options);

        Assert.InRange(result.FrequencyHz, 218.0, 222.0);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void AnUnvoicedPrevious_LeavesYinsPickAlone()
    {
        PitchEstimate result = YinPitchDetector.ApplyContinuity(
            Voiced(220.0), PitchEstimate.Unvoiced,
            new List<PitchCandidate> { new(440.0, 0.10) }, Options);

        Assert.InRange(result.FrequencyHz, 218.0, 222.0);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(33)]  // A1
    [InlineData(69)]  // A4
    [InlineData(84)]  // C6
    public void OnACleanSine_ContinuityIsANoOp(int midi)
    {
        // A clean, stable tone: YIN does not jump octaves, so passing the previous frame's (identical)
        // estimate must not change the result — the correction only ever touches ambiguous frames.
        const int Rate = 44100, FrameSize = 4096;
        var r = new SampleRate(Rate);
        double hz = new Pitch(midi).Frequency();
        var frame = new Frame(SignalGenerator.Sine(hz, FrameSize, r), new SamplePosition(0, r));

        PitchEstimate withoutPrev = YinPitchDetector.Detect(frame, YinOptions.Default);
        PitchEstimate withPrev = YinPitchDetector.Detect(frame, YinOptions.Default, withoutPrev);

        Assert.Equal(withoutPrev, withPrev);
    }
}
