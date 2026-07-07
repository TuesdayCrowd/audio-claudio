using AudioClaudio.Domain;

namespace AudioClaudio.Tests.Signals;

/// <summary>
/// Deterministic test signals (R2.3). No randomness: identical arguments produce identical samples,
/// so any fixture built on top is reproducible. Amplitudes are kept &lt; 1 so output stays in [-1, 1).
/// </summary>
public static class SignalGenerator
{
    /// <summary>A pure sine at <paramref name="frequencyHz"/>: x[i] = A·sin(2π f i / rate).</summary>
    public static float[] Sine(double frequencyHz, int sampleCount, SampleRate rate, double amplitude = 0.8)
    {
        if (frequencyHz <= 0) throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        if (sampleCount < 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));
        if (amplitude is < 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(amplitude), amplitude, "Keep amplitude in [0, 1) to stay within [-1, 1].");

        var buffer = new float[sampleCount];
        double step = 2.0 * Math.PI * frequencyHz / rate.Hz;
        for (int i = 0; i < sampleCount; i++)
            buffer[i] = (float)(amplitude * Math.Sin(step * i));
        return buffer;
    }

    /// <summary>
    /// A harmonic stack: the fundamental plus <paramref name="partials"/> overtones at integer
    /// multiples of the fundamental, the k-th partial scaled by 1/k^<paramref name="decay"/>
    /// (piano-like roll-off). Normalised by the sum of coefficients so the peak stays &lt; amplitude &lt; 1.
    /// </summary>
    public static float[] HarmonicStack(
        double fundamentalHz, int sampleCount, SampleRate rate,
        int partials = 6, double decay = 1.0, double amplitude = 0.8)
    {
        if (fundamentalHz <= 0) throw new ArgumentOutOfRangeException(nameof(fundamentalHz));
        if (sampleCount < 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));
        if (partials < 1) throw new ArgumentOutOfRangeException(nameof(partials));
        if (amplitude is < 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(amplitude), amplitude, "Keep amplitude in [0, 1) to stay within [-1, 1].");

        double norm = 0.0;
        for (int k = 1; k <= partials; k++) norm += 1.0 / Math.Pow(k, decay);

        var buffer = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            double acc = 0.0;
            for (int k = 1; k <= partials; k++)
            {
                double coeff = 1.0 / Math.Pow(k, decay);
                double phase = 2.0 * Math.PI * (k * fundamentalHz) * i / rate.Hz;
                acc += coeff * Math.Sin(phase);
            }
            buffer[i] = (float)(amplitude * acc / norm); // |acc/norm| <= 1  =>  |sample| <= amplitude
        }
        return buffer;
    }
}
