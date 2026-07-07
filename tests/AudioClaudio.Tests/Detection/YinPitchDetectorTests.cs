using System;
using AudioClaudio.Domain;
using AudioClaudio.Tests.Signals;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests;

public class YinPitchDetectorTests
{
    private const int Rate = 44100;
    private const int FrameSize = 4096;                 // N; W = N/2 = 2048 (see plan sizing)
    private static readonly SampleRate R = new(Rate);

    /// <summary>Single point of coupling to the Step 2 Frame constructor.</summary>
    private static Frame MakeFrame(float[] samples) =>
        new(samples, new SamplePosition(0L, R));

    [Fact]
    [Trait("Category", "Fast")]
    public void Detect_PureSineA4_IsVoicedWithin10Cents()
    {
        double hz = new Pitch(69).Frequency();          // A4 = 440 Hz
        Frame frame = MakeFrame(SignalGenerator.Sine(hz, FrameSize, R));

        PitchEstimate e = YinPitchDetector.Detect(frame, YinOptions.Default);

        Assert.True(e.IsVoiced);
        Assert.True(Math.Abs(PitchMath.CentsBetween(hz, e.FrequencyHz)) <= 10.0,
            $"A4: {PitchMath.CentsBetween(hz, e.FrequencyHz):F2} cents off (got {e.FrequencyHz:F3} Hz)");
        Assert.True(e.Confidence > 0.9, $"expected a deep dip; confidence was {e.Confidence:F3}");
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(33)]   // A1  ~55 Hz  — the low-end window stress case
    [InlineData(45)]   // A2  ~110 Hz
    [InlineData(57)]   // A3  ~220 Hz
    [InlineData(69)]   // A4  440 Hz
    [InlineData(84)]   // C6  ~1047 Hz
    [InlineData(96)]   // C7  ~2093 Hz — parabolic-interpolation stress case
    public void Detect_LandmarkPitches_Within10Cents(int midi)
    {
        double hz = new Pitch(midi).Frequency();
        Frame frame = MakeFrame(SignalGenerator.Sine(hz, FrameSize, R));

        PitchEstimate e = YinPitchDetector.Detect(frame, YinOptions.Default);

        Assert.True(e.IsVoiced, $"MIDI {midi} came back unvoiced");
        double cents = PitchMath.CentsBetween(hz, e.FrequencyHz);
        Assert.True(Math.Abs(cents) <= 10.0, $"MIDI {midi}: {cents:F2} cents off (got {e.FrequencyHz:F3} Hz)");
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Detect_Silence_IsUnvoiced()
    {
        Frame frame = MakeFrame(new float[FrameSize]);   // all zeros

        PitchEstimate e = YinPitchDetector.Detect(frame, YinOptions.Default);

        Assert.False(e.IsVoiced);
        Assert.Equal(PitchEstimate.Unvoiced, e);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Detect_WhiteNoise_IsUnvoiced()
    {
        for (int seed = 1000; seed < 1010; seed++)
        {
            var rng = new Random(seed);                  // fixed seed => deterministic test input
            var samples = new float[FrameSize];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (float)(rng.NextDouble() * 2.0 - 1.0);

            PitchEstimate e = YinPitchDetector.Detect(MakeFrame(samples), YinOptions.Default);

            Assert.False(e.IsVoiced, $"white noise (seed {seed}) was reported voiced at {e.FrequencyHz:F1} Hz");
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Property_PureSine_Within10CentsAcrossRange()
    {
        // A frequency uniformly inside each semitone band so we exercise off-grid
        // fundamentals too, not just the 64 exact piano pitches.
        Gen<double> hzGen =
            from midi in Gen.Int[33, 96]
            from detune in Gen.Double[-0.49, 0.49]     // fraction of a semitone
            select new Pitch(midi).Frequency() * Math.Pow(2.0, detune / 12.0);

        hzGen.Sample(hz =>
        {
            Frame frame = MakeFrame(SignalGenerator.Sine(hz, FrameSize, R));
            PitchEstimate e = YinPitchDetector.Detect(frame, YinOptions.Default);

            Assert.True(e.IsVoiced, $"{hz:F2} Hz came back unvoiced");
            double cents = PitchMath.CentsBetween(hz, e.FrequencyHz);
            Assert.True(Math.Abs(cents) <= 10.0, $"{hz:F2} Hz: {cents:F2} cents off (got {e.FrequencyHz:F3} Hz)");
        },
        iter: 2000, seed: "0N0XdO8lNYZ0");
        // Seed pins reproducibility (§5). CsCheck seeds are interchangeable; if this
        // literal is rejected by your CsCheck version, run once and pin the seed it prints.
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Property_HarmonicStack_Within10CentsAndNoOctaveError()
    {
        var gen =
            from midi in Gen.Int[33, 96]
            from partials in Gen.Int[3, 8]             // fundamental + up to 7 overtones
            from decay in Gen.Double[0.5, 2.0]         // amplitude of partial k = 1/k^decay
            select (midi, partials, decay);

        gen.Sample(t =>
        {
            double f0 = new Pitch(t.midi).Frequency();
            Frame frame = MakeFrame(
                SignalGenerator.HarmonicStack(f0, FrameSize, R, partials: t.partials, decay: t.decay));

            PitchEstimate e = YinPitchDetector.Detect(frame, YinOptions.Default);

            Assert.True(e.IsVoiced, $"MIDI {t.midi} (k={t.partials}, p={t.decay:F2}) came back unvoiced");

            double cents = PitchMath.CentsBetween(f0, e.FrequencyHz);
            Assert.True(Math.Abs(cents) <= 10.0,
                $"MIDI {t.midi} (k={t.partials}, p={t.decay:F2}): {cents:F2} cents off (got {e.FrequencyHz:F3} Hz)");

            // Explicit octave-error guard: the estimate must sit far nearer the
            // fundamental than either the octave-up partial or the octave-down alias.
            double toFundamental = Math.Abs(PitchMath.CentsBetween(f0, e.FrequencyHz));
            double toOctaveUp = Math.Abs(PitchMath.CentsBetween(2.0 * f0, e.FrequencyHz));
            double toOctaveDown = Math.Abs(PitchMath.CentsBetween(0.5 * f0, e.FrequencyHz));
            Assert.True(toFundamental < toOctaveUp && toFundamental < toOctaveDown,
                $"octave error at MIDI {t.midi}: locked near {e.FrequencyHz:F2} Hz");
        },
        iter: 1000, seed: "0N0XdO8lNYZ0");
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Detect_IsDeterministic_SameFrameSameEstimate()
    {
        double hz = new Pitch(60).Frequency();          // middle C
        float[] samples = SignalGenerator.Sine(hz, FrameSize, R);

        PitchEstimate first = YinPitchDetector.Detect(MakeFrame(samples), YinOptions.Default);
        PitchEstimate second = YinPitchDetector.Detect(MakeFrame(samples), YinOptions.Default);

        // Value equality across all fields — no run-to-run drift.
        Assert.Equal(first, second);
        Assert.Equal(first.FrequencyHz, second.FrequencyHz, precision: 12);
        Assert.Equal(first.Confidence, second.Confidence, precision: 12);
    }
}
