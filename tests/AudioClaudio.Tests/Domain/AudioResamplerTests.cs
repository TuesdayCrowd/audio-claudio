using System;
using System.Numerics;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;
using Xunit;

namespace AudioClaudio.Tests.Domain;

/// <summary>
/// Band-limited resampling to the model's 22 050 Hz rate. It must preserve a tone's pitch (the
/// whole point — a resampler that shifts frequencies would ruin transcription) and anti-alias on
/// downsampling. Pitch preservation is checked with the domain's own FFT: the peak bin must stay
/// at the tone's frequency after resampling.
/// </summary>
public class AudioResamplerTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Output_length_follows_the_rate_ratio()
    {
        var input = new float[44100]; // 1 s at 44.1 kHz
        float[] output = AudioResampler.Resample(input, 44100, 22050);
        Assert.InRange(output.Length, 22050 - 2, 22050 + 2); // ~half as many samples
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void A_constant_signal_stays_constant()
    {
        var input = new float[10000];
        Array.Fill(input, 0.5f);
        float[] output = AudioResampler.Resample(input, 44100, 22050);

        // Away from the very edges, DC is preserved (kernel weights sum to 1).
        for (int i = 100; i < output.Length - 100; i++)
        {
            Assert.InRange(output[i], 0.49f, 0.51f);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Resampling_preserves_a_tone_frequency()
    {
        const int inRate = 44100;
        const int outRate = 22050;
        const double toneHz = 1000.0;
        const int fftSize = 4096;

        // Generate enough input that the resampled output covers one FFT frame.
        int inLen = (int)((long)fftSize * inRate / outRate) + 64;
        var input = new float[inLen];
        for (int i = 0; i < inLen; i++)
        {
            input[i] = (float)Math.Sin(2.0 * Math.PI * toneHz * i / inRate);
        }

        float[] output = AudioResampler.Resample(input, inRate, outRate);

        // FFT the first fftSize output samples; the peak bin should sit at 1 kHz.
        var frame = new double[fftSize];
        for (int i = 0; i < fftSize; i++)
        {
            frame[i] = output[i];
        }

        int peak = PeakBin(new Radix2Fft(), frame);
        double peakHz = (double)peak * outRate / fftSize;
        Assert.InRange(peakHz, toneHz - outRate / (double)fftSize * 2, toneHz + outRate / (double)fftSize * 2);
    }

    private static int PeakBin(IFourierTransform fft, double[] frame)
    {
        Complex[] spectrum = fft.Forward(frame);
        int best = 0;
        double bestMag = -1.0;
        for (int k = 1; k < frame.Length / 2; k++)
        {
            double mag = spectrum[k].Magnitude;
            if (mag > bestMag)
            {
                bestMag = mag;
                best = k;
            }
        }

        return best;
    }
}
