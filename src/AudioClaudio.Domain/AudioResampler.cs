namespace AudioClaudio.Domain;

/// <summary>
/// Band-limited sample-rate conversion by windowed-sinc (Lanczos) interpolation — used to bring
/// arbitrary-rate audio to the 22 050 Hz the Basic Pitch model expects. On downsampling the sinc
/// is widened to the OUTPUT Nyquist so frequencies that would alias are filtered out first; weights
/// are normalised so a constant passes through unchanged. Pure and deterministic: same input, same
/// output. Time stays a display concept only — this operates on raw sample buffers, not positions.
/// </summary>
public static class AudioResampler
{
    /// <summary>Resamples <paramref name="input"/> from <paramref name="inRate"/> to <paramref name="outRate"/> Hz.</summary>
    /// <param name="lobes">Lanczos window half-width (taps ≈ 2·lobes per output on upsampling); 4 is a good default.</param>
    public static float[] Resample(float[] input, int inRate, int outRate, int lobes = 4)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (inRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inRate), inRate, "Sample rate must be positive.");
        }

        if (outRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outRate), outRate, "Sample rate must be positive.");
        }

        if (inRate == outRate)
        {
            return (float[])input.Clone();
        }

        double ratio = (double)outRate / inRate;
        int outLength = (int)System.Math.Round(input.Length * ratio);
        var output = new float[outLength];

        // On downsampling (ratio < 1) widen the kernel to the lower Nyquist to anti-alias.
        double filterScale = System.Math.Min(1.0, ratio);
        double support = lobes / filterScale;

        for (int i = 0; i < outLength; i++)
        {
            double center = i / ratio; // corresponding position in the input, in input samples
            int left = (int)System.Math.Ceiling(center - support);
            int right = (int)System.Math.Floor(center + support);

            double sum = 0.0;
            double weightSum = 0.0;
            for (int j = left; j <= right; j++)
            {
                double weight = Lanczos((center - j) * filterScale, lobes);
                if (weight == 0.0)
                {
                    continue;
                }

                double sample = j >= 0 && j < input.Length ? input[j] : 0.0;
                sum += sample * weight;
                weightSum += weight;
            }

            output[i] = weightSum != 0.0 ? (float)(sum / weightSum) : 0f;
        }

        return output;
    }

    // Lanczos kernel: sinc(x)·sinc(x/lobes) for |x| < lobes, else 0.
    private static double Lanczos(double x, int lobes)
    {
        if (x == 0.0)
        {
            return 1.0;
        }

        if (x <= -lobes || x >= lobes)
        {
            return 0.0;
        }

        double px = System.Math.PI * x;
        return lobes * System.Math.Sin(px) * System.Math.Sin(px / lobes) / (px * px);
    }
}
