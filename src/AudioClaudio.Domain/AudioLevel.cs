namespace AudioClaudio.Domain;

/// <summary>
/// Root-mean-square amplitude of a block of samples -- the standard loudness proxy behind a
/// VU-style meter. Pure math, no I/O, no device (R1.5).
/// </summary>
public static class AudioLevel
{
    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return 0.0;

        double sumOfSquares = 0.0;
        foreach (float sample in samples)
        {
            sumOfSquares += (double)sample * sample;
        }

        return Math.Sqrt(sumOfSquares / samples.Length);
    }
}
