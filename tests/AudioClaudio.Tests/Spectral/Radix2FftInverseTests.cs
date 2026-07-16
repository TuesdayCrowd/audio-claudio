using System;
using System.Numerics;
using AudioClaudio.Domain.Spectral;
using Xunit;

namespace AudioClaudio.Tests.Spectral;

// Stage 1.1 (source separation reconstruction DSP) — Inverse is the seam an upcoming
// iSTFT/overlap-add stage needs to turn a spectrogram back into time-domain samples.
// No dependency on the rest of that feature: this only proves Inverse(Forward(x)) == x.
[Trait("Category", "Fast")]
public class Radix2FftInverseTests
{
    // A 4096-sample 440 Hz sine at 44100 Hz — a realistic frame size/pitch, round-tripped
    // through Forward then Inverse must recover the original samples to double-precision.
    [Fact]
    public void Inverse_of_Forward_recovers_a_sine_signal()
    {
        var fft = new Radix2Fft();
        const int n = 4096;
        const double sampleRate = 44100.0;
        const double frequency = 440.0;

        var samples = new double[n];
        for (int i = 0; i < n; i++)
            samples[i] = Math.Sin(2.0 * Math.PI * frequency * i / sampleRate);

        Complex[] spectrum = fft.Forward(samples);
        Complex[] roundTripped = fft.Inverse(spectrum);

        Assert.Equal(n, roundTripped.Length);
        double maxError = 0.0;
        for (int i = 0; i < n; i++)
            maxError = Math.Max(maxError, Math.Abs(roundTripped[i].Real - samples[i]));

        Assert.True(maxError < 1e-9, $"max round-trip error {maxError} exceeded tolerance");
    }

    // A unit impulse is the sharpest possible time-domain signal (a flat spectrum) —
    // a second, independent case so the sine round-trip above isn't a fluke.
    [Fact]
    public void Inverse_of_Forward_recovers_an_impulse()
    {
        var fft = new Radix2Fft();
        const int n = 1024;

        var samples = new double[n];
        samples[0] = 1.0;

        Complex[] spectrum = fft.Forward(samples);
        Complex[] roundTripped = fft.Inverse(spectrum);

        Assert.Equal(n, roundTripped.Length);
        double maxError = 0.0;
        for (int i = 0; i < n; i++)
            maxError = Math.Max(maxError, Math.Abs(roundTripped[i].Real - samples[i]));

        Assert.True(maxError < 1e-9, $"max round-trip error {maxError} exceeded tolerance");
    }

    // A third case at a different frequency + length, to guard against a size-specific
    // (e.g. power-of-two-adjacent) bug in the conjugate-trick implementation.
    [Fact]
    public void Inverse_of_Forward_recovers_a_second_frequency_and_length()
    {
        var fft = new Radix2Fft();
        const int n = 2048;
        const double sampleRate = 22050.0;
        const double frequency = 261.63; // middle C

        var samples = new double[n];
        for (int i = 0; i < n; i++)
            samples[i] = Math.Sin(2.0 * Math.PI * frequency * i / sampleRate);

        Complex[] spectrum = fft.Forward(samples);
        Complex[] roundTripped = fft.Inverse(spectrum);

        Assert.Equal(n, roundTripped.Length);
        double maxError = 0.0;
        for (int i = 0; i < n; i++)
            maxError = Math.Max(maxError, Math.Abs(roundTripped[i].Real - samples[i]));

        Assert.True(maxError < 1e-9, $"max round-trip error {maxError} exceeded tolerance");
    }
}
