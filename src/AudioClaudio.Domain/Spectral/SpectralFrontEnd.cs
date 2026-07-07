using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AudioClaudio.Domain.Spectral;

/// <summary>
/// The spectral front end: window a <see cref="Frame"/> with Hann (the single window
/// site — R3.1), forward-FFT it, and return the half-spectrum magnitudes (R3.2). Pure —
/// same frame in, same spectrum out; no I/O, no clock; the only state is immutable
/// configuration (the precomputed window and the injected transform) (R3.3).
/// </summary>
public sealed class SpectralFrontEnd
{
    private readonly IFourierTransform _fft;
    private readonly double[] _window;
    private readonly int _frameSize;

    /// <param name="frameSize">Analysis window length N; MUST be a power of two.</param>
    /// <param name="fft">The forward transform (the Step 3 design-decision seam; explicit dependency).</param>
    public SpectralFrontEnd(int frameSize, IFourierTransform fft)
    {
        ArgumentNullException.ThrowIfNull(fft);
        if (frameSize <= 0 || (frameSize & (frameSize - 1)) != 0)
            throw new ArgumentException($"Frame size must be a positive power of two, got {frameSize}.", nameof(frameSize));

        _frameSize = frameSize;
        _fft = fft;
        _window = HannWindow.Coefficients(frameSize); // computed once — the single window site
    }

    /// <summary>Window, transform, and take the magnitude half-spectrum of one frame.</summary>
    public MagnitudeSpectrum Analyze(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        IReadOnlyList<float> samples = frame.Samples;
        if (samples.Count != _frameSize)
            throw new ArgumentException(
                $"Frame length {samples.Count} does not match configured frame size {_frameSize}.", nameof(frame));

        var windowed = new double[_frameSize];
        for (int n = 0; n < _frameSize; n++)
            windowed[n] = samples[n] * _window[n];

        Complex[] spectrum = _fft.Forward(windowed);

        int bins = _frameSize / 2 + 1; // real-signal half-spectrum: DC through Nyquist
        var magnitudes = new double[bins];
        for (int k = 0; k < bins; k++)
            magnitudes[k] = spectrum[k].Magnitude;

        return new MagnitudeSpectrum(magnitudes, _frameSize, frame.Rate);
    }

    /// <summary>Analyze an ordered stream of frames, one spectrum each — frames in, spectra out (R3.3).</summary>
    public IEnumerable<MagnitudeSpectrum> Analyze(IEnumerable<Frame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        return frames.Select(Analyze);
    }
}
