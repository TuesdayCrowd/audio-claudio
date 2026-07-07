using AudioClaudio.Domain;

namespace AudioClaudio.Infrastructure.Capture;

/// <summary>
/// Reframes a continuous mono sample stream into overlapping frames of
/// <c>frameSize</c> samples advanced by <c>hop</c> samples. Pure and
/// device-free: samples arrive via <see cref="Append"/> in arbitrary-sized
/// pushes; complete frames are appended to the caller's list. The running
/// start position is a sample count from the stream start (never a clock read),
/// so the same audio always produces bit-identical frames (non-negotiables 1-3).
///
/// Frame-tail convention (R10.1: MUST match <c>WavAudioSource</c> exactly — verified
/// against the actual <see cref="AudioClaudio.Domain.Framing"/> source and CONTRACTS.md
/// §2, not just the prose of this step's plan): a file is read as one frame per hop
/// position with start &lt; length, and the final frame is ZERO-PADDED rather than
/// dropped, so every input sample appears in a frame. A live stream has no length
/// until it ends, so <see cref="Append"/> emits only full, unpadded frames as data
/// arrives (there is nothing to pad yet — padding early would guess at samples that
/// may still arrive), and <see cref="Flush"/> emits the trailing zero-padded frame(s)
/// once, at the declared end of stream — reproducing the file adapter's tail exactly.
/// </summary>
public sealed class FrameAccumulator
{
    private readonly int _frameSize;
    private readonly int _hop;
    private readonly SampleRate _rate;
    private readonly List<float> _buffer = new();
    private long _nextStart; // sample index of the next frame's first sample

    public FrameAccumulator(int frameSize, int hop, SampleRate rate)
    {
        if (frameSize < 1) throw new ArgumentOutOfRangeException(nameof(frameSize));
        if (hop < 1) throw new ArgumentOutOfRangeException(nameof(hop));
        _frameSize = frameSize;
        _hop = hop;
        _rate = rate;
    }

    /// <summary>Buffers <paramref name="mono"/> and appends every full frame it completes.</summary>
    public void Append(ReadOnlySpan<float> mono, List<Frame> output)
    {
        foreach (var s in mono) _buffer.Add(s);

        int pos = 0;
        while (_buffer.Count - pos >= _frameSize)
        {
            var samples = new float[_frameSize];
            _buffer.CopyTo(pos, samples, 0, _frameSize);
            output.Add(new Frame(samples, new SamplePosition(_nextStart, _rate)));
            _nextStart += _hop;
            pos += _hop;
        }

        // Drop the consumed prefix; samples before the next frame start are never reused.
        if (pos > 0) _buffer.RemoveRange(0, pos);
    }

    /// <summary>
    /// Emits the buffered tail as zero-padded trailing frame(s) — one frame per
    /// remaining hop position with start &lt; the buffered length, exactly mirroring
    /// <see cref="AudioClaudio.Domain.Framing"/>.Split's end-of-file convention — so a
    /// capture session's last (possibly &lt; frameSize) samples are never silently
    /// discarded. Call once, when the caller knows the stream has ended (never on a
    /// timer/clock — non-negotiable 2). Safe when nothing is buffered (no-op) and
    /// safe to call more than once (idempotent: the buffer is empty after the first call).
    /// </summary>
    public void Flush(List<Frame> output)
    {
        int length = _buffer.Count;
        for (int start = 0; start < length; start += _hop)
        {
            var window = new float[_frameSize];
            int remaining = length - start;
            int count = Math.Min(remaining, _frameSize);
            _buffer.CopyTo(start, window, 0, count); // rest of `window` stays zero (padding)
            output.Add(new Frame(window, new SamplePosition(_nextStart, _rate)));
            _nextStart += _hop;
        }

        _buffer.Clear();
    }
}
