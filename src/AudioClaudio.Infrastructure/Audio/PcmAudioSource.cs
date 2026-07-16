using System.Collections.Generic;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;

namespace AudioClaudio.Infrastructure.Audio;

/// <summary>
/// Production twin of the test-only <c>InMemoryAudioSource</c>: wraps a raw mono PCM buffer that
/// already lives in memory (e.g. a stem produced by an <see cref="ISourceSeparator"/>, with no file
/// or device to read from) and frames it via the Domain splitter.
/// </summary>
public sealed class PcmAudioSource : IAudioSource
{
    private readonly IReadOnlyList<Frame> _frames;

    public IEnumerable<Frame> Frames => _frames;

    public PcmAudioSource(float[] samples, SampleRate rate, FrameParameters parameters)
    {
        _frames = Framing.Split(samples, rate, parameters);
    }
}
