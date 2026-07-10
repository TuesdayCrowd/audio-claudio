using System;
using System.Collections.Generic;

namespace AudioClaudio.Domain.Spectral;

/// <summary>
/// Computes a chromagram — a sequence of 12-bin pitch-class energy vectors, one per frame, each
/// L2-normalized. Every FFT bin whose frequency lands in the piano's fundamental range is folded into
/// its pitch class (octave discarded), so the feature captures <b>which notes are sounding over time</b>
/// while discarding timbre and octave. That timbre-invariance is the whole point: it lets a real-piano
/// recording be compared against a SoundFont re-synthesis of a transcription (see
/// <see cref="Evaluation.ChromaSimilarity"/>). Pure and deterministic; reuses the Hann window + FFT of
/// <see cref="SpectralFrontEnd"/> (Step 3).
/// </summary>
public static class Chromagram
{
    public const int DefaultFrameSize = 4096; // ~93 ms at 44.1 kHz — resolves fundamentals, absorbs onset jitter
    public const int DefaultHop = 2048;

    // Fold only the fundamental range; higher partials would smear the chroma with a real piano's
    // rich overtones, and sub-audio bins are noise.
    private const double MinHz = 50.0;   // ~G1
    private const double MaxHz = 2100.0; // ~C7

    public static IReadOnlyList<double[]> FromSamples(
        float[] samples, SampleRate rate, int frameSize = DefaultFrameSize, int hop = DefaultHop)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return FromFrames(Framing.Split(samples, rate, new FrameParameters(frameSize, hop)), frameSize);
    }

    public static IReadOnlyList<double[]> FromFrames(IEnumerable<Frame> frames, int frameSize = DefaultFrameSize)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var frontEnd = new SpectralFrontEnd(frameSize, new Radix2Fft());
        var chromas = new List<double[]>();
        foreach (MagnitudeSpectrum spectrum in frontEnd.Analyze(frames))
        {
            chromas.Add(ChromaOf(spectrum));
        }

        return chromas;
    }

    private static double[] ChromaOf(MagnitudeSpectrum spectrum)
    {
        var chroma = new double[12];
        for (int bin = 1; bin < spectrum.BinCount; bin++)
        {
            double frequency = spectrum.FrequencyOf(bin);
            if (frequency < MinHz || frequency > MaxHz)
            {
                continue;
            }

            int pitchClass = ((((int)Math.Round(69 + (12 * Math.Log2(frequency / 440.0)))) % 12) + 12) % 12;
            double magnitude = spectrum[bin];
            chroma[pitchClass] += magnitude * magnitude; // power, not magnitude — suppresses the low sidelobe floor, sharpening discrimination
        }

        double norm = 0.0;
        foreach (double v in chroma)
        {
            norm += v * v;
        }

        norm = Math.Sqrt(norm);
        if (norm > 0.0)
        {
            for (int i = 0; i < 12; i++)
            {
                chroma[i] /= norm;
            }
        }

        return chroma;
    }
}
