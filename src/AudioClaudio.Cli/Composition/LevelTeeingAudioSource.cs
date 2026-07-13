using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;

namespace AudioClaudio.Cli.Composition;

/// <summary>
/// Tees an RMS level from each frame the listen loop consumes, invoking <c>onLevel</c> as a side
/// channel while passing every frame through unchanged. Lives in the CLI composition root, NOT in
/// Infrastructure.Audio -- PortAudioAudioSource stays free of any transcription/analysis logic
/// (R10.4).
/// </summary>
public sealed class LevelTeeingAudioSource : IAudioSource
{
    private readonly IAudioSource _inner;
    private readonly Action<double> _onLevel;

    public LevelTeeingAudioSource(IAudioSource inner, Action<double> onLevel)
    {
        _inner = inner;
        _onLevel = onLevel;
    }

    public IEnumerable<Frame> Frames
    {
        get
        {
            foreach (Frame frame in _inner.Frames)
            {
                _onLevel(AudioLevel.Rms(frame.Samples));
                yield return frame;
            }
        }
    }
}
