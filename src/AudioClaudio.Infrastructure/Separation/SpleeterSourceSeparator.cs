using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Separation;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Infrastructure.Audio;

namespace AudioClaudio.Infrastructure.Separation;

/// <summary>
/// The Spleeter 5-stem <see cref="ISourceSeparator"/> adapter: wires the already-built pieces
/// together end to end (per <c>MODEL_CARD.md</c>'s reconstruction recipe) —
/// <see cref="Framing.ReconstructMono"/> + <see cref="AudioResampler"/> to get 44100 Hz mono,
/// upmix to fake stereo (L=R — Spleeter is stereo-only; the repo's mono <see cref="IAudioSource"/>
/// contract is an accepted ceiling here, see <c>DECISIONS.md</c> "Source separation"), <see cref="SpleeterStft"/>
/// for the magnitude + retained complex mixture STFT, <see cref="SpleeterModel"/> for the 5 raw
/// logits, <see cref="SpleeterMasking"/> for the cross-branch softmax and power-ratio remask (the
/// two non-learned steps lifted out of the ONNX graph), and <see cref="SpleeterReconstruction"/> to
/// turn each stem's masked spectrum back into mono PCM wrapped in a <see cref="PcmAudioSource"/>.
/// </summary>
public sealed class SpleeterSourceSeparator : ISourceSeparator, IDisposable
{
    private const int TargetSampleRateHz = 44100;

    // The frame parameters each stem's PcmAudioSource is framed with; a sane general-purpose choice
    // (matches the STFT's own frame_length/hop, so a stem could be re-analyzed without a mismatch).
    private static readonly FrameParameters StemFrameParameters = new(4096, 1024);

    private readonly SpleeterModel _model;
    private readonly SpleeterStft _stft;
    private readonly SpleeterReconstruction _reconstruction;

    public SpleeterSourceSeparator(string modelDir, IFourierTransform fft)
    {
        ArgumentNullException.ThrowIfNull(modelDir);
        ArgumentNullException.ThrowIfNull(fft);
        _model = new SpleeterModel(modelDir);
        _stft = new SpleeterStft(fft);
        _reconstruction = new SpleeterReconstruction(fft);
    }

    public IReadOnlyList<SeparatedStem> Separate(IAudioSource mix)
    {
        ArgumentNullException.ThrowIfNull(mix);

        List<Frame> frames = mix.Frames.ToList();
        float[] mono = Framing.ReconstructMono(frames);
        if (frames.Count > 0)
        {
            int sourceRate = frames[0].Rate.Hz;
            if (sourceRate != TargetSampleRateHz)
            {
                mono = AudioResampler.Resample(mono, sourceRate, TargetSampleRateHz);
            }
        }

        var rate44 = new SampleRate(TargetSampleRateHz);
        if (mono.Length == 0)
        {
            return SpleeterModel.StemNames
                .Select(name => new SeparatedStem(
                    name, new PcmAudioSource(Array.Empty<float>(), rate44, StemFrameParameters)))
                .ToArray();
        }

        var monoDouble = new double[mono.Length];
        for (int i = 0; i < mono.Length; i++) monoDouble[i] = mono[i];

        // Spleeter is stereo-only; a mono source is upmixed L=R (the accepted mono ceiling above).
        SpleeterStftResult stftResult = _stft.Analyze(monoDouble, monoDouble);

        float[,,,] magnitudeFloat = ToFloat(stftResult.CroppedMagnitude);
        IReadOnlyList<float[,,,]> logits = _model.RunLogits(magnitudeFloat);

        IReadOnlyList<double[,,,]> estimated = SpleeterMasking.Softmax(logits, stftResult.CroppedMagnitude);
        IReadOnlyList<double[,,,]> ratio = SpleeterMasking.RatioMask(estimated);

        int frameCount = stftResult.FrameCount;
        var stems = new List<SeparatedStem>(SpleeterModel.StemNames.Count);
        for (int k = 0; k < SpleeterModel.StemNames.Count; k++)
        {
            // Because the mixture was upmixed L=R, channel 0 and channel 1 of every intermediate
            // (magnitude, logits, estimated, ratio) are identical -- reconstruct from channel 0 only
            // and reuse it as the mono stem, per MODEL_CARD.md's reconstruction recipe.
            Complex[][] maskedSpectrum = BuildMaskedSpectrum(ratio[k], stftResult.LeftStft, frameCount);
            double[] stemMono = _reconstruction.Reconstruct(maskedSpectrum, monoDouble.Length);

            var stemFloat = new float[stemMono.Length];
            for (int i = 0; i < stemMono.Length; i++) stemFloat[i] = (float)stemMono[i];

            var source = new PcmAudioSource(stemFloat, rate44, StemFrameParameters);
            stems.Add(new SeparatedStem(SpleeterModel.StemNames[k], source));
        }

        return stems;
    }

    // Builds one stem's full (SpleeterStft.FullBins-bin) complex spectrum per analysis frame: the
    // ratio mask is zero-extended from F=1024 up to the retained 2049 bins (bins 1024..2048 = 0 --
    // Spleeter's ~11 kHz ceiling, a weight property) and multiplied elementwise by the mixture's
    // complex STFT (reusing the mixture's phase for every stem, per Spleeter's own reconstruction).
    private static Complex[][] BuildMaskedSpectrum(double[,,,] ratio, Complex[][] mixtureStft, int frameCount)
    {
        var masked = new Complex[frameCount][];
        for (int frame = 0; frame < frameCount; frame++)
        {
            int split = frame / SpleeterStft.SegmentFrames;
            int t = frame % SpleeterStft.SegmentFrames;
            Complex[] mixtureBins = mixtureStft[frame];

            var bins = new Complex[SpleeterStft.FullBins];
            for (int bin = 0; bin < SpleeterStft.FullBins; bin++)
            {
                // Channel 0 only -- see the "identical L=R" remark in Separate. Zero-extend the F=1024
                // ratio mask up to the retained 2049 bins for bins the net never scored.
                double r = bin < SpleeterStft.CroppedBins ? ratio[split, t, bin, 0] : 0.0;
                bins[bin] = mixtureBins[bin] * r;
            }

            masked[frame] = bins;
        }

        return masked;
    }

    private static float[,,,] ToFloat(double[,,,] source)
    {
        int d0 = source.GetLength(0), d1 = source.GetLength(1), d2 = source.GetLength(2), d3 = source.GetLength(3);
        var result = new float[d0, d1, d2, d3];
        for (int a = 0; a < d0; a++)
            for (int b = 0; b < d1; b++)
                for (int c = 0; c < d2; c++)
                    for (int d = 0; d < d3; d++)
                        result[a, b, c, d] = (float)source[a, b, c, d];
        return result;
    }

    public void Dispose() => _model.Dispose();
}
