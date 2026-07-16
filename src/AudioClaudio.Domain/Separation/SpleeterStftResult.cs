using System.Numerics;

namespace AudioClaudio.Domain.Separation;

/// <summary>
/// The output of <see cref="SpleeterStft.Analyze"/>: the net's cropped/partitioned magnitude
/// input, plus each channel's full (uncropped, unpartitioned) complex STFT — the latter is what
/// <see cref="SpleeterReconstruction"/> needs (mixture phase reuse + full-bandwidth iSTFT).
/// Immutable data holder; no behavior beyond carrying these together.
/// </summary>
public sealed class SpleeterStftResult
{
    public SpleeterStftResult(double[,,,] croppedMagnitude, Complex[][] leftStft, Complex[][] rightStft, int frameCount)
    {
        CroppedMagnitude = croppedMagnitude;
        LeftStft = leftStft;
        RightStft = rightStft;
        FrameCount = frameCount;
    }

    /// <summary>The net's input: <c>[num_splits, T=512, F=1024, C=2]</c>.</summary>
    public double[,,,] CroppedMagnitude { get; }

    /// <summary>Left channel's full (<see cref="SpleeterStft.FullBins"/>-bin) complex STFT, one
    /// entry per raw analysis frame, in order — NOT zero-padded to a <see cref="SpleeterStft.SegmentFrames"/>
    /// multiple (that padding exists only in <see cref="CroppedMagnitude"/>, for the net's fixed shape).</summary>
    public Complex[][] LeftStft { get; }

    /// <summary>Right channel's full complex STFT; see <see cref="LeftStft"/>.</summary>
    public Complex[][] RightStft { get; }

    /// <summary>Raw frame count before zero-padding to a <see cref="SpleeterStft.SegmentFrames"/>
    /// multiple — the length of both <see cref="LeftStft"/> and <see cref="RightStft"/>.</summary>
    public int FrameCount { get; }
}
