using System.Numerics;

namespace AudioClaudio.Domain.Spectral;

/// <summary>
/// A forward Discrete Fourier Transform of real-valued samples. This is the Step 3
/// design-decision seam: the hand-rolled <c>Radix2Fft</c> and any library-backed
/// implementation both satisfy it, so the pipeline depends only on this BCL-only
/// abstraction (keeps <c>AudioClaudio.Domain</c> dependency-free per R0.2).
/// </summary>
public interface IFourierTransform
{
    /// <summary>
    /// Forward DFT. <paramref name="samples"/> length MUST be a power of two, else
    /// <see cref="System.ArgumentException"/>. Returns the full complex spectrum of the
    /// same length (bins 0..N−1); callers keep the 0..N/2 half for real signals.
    /// </summary>
    Complex[] Forward(double[] samples);
}
