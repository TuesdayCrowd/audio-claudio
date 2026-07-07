using System;

namespace AudioClaudio.Domain.Spectral;

/// <summary>
/// The Hann window. This is the single place a window is defined for the pipeline (R3.1);
/// only <see cref="SpectralFrontEnd"/> applies it. Pure, BCL-only.
/// </summary>
public static class HannWindow
{
    /// <summary>
    /// Symmetric Hann coefficients <c>w[n] = 0.5·(1 − cos(2π·n/(N−1)))</c> for <c>n = 0..N−1</c>.
    /// Zero at both endpoints; unit-length special case returns <c>{1}</c>.
    /// </summary>
    /// <param name="size">Window length N in samples; must be positive.</param>
    public static double[] Coefficients(int size)
    {
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), size, "Window size must be positive.");

        var w = new double[size];
        if (size == 1)
        {
            w[0] = 1.0;
            return w;
        }

        double denom = size - 1;
        for (int n = 0; n < size; n++)
            w[n] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / denom));
        return w;
    }
}
