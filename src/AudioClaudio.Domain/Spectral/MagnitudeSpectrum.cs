using System;
using System.Collections.Generic;

namespace AudioClaudio.Domain.Spectral;

/// <summary>
/// A per-frame magnitude spectrum: bins <c>0..N/2</c> inclusive (the real-signal
/// half-spectrum, DC through Nyquist), carrying the <see cref="SampleRate"/> and
/// frame size N so bins map to Hz. Immutable; equal inputs produce equal spectra
/// (determinism, non-negotiable #3).
/// </summary>
public sealed class MagnitudeSpectrum
{
    private readonly double[] _magnitudes;

    /// <param name="magnitudes">Bin magnitudes; length MUST equal <c>frameSize/2 + 1</c>.</param>
    /// <param name="frameSize">Analysis window length N (samples); a power of two.</param>
    /// <param name="rate">The sample rate the frame was captured at.</param>
    public MagnitudeSpectrum(double[] magnitudes, int frameSize, SampleRate rate)
    {
        ArgumentNullException.ThrowIfNull(magnitudes);
        int expected = frameSize / 2 + 1;
        if (magnitudes.Length != expected)
            throw new ArgumentException(
                $"Expected {expected} bins for frame size {frameSize}, got {magnitudes.Length}.", nameof(magnitudes));

        FrameSize = frameSize;
        Rate = rate;
        _magnitudes = (double[])magnitudes.Clone(); // defensive copy — immutability
    }

    /// <summary>Analysis window length N in samples.</summary>
    public int FrameSize { get; }

    /// <summary>The sample rate the source frame was captured at.</summary>
    public SampleRate Rate { get; }

    /// <summary>Number of bins, <c>N/2 + 1</c> (DC through Nyquist).</summary>
    public int BinCount => _magnitudes.Length;

    /// <summary>Magnitude |X[k]| of bin k.</summary>
    public double this[int bin] => _magnitudes[bin];

    /// <summary>Read-only view of all bin magnitudes.</summary>
    public IReadOnlyList<double> Magnitudes => _magnitudes;

    /// <summary>Centre frequency of bin k in Hz: <c>k · sampleRate / N</c> (the only Hz conversion — a display-edge concern).</summary>
    public double FrequencyOf(int bin) => (double)bin * Rate.Hz / FrameSize;

    /// <summary>Index of the largest-magnitude bin. Ties resolve to the lowest bin (defined tie-break, non-negotiable #3).</summary>
    public int PeakBin()
    {
        int peak = 0;
        for (int k = 1; k < _magnitudes.Length; k++)
            if (_magnitudes[k] > _magnitudes[peak]) peak = k;
        return peak;
    }
}
