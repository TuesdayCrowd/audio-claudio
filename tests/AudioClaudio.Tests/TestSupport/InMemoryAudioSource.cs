using System.Collections.Generic;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.TestSupport;

/// <summary>Trivial in-memory <see cref="IAudioSource"/>: frames a buffer via the Domain splitter.</summary>
public sealed class InMemoryAudioSource : IAudioSource
{
    private readonly IReadOnlyList<Frame> _frames;

    public IEnumerable<Frame> Frames => _frames;

    public InMemoryAudioSource(float[] samples, SampleRate rate, FrameParameters parameters)
    {
        _frames = Framing.Split(samples, rate, parameters);
    }
}
