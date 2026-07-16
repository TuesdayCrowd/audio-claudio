using System;

namespace AudioClaudio.Domain.Spectral;

/// <summary>
/// The periodic (a.k.a. DFT-even) Hann window: <c>w[n] = 0.5 − 0.5·cos(2π·n/N)</c> for
/// <c>n = 0..N−1</c> — note the denominator is <c>N</c>, not <c>N−1</c>. That N−1 form is
/// <see cref="HannWindow"/>, the symmetric variant this pipeline's monophonic front end uses
/// (R3.1); it stays untouched. STFT/iSTFT round-trips (e.g. Spleeter's separation front end,
/// Stage 1.1b) need this periodic form instead — the standard choice whenever the same window
/// is applied on both analysis and synthesis for overlap-add perfect reconstruction (it is what
/// librosa's <c>periodic=True</c> and TensorFlow's periodic Hann compute).
/// </summary>
public static class PeriodicHannWindow
{
    /// <param name="size">Window length N in samples; must be positive.</param>
    public static double[] Coefficients(int size)
    {
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), size, "Window size must be positive.");

        var w = new double[size];
        if (size == 1)
        {
            // The pure formula gives 0 here (cos(0) = 1 regardless of denominator), which would
            // be a useless all-zero single-sample window; mirror HannWindow's degenerate case.
            w[0] = 1.0;
            return w;
        }

        for (int n = 0; n < size; n++)
            w[n] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * n / size);
        return w;
    }
}
