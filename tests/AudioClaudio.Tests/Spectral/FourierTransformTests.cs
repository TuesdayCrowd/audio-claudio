using System;
using AudioClaudio.Domain.Spectral;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.Spectral;

[Trait("Category", "Fast")]
public class FourierTransformTests
{
    // Parseval's theorem — the FFT's trial balance: Σ x[n]² == (1/N)·Σ |X[k]|².
    // Relative tolerance is loose enough for a single-precision path and
    // trivially satisfied by the double-precision hand-rolled path.
    [Fact]
    public void Forward_satisfies_Parsevals_theorem()
    {
        IFourierTransform fft = TestFft.Create();

        Gen.Double[-1.0, 1.0].Array[1024].Sample(samples =>
        {
            System.Numerics.Complex[] spectrum = fft.Forward(samples);

            double timeEnergy = 0.0;
            foreach (double s in samples) timeEnergy += s * s;

            double freqEnergy = 0.0;
            foreach (System.Numerics.Complex c in spectrum) freqEnergy += c.Magnitude * c.Magnitude;
            freqEnergy /= samples.Length;

            double denom = Math.Max(timeEnergy, 1e-9);
            return Math.Abs(timeEnergy - freqEnergy) / denom < 1e-4;
        }, iter: 200, seed: "0002mmN8nqH6");
        // Determinism (non-negotiable #3): seed pinned per this repo's CsCheck convention
        // (see FramingProperties/PitchMathTests) so every CI run samples the same signals.
    }

    [Fact]
    public void Forward_rejects_non_power_of_two_lengths()
    {
        IFourierTransform fft = TestFft.Create();
        Assert.Throws<ArgumentException>(() => fft.Forward(new double[1000]));
    }

    // Extra hardening beyond Parseval (which only checks aggregate energy, not bin
    // correctness): cross-check every bin of Radix2Fft.Forward against a naive O(N²)
    // direct DFT reference — X[k] = Σ_t x[t]·e^(−2πi·k·t/N), the textbook definition,
    // computed independently of the radix-2 butterfly implementation. This is a test
    // oracle only; it never ships in production.
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void Forward_matches_the_naive_direct_dft_reference(int n)
    {
        IFourierTransform fft = TestFft.Create();

        Gen.Double[-1.0, 1.0].Array[n].Sample(samples =>
        {
            System.Numerics.Complex[] actual = fft.Forward(samples);
            System.Numerics.Complex[] expected = NaiveDft(samples);

            double maxError = 0.0;
            for (int k = 0; k < samples.Length; k++)
                maxError = Math.Max(maxError, (actual[k] - expected[k]).Magnitude);

            return maxError < 1e-9;
        }, iter: 50, seed: "7xCP-wMl20i1");
    }

    private static System.Numerics.Complex[] NaiveDft(double[] samples)
    {
        int n = samples.Length;
        var result = new System.Numerics.Complex[n];
        for (int k = 0; k < n; k++)
        {
            System.Numerics.Complex sum = System.Numerics.Complex.Zero;
            for (int t = 0; t < n; t++)
            {
                double angle = -2.0 * Math.PI * k * t / n;
                sum += samples[t] * new System.Numerics.Complex(Math.Cos(angle), Math.Sin(angle));
            }
            result[k] = sum;
        }
        return result;
    }
}
