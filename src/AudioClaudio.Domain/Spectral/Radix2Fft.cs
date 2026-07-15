using System;
using System.Numerics;

namespace AudioClaudio.Domain.Spectral;

/// <summary>
/// Hand-rolled iterative radix-2 Cooley–Tukey FFT over <see cref="Complex"/> (BCL only).
/// Forward transform, <c>O(N·log N)</c>; input length must be a power of two.
/// </summary>
public sealed class Radix2Fft : IFourierTransform
{
    public Complex[] Forward(double[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        int n = samples.Length;
        if (n == 0 || (n & (n - 1)) != 0)
            throw new ArgumentException($"FFT length must be a positive power of two, got {n}.", nameof(samples));

        var a = new Complex[n];
        for (int i = 0; i < n; i++) a[i] = new Complex(samples[i], 0.0);

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }

        // Butterflies. wlen = exp(-2πi/len) is the forward-transform twiddle.
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2.0 * Math.PI / len;
            var wlen = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                Complex w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    Complex u = a[i + k];
                    Complex v = a[i + k + len / 2] * w;
                    a[i + k] = u + v;
                    a[i + k + len / 2] = u - v;
                    w *= wlen;
                }
            }
        }

        return a;
    }

    public Complex[] Inverse(Complex[] spectrum)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        int n = spectrum.Length;
        if (n == 0 || (n & (n - 1)) != 0)
            throw new ArgumentException($"IFFT length must be a positive power of two, got {n}.", nameof(spectrum));

        // Conjugate trick: ifft(X) = conj(fft(conj(X))) / N. The DFT is linear over ℂ,
        // and Forward already computes fft of a real array (its imaginary part zero),
        // so fft(conj(X)) = fft(Re(X)) − i·fft(Im(X)) — two real-input Forward calls,
        // no separate complex-input FFT engine needed.
        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < n; i++)
        {
            re[i] = spectrum[i].Real;
            im[i] = spectrum[i].Imaginary;
        }

        Complex[] fftRe = Forward(re);
        Complex[] fftIm = Forward(im);

        var result = new Complex[n];
        for (int k = 0; k < n; k++)
        {
            Complex fftConjX = fftRe[k] - Complex.ImaginaryOne * fftIm[k];
            result[k] = Complex.Conjugate(fftConjX) / n;
        }

        return result;
    }
}
