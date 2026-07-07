using System.Threading;
using System.Threading.Channels;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;

namespace AudioClaudio.Infrastructure.Capture;

/// <summary>
/// Device-independent core of live capture. Downmixes interleaved PCM blocks to
/// mono, reframes them via <see cref="FrameAccumulator"/>, and bridges the
/// real-time producer thread to the pull-based pipeline through a bounded
/// channel exposed as the <see cref="Frames"/> property (the <c>IAudioSource</c>
/// pull contract). Contains NO transcription logic (R10.4): this class only
/// downmixes and reframes. The audio thread calls <see cref="Submit"/> only; it
/// never blocks or allocates on the steady-state hot path, and a full buffer is
/// reported via <see cref="DroppedFrameCount"/> rather than swallowed. On
/// <see cref="Complete"/> the trailing (possibly &lt; frameSize) tail is flushed
/// as zero-padded frame(s), so it is never silently discarded (R10.1: identical
/// to <c>WavAudioSource</c>'s own end-of-file convention).
/// </summary>
public sealed class CaptureFrameStream : IAudioSource
{
    private readonly FrameAccumulator _accumulator;
    private readonly Channel<Frame> _channel;
    private readonly List<Frame> _emitted = new();
    private float[] _mono = new float[4096];
    private long _dropped;

    public SampleRate SampleRate { get; }

    public CaptureFrameStream(int frameSize, int hop, SampleRate rate, int channelCapacity)
    {
        SampleRate = rate;
        _accumulator = new FrameAccumulator(frameSize, hop, rate);

        // FullMode is deliberately Wait (also the default), NOT dead here, and NOT
        // interchangeable with the Drop* modes despite only ever calling the
        // synchronous, non-blocking TryWrite (never WriteAsync): verified empirically
        // (a throwaway BoundedChannel<int> probe) that FullMode governs TryWrite's
        // behavior too, not only WriteAsync's. Under Wait, TryWrite returns false
        // when full and leaves the channel untouched -- exactly what lets us detect
        // and count each overflow ourselves below. Under DropNewest/DropOldest/
        // DropWrite, TryWrite instead always returns true and the channel silently
        // evicts/discards a frame internally, which would pin DroppedFrameCount at
        // zero forever -- the exact "swallow the error" failure this design (and the
        // constitution's Error Handling rule) forbids.
        _channel = Channel.CreateBounded<Frame>(new BoundedChannelOptions(channelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public long DroppedFrameCount => Interlocked.Read(ref _dropped);

    /// <summary>Called on the real-time audio thread with one interleaved block.</summary>
    public void Submit(ReadOnlySpan<float> interleaved, int channels)
    {
        int frames = interleaved.Length / channels;
        if (_mono.Length < frames) _mono = new float[frames];

        for (int i = 0; i < frames; i++)
        {
            float sum = 0f;
            int baseIdx = i * channels;
            for (int c = 0; c < channels; c++) sum += interleaved[baseIdx + c];
            _mono[i] = sum / channels;
        }

        _emitted.Clear();
        _accumulator.Append(_mono.AsSpan(0, frames), _emitted);
        EmitToChannel(_emitted);
    }

    /// <summary>
    /// Signals end-of-stream: flushes the buffered tail as zero-padded trailing
    /// frame(s) (<see cref="FrameAccumulator.Flush"/> — matching the WAV adapter's
    /// end-of-file convention, R10.1) and then completes the channel so
    /// <see cref="Frames"/> finishes. Idempotent — safe to call more than once
    /// (e.g. once from a Ctrl+C handler and again from <c>Dispose</c>).
    /// </summary>
    public void Complete()
    {
        _emitted.Clear();
        _accumulator.Flush(_emitted);
        EmitToChannel(_emitted);
        _channel.Writer.TryComplete();
    }

    private void EmitToChannel(List<Frame> frames)
    {
        foreach (var f in frames)
            if (!_channel.Writer.TryWrite(f))
                Interlocked.Increment(ref _dropped);
    }

    public IEnumerable<Frame> Frames
    {
        get
        {
            var reader = _channel.Reader;
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                while (reader.TryRead(out var frame))
                    yield return frame;
        }
    }
}
