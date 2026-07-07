namespace AudioClaudio.Domain;

/// <summary>
/// Detects note onsets from a spectral-flux novelty curve (R5.1). Pure and
/// deterministic — no state, no I/O, no clock.
/// </summary>
public sealed class OnsetDetector
{
    private readonly OnsetDetectorOptions _options;

    public OnsetDetector()
        : this(new OnsetDetectorOptions())
    {
    }

    public OnsetDetector(OnsetDetectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Picks onset frame indices from a novelty curve. The curve is first normalized
    /// by its maximum, so the thresholds are independent of the FFT's magnitude scale.
    /// A frame is an onset iff it is a local maximum within LocalMaxRadiusFrames,
    /// exceeds the adaptive threshold (Multiplier · localMean + Delta), and is at least
    /// MinGapFrames after the previously accepted onset. Left ties lose (a plateau's
    /// first frame is chosen), so results are deterministic (non-negotiable 3).
    /// </summary>
    public IReadOnlyList<int> PickPeaks(IReadOnlyList<double> novelty)
    {
        ArgumentNullException.ThrowIfNull(novelty);

        int n = novelty.Count;
        var onsets = new List<int>();
        if (n == 0)
        {
            return onsets;
        }

        double max = 0.0;
        for (int i = 0; i < n; i++)
        {
            if (novelty[i] > max)
            {
                max = novelty[i];
            }
        }
        if (max <= 0.0)
        {
            return onsets;   // pure silence / no change → no onsets
        }

        int r = Math.Max(0, _options.LocalMaxRadiusFrames);
        int w = Math.Max(1, _options.ThresholdWindowFrames);
        int? lastAccepted = null;   // no previous onset yet; avoids int.MinValue sentinel-subtraction overflow

        for (int m = 0; m < n; m++)
        {
            double value = novelty[m] / max;

            // Local-maximum test: strictly greater than left neighbours, >= right
            // neighbours (so the first frame of a plateau wins).
            bool isLocalMax = true;
            for (int j = m - r; j <= m + r && isLocalMax; j++)
            {
                if (j < 0 || j >= n || j == m)
                {
                    continue;
                }
                double neighbour = novelty[j] / max;
                if (j < m && value <= neighbour)
                {
                    isLocalMax = false;
                }
                else if (j > m && value < neighbour)
                {
                    isLocalMax = false;
                }
            }
            if (!isLocalMax)
            {
                continue;
            }

            // Adaptive threshold from the local mean of the normalized novelty.
            double sum = 0.0;
            int count = 0;
            for (int j = m - w; j <= m + w; j++)
            {
                if (j < 0 || j >= n)
                {
                    continue;
                }
                sum += novelty[j] / max;
                count++;
            }
            double localMean = count > 0 ? sum / count : 0.0;
            double threshold = (_options.ThresholdMultiplier * localMean) + _options.ThresholdDelta;
            if (value < threshold)
            {
                continue;
            }

            if (lastAccepted is int last && m - last < _options.MinGapFrames)
            {
                continue;
            }

            onsets.Add(m);
            lastAccepted = m;
        }

        return onsets;
    }

    /// <summary>
    /// Detects onsets from per-frame magnitude spectra: computes the spectral-flux
    /// novelty (<see cref="SpectralFlux"/>) then picks peaks. Returns frame indices.
    /// </summary>
    public IReadOnlyList<int> Detect(IReadOnlyList<IReadOnlyList<double>> magnitudeSpectra)
    {
        ArgumentNullException.ThrowIfNull(magnitudeSpectra);
        double[] novelty = SpectralFlux.Compute(magnitudeSpectra);
        return PickPeaks(novelty);
    }

    /// <summary>
    /// Detects onsets and expresses each as the starting <see cref="SamplePosition"/>
    /// of its frame (the R5.1 contract). <paramref name="frameStarts"/> must be parallel
    /// to <paramref name="magnitudeSpectra"/> (one start per frame).
    /// </summary>
    public IReadOnlyList<SamplePosition> DetectOnsetPositions(
        IReadOnlyList<IReadOnlyList<double>> magnitudeSpectra,
        IReadOnlyList<SamplePosition> frameStarts)
    {
        ArgumentNullException.ThrowIfNull(frameStarts);

        IReadOnlyList<int> frames = Detect(magnitudeSpectra);
        var positions = new List<SamplePosition>(frames.Count);
        foreach (int f in frames)
        {
            positions.Add(frameStarts[f]);
        }
        return positions;
    }
}
