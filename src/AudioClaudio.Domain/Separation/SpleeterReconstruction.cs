using System;
using System.Numerics;
using AudioClaudio.Domain.Spectral;

namespace AudioClaudio.Domain.Separation;

/// <summary>
/// Inverts <see cref="SpleeterStft"/> (Stage 1.1c) for one channel: per frame, mirror the
/// retained half-spectrum back to a full <see cref="SpleeterStft.FrameLength"/>-bin spectrum via
/// Hermitian symmetry (a real-signal rfft's defining property — the same one <see cref="Radix2Fft"/>
/// exploits in reverse), inverse-transform it (the injected <see cref="IFourierTransform"/>),
/// overlap-add the result with the periodic Hann SYNTHESIS window (same window as analysis — the
/// standard "squared-window" OLA recipe), apply Spleeter's constant
/// <see cref="WindowCompensationFactor"/> = 2/3 (not a per-sample COLA normalization array — at
/// frame_length/hop = 4x overlap the squared periodic-Hann OLA sum is itself already the constant
/// 1.5 in the interior, so a single global scalar undoes it), then truncate to
/// <c>originalLength</c> samples (<see cref="SpleeterStft"/> frames directly from sample 0 — no
/// leading zero pad; see that class's remarks on the MODEL_CARD deviation — so there is no
/// leading pad to crop here either). Pure and deterministic.
/// </summary>
public sealed class SpleeterReconstruction
{
    /// <summary>Spleeter's WINDOW_COMPENSATION_FACTOR: a fixed global scalar, not a per-sample
    /// normalization, applied after overlap-add with the squared periodic Hann window.</summary>
    public const double WindowCompensationFactor = 2.0 / 3.0;

    private readonly IFourierTransform _fft;
    private readonly double[] _window; // periodic Hann, length SpleeterStft.FrameLength

    /// <param name="fft">The inverse transform (injected, per the Step 3 seam).</param>
    public SpleeterReconstruction(IFourierTransform fft)
    {
        _fft = fft ?? throw new ArgumentNullException(nameof(fft));
        _window = PeriodicHannWindow.Coefficients(SpleeterStft.FrameLength);
    }

    /// <summary>
    /// Reconstructs <paramref name="originalLength"/> samples of one channel from its full
    /// (<see cref="SpleeterStft.FullBins"/>-bin) complex STFT — one <see cref="Complex"/>[] per
    /// analysis frame in order, as produced by <see cref="SpleeterStftResult.LeftStft"/> /
    /// <see cref="SpleeterStftResult.RightStft"/> (NOT the T=512-padded/partitioned magnitude,
    /// which has already lost phase and the bins above <see cref="SpleeterStft.CroppedBins"/>).
    /// </summary>
    public double[] Reconstruct(Complex[][] channelStft, int originalLength)
    {
        ArgumentNullException.ThrowIfNull(channelStft);
        if (originalLength < 0)
            throw new ArgumentOutOfRangeException(nameof(originalLength), originalLength, "Length must be >= 0.");

        const int frameLength = SpleeterStft.FrameLength;
        const int hop = SpleeterStft.Hop;
        int numFrames = channelStft.Length;

        int paddedLength = numFrames == 0 ? 0 : frameLength + (numFrames - 1) * hop;
        var padded = new double[Math.Max(paddedLength, originalLength)];

        var fullSpectrum = new Complex[frameLength];
        for (int f = 0; f < numFrames; f++)
        {
            Complex[] half = channelStft[f];
            if (half.Length != SpleeterStft.FullBins)
            {
                throw new ArgumentException(
                    $"Frame {f} has {half.Length} bins; expected {SpleeterStft.FullBins}.", nameof(channelStft));
            }

            MirrorToFull(half, fullSpectrum, frameLength);
            Complex[] timeDomain = _fft.Inverse(fullSpectrum);

            int start = f * hop;
            for (int n = 0; n < frameLength; n++)
                padded[start + n] += timeDomain[n].Real * _window[n];
        }

        var result = new double[originalLength];
        for (int i = 0; i < originalLength; i++)
        {
            result[i] = i < padded.Length ? padded[i] * WindowCompensationFactor : 0.0;
        }

        return result;
    }

    /// <summary>Rebuilds the full <paramref name="frameLength"/>-bin spectrum from the retained
    /// half-spectrum (bins 0..FullBins-1) via <c>X[N-k] = conj(X[k])</c> for a real time-domain
    /// signal — exactly what <see cref="SpleeterStft"/>'s forward transform already produced,
    /// recovered rather than re-derived.</summary>
    private static void MirrorToFull(Complex[] half, Complex[] full, int frameLength)
    {
        int fullBins = SpleeterStft.FullBins;
        for (int k = 0; k < fullBins; k++) full[k] = half[k];
        for (int k = 1; k < fullBins - 1; k++) full[frameLength - k] = Complex.Conjugate(half[k]);
    }
}
