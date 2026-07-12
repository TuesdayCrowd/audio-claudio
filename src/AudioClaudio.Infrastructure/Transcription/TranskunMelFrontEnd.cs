using System;
using System.Numerics;
using System.Threading.Tasks;
using AudioClaudio.Domain.Spectral;

namespace AudioClaudio.Infrastructure.Transcription;

/// <summary>
/// The Transkun mel front end in C# (v2 Stage 4b): mono audio → <c>featuresBatch</c> <c>[nFrame, nMels,
/// nWindows]</c>, the exact input the exported ONNX (<see cref="TranskunBuffers"/>) expects. Reproduces
/// transkun's <c>makeFrame</c> → per-segment gain-normalize → six analysis windows → <c>rfft(norm=ortho)</c>
/// → power → mel filterbank → log-normalize. <c>torch.fft.rfft</c> is not ONNX-exportable, which is why
/// this lives outside the model graph. Pure given its buffers and FFT; the FFT is injected
/// (<see cref="Domain.Spectral.Radix2Fft"/>). Cross-implementation float drift vs PyTorch is tiny after the
/// log-normalization (verified against the committed <c>ref3b</c> fixture).
/// </summary>
public sealed class TranskunMelFrontEnd
{
    private readonly TranskunBuffers _buffers;
    private readonly IFourierTransform _fft;
    private readonly int _win;
    private readonly int _hop;
    private readonly int _nMels;
    private readonly int _nWindows;
    private readonly int _rfftBins;
    private readonly double _eps;
    private readonly double _logEps;
    private readonly int[] _melFirst; // first nonzero rfft bin per mel filter
    private readonly int[] _melLast;  // last nonzero rfft bin per mel filter (inclusive)

    public TranskunMelFrontEnd(TranskunBuffers buffers, IFourierTransform fft)
    {
        _buffers = buffers ?? throw new ArgumentNullException(nameof(buffers));
        _fft = fft ?? throw new ArgumentNullException(nameof(fft));
        TranskunParams p = buffers.Params;
        _win = p.WindowSize;
        _hop = p.HopSize;
        _nMels = p.NMels;
        _nWindows = p.NWindows;
        _rfftBins = p.RfftBins;
        _eps = p.Eps;
        _logEps = Math.Log(_eps);

        // Mel filters are triangular — each spans a small contiguous band of rfft bins and is exactly zero
        // elsewhere. Precompute each filter's nonzero range so the mel matmul skips the zeros (exact, ~100×
        // faster than the dense 2049×229 product — the transcriber's hot loop).
        _melFirst = new int[_nMels];
        _melLast = new int[_nMels];
        for (int m = 0; m < _nMels; m++)
        {
            int first = -1, last = -1;
            for (int k = 0; k < _rfftBins; k++)
            {
                if (buffers.Freq2Mels[k * _nMels + m] != 0f)
                {
                    if (first < 0)
                    {
                        first = k;
                    }

                    last = k;
                }
            }

            _melFirst[m] = first < 0 ? 0 : first;
            _melLast[m] = last < 0 ? -1 : last;
        }
    }

    /// <summary>Compute <c>featuresBatch[nFrame, nMels, nWindows]</c> for one segment of mono audio.</summary>
    public float[,,] Compute(ReadOnlySpan<float> audio)
    {
        int n = audio.Length;

        // (1) makeFrame: nFrame = ceil(n/hop)+1, half-window left pad, right pad to the last full frame.
        int nFrame = (int)Math.Ceiling((double)n / _hop) + 1;
        int lPad = _win / 2;
        int rPad = (nFrame - 1) * _hop + _win / 2 - n;
        var padded = new double[lPad + n + rPad];
        for (int i = 0; i < n; i++)
        {
            padded[lPad + i] = audio[i];
        }

        // (2) gain normalization over ALL frame samples (nFrame*win, with overlap), unbiased std — this is
        // transkun's per-segment mean/std over the framed tensor, computed before windowing.
        long count = (long)nFrame * _win;
        double sum = 0.0;
        for (int f = 0; f < nFrame; f++)
        {
            int b = f * _hop;
            for (int j = 0; j < _win; j++)
            {
                sum += padded[b + j];
            }
        }

        double mean = sum / count;
        double sqSum = 0.0;
        for (int f = 0; f < nFrame; f++)
        {
            int b = f * _hop;
            for (int j = 0; j < _win; j++)
            {
                double d = padded[b + j] - mean;
                sqSum += d * d;
            }
        }

        double std = Math.Sqrt(sqSum / (count - 1)); // torch.std is unbiased by default
        double invStd = 1.0 / (std + 1e-8);

        // (3)–(5) per frame, per window: window → ortho rfft → power → mel → log-normalize.
        // Frames are independent (each writes only its own features[f,*,*] and the FFT is a pure function),
        // so this fans out across cores with thread-local scratch. The per-frame math is untouched, so the
        // result is bit-identical to the serial loop — determinism (§4) holds. This phase is otherwise the
        // single-threaded ~24% of transcription while the ONNX cores sit idle.
        var features = new float[nFrame, _nMels, _nWindows];
        double orthoSq = 1.0 / _win; // |rfft(norm="ortho")|^2 = |rfft(raw)|^2 / N
        double invNegLogEps = 1.0 / (-_logEps);
        float[] freq2mels = _buffers.Freq2Mels;

        Parallel.For(
            0,
            nFrame,
            () => (buf: new double[_win], power: new double[_rfftBins]),
            (f, _, scratch) =>
            {
                double[] buf = scratch.buf;
                double[] power = scratch.power;
                int b = f * _hop;
                for (int w = 0; w < _nWindows; w++)
                {
                    float[] window = _buffers.Windows[w];
                    for (int j = 0; j < _win; j++)
                    {
                        buf[j] = ((padded[b + j] - mean) * invStd) * window[j];
                    }

                    Complex[] spec = _fft.Forward(buf);
                    for (int k = 0; k < _rfftBins; k++)
                    {
                        Complex c = spec[k];
                        power[k] = (c.Real * c.Real + c.Imaginary * c.Imaginary) * orthoSq;
                    }

                    for (int m = 0; m < _nMels; m++)
                    {
                        double acc = 0.0;
                        int last = _melLast[m]; // freq2mels is [rfftBins, nMels] row-major → index k*nMels + m
                        for (int k = _melFirst[m]; k <= last; k++)
                        {
                            acc += power[k] * freq2mels[k * _nMels + m];
                        }

                        features[f, m, w] = (float)((Math.Log(acc + _eps) - _logEps) * invNegLogEps);
                    }
                }

                return scratch;
            },
            _ => { });

        return features;
    }
}
