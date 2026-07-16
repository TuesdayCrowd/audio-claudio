using System;
using System.Numerics;
using AudioClaudio.Domain.Spectral;

namespace AudioClaudio.Domain.Separation;

/// <summary>
/// Reproduces Deezer Spleeter's exact STFT analysis (Stage 1.1b), NOT the librosa-centered
/// convention: 44100 Hz, n_fft=frame_length=4096, hop=frame_step=1024, periodic Hann,
/// <c>pad_end</c> zero-padding of the final partial frame, rfft to <see cref="FullBins"/>=2049
/// bins, magnitude cropped to the first <see cref="CroppedBins"/>=1024 bins (Spleeter's ~11 kHz
/// ceiling — a property of the trained weights, not a choice made here), and the time axis
/// zero-padded up to a multiple of <see cref="SegmentFrames"/>=512 and partitioned into
/// <c>[num_splits, 512, 1024, 2]</c> — the exact shape the 5 per-branch ONNX models consume.
/// Stereo only (2-channel); mono is upmixed L=R by the caller. The full (uncropped,
/// unpartitioned) complex STFT is retained per channel too, for
/// <see cref="SpleeterReconstruction"/>. Pure and deterministic: waveform in, magnitude +
/// complex STFT out; the FFT is injected (the Step 3 design-decision seam), never hardcoded.
///
/// <para><b>Deviation from the MODEL_CARD.md spec, resolved against the committed golden (the
/// stated arbiter):</b> the MODEL_CARD describes prepending a full <c>frame_length</c> (4096)
/// zero pad before framing. Empirically, that produces a magnitude spectrogram whose first
/// frames are pure analysis-window artifacts of the zero pad, offset by exactly 4 frames (one
/// frame_length / hop) from the committed <c>golden/magnitude_nhwc.f32</c> — max abs diff ~418
/// against a signal whose peak magnitude is ~448, i.e. wildly wrong. Framing directly from
/// sample 0 (no leading pad at all, otherwise identical: periodic Hann, hop 1024, pad_end)
/// matches the golden to ~1.6e-3 max abs / ~2.5e-5 RMS — float32-precision-level agreement
/// (TF's float32 STFT vs this class's double-precision FFT). That is what this class does.
/// </para>
/// </summary>
public sealed class SpleeterStft
{
    /// <summary>n_fft / frame_length.</summary>
    public const int FrameLength = 4096;

    /// <summary>hop / frame_step.</summary>
    public const int Hop = 1024;

    /// <summary>rfft half-spectrum bin count, DC through Nyquist: <c>FrameLength/2 + 1</c>.</summary>
    public const int FullBins = FrameLength / 2 + 1;

    /// <summary>F — the net's cropped bin count (~11.025 kHz ceiling).</summary>
    public const int CroppedBins = 1024;

    /// <summary>T — the net's fixed per-segment time-frame count.</summary>
    public const int SegmentFrames = 512;

    /// <summary>Spleeter is stereo-only; mono callers upmix L=R first.</summary>
    public const int Channels = 2;

    private readonly IFourierTransform _fft;
    private readonly double[] _window; // periodic Hann, length FrameLength

    /// <param name="fft">The forward transform (injected, per the Step 3 seam). Must accept
    /// power-of-two lengths; <see cref="FrameLength"/> (4096) qualifies.</param>
    public SpleeterStft(IFourierTransform fft)
    {
        _fft = fft ?? throw new ArgumentNullException(nameof(fft));
        _window = PeriodicHannWindow.Coefficients(FrameLength);
    }

    /// <summary>
    /// Analyzes one stereo waveform. <paramref name="left"/> and <paramref name="right"/> MUST
    /// have equal length (mono input is upmixed to identical L/R buffers by the caller).
    /// </summary>
    public SpleeterStftResult Analyze(double[] left, double[] right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Length != right.Length)
        {
            throw new ArgumentException(
                $"Channels must have equal length (left: {left.Length}, right: {right.Length}).", nameof(right));
        }

        Complex[][] leftStft = ComputeFullStft(left);
        Complex[][] rightStft = ComputeFullStft(right);

        int numFrames = leftStft.Length; // == rightStft.Length, same length input
        int numSplits = (numFrames + SegmentFrames - 1) / SegmentFrames;

        var magnitude = new double[numSplits, SegmentFrames, CroppedBins, Channels];
        FillMagnitude(magnitude, leftStft, channel: 0);
        FillMagnitude(magnitude, rightStft, channel: 1);

        return new SpleeterStftResult(magnitude, leftStft, rightStft, numFrames);
    }

    /// <summary>
    /// The full (2049-bin) complex STFT of one channel: frame directly from sample 0 at
    /// <see cref="Hop"/> (start &lt; channel length, zero-padding a short final frame —
    /// <c>pad_end</c>; no leading zero pad — see the class remarks), window, and
    /// forward-transform each frame.
    /// </summary>
    private Complex[][] ComputeFullStft(double[] channel)
    {
        int numFrames = 0;
        for (int start = 0; start < channel.Length; start += Hop) numFrames++;

        var frames = new Complex[numFrames][];
        var windowed = new double[FrameLength];
        int idx = 0;
        for (int start = 0; start < channel.Length; start += Hop, idx++)
        {
            int count = Math.Min(FrameLength, channel.Length - start);
            for (int n = 0; n < count; n++) windowed[n] = channel[start + n] * _window[n];
            for (int n = count; n < FrameLength; n++) windowed[n] = 0.0; // pad_end for the final partial frame

            Complex[] spectrum = _fft.Forward(windowed);
            var half = new Complex[FullBins];
            Array.Copy(spectrum, half, FullBins);
            frames[idx] = half;
        }

        return frames;
    }

    private static void FillMagnitude(double[,,,] magnitude, Complex[][] stft, int channel)
    {
        int numFrames = stft.Length;
        for (int f = 0; f < numFrames; f++)
        {
            int split = f / SegmentFrames;
            int t = f % SegmentFrames;
            Complex[] bins = stft[f];
            for (int k = 0; k < CroppedBins; k++)
                magnitude[split, t, k, channel] = bins[k].Magnitude;
        }
        // Frames beyond numFrames up to the next SegmentFrames boundary stay at the array's
        // default 0.0 — the time-axis zero pad up to a multiple of T=512.
    }
}
