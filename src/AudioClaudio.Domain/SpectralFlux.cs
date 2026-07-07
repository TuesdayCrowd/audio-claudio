namespace AudioClaudio.Domain;

/// <summary>
/// The onset novelty function (R5.1): the half-wave-rectified frame-to-frame
/// increase in spectral magnitude. A note attack injects energy across many bins
/// at once, producing a positive spike; steady sustain and decay produce ~zero.
/// Pure and deterministic — no state, no I/O, no clock.
/// </summary>
public static class SpectralFlux
{
    /// <summary>
    /// Computes the spectral-flux novelty for a sequence of per-frame magnitude
    /// spectra. The result has one value per frame. Index 0 is measured against an
    /// implicit all-zero "previous" frame, so a note that starts in frame 0 still
    /// registers an onset. Only positive changes count (half-wave rectification):
    /// flux[m] = Σ_k max(0, |X_m[k]| − |X_{m-1}[k]|). Magnitudes are non-negative
    /// linear FFT magnitudes; the units cancel because peak-picking normalizes later.
    /// </summary>
    public static double[] Compute(IReadOnlyList<IReadOnlyList<double>> magnitudeSpectra)
    {
        ArgumentNullException.ThrowIfNull(magnitudeSpectra);

        int frameCount = magnitudeSpectra.Count;
        var novelty = new double[frameCount];

        for (int m = 0; m < frameCount; m++)
        {
            IReadOnlyList<double> current = magnitudeSpectra[m];
            IReadOnlyList<double>? previous = m > 0 ? magnitudeSpectra[m - 1] : null;

            double sum = 0.0;
            for (int k = 0; k < current.Count; k++)
            {
                double prev = previous is not null && k < previous.Count ? previous[k] : 0.0;
                double diff = current[k] - prev;
                if (diff > 0.0)
                {
                    sum += diff;
                }
            }

            novelty[m] = sum;
        }

        return novelty;
    }
}
