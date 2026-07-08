using System.Collections.Generic;

namespace AudioClaudio.Domain;

/// <summary>
/// Pure framing: slice a mono buffer into overlapping windows by the declared
/// <see cref="FrameParameters"/>. Deterministic, no I/O — the single place framing lives (R2.4).
/// </summary>
public static class Framing
{
    /// <param name="samples">Mono buffer (float, nominally [-1, 1]).</param>
    /// <param name="rate">Declared sample rate; stamped onto every frame's start position.</param>
    /// <param name="parameters">Window size N and hop H.</param>
    /// <param name="startSample">Sample index of <paramref name="samples"/>[0] in the wider stream.</param>
    /// <returns>
    /// One frame per hop position with start &lt; length. The final frame is zero-padded if it
    /// runs past the buffer, so every input sample appears in at least one frame.
    /// </returns>
    public static IReadOnlyList<Frame> Split(
        float[] samples, SampleRate rate, FrameParameters parameters, long startSample = 0)
    {
        if (samples is null) throw new ArgumentNullException(nameof(samples));
        if (startSample < 0)
            throw new ArgumentOutOfRangeException(nameof(startSample), startSample, "Start sample must be >= 0.");

        int n = parameters.Size;
        int h = parameters.Hop;
        long length = samples.LongLength;

        var frames = new List<Frame>();
        for (long start = 0; start < length; start += h)
        {
            var window = new float[n];
            long remaining = length - start;
            int count = (int)(remaining < n ? remaining : n);
            Array.Copy(samples, start, window, 0, count); // rest of `window` stays zero (padding)
            frames.Add(new Frame(window, new SamplePosition(startSample + start, rate)));
        }

        return frames;
    }

    /// <summary>
    /// The inverse of <see cref="Split"/>: reassembles a mono buffer from frames that tile it at a
    /// fixed hop, in non-decreasing start order. Consecutive frames' overlap is identical, so each
    /// frame after the first contributes only the samples past the previous frame's end. The result
    /// spans from the first frame's start through the last frame's end, so any zero-padding
    /// <see cref="Split"/> added to a short final frame reappears here as trailing zeros.
    /// </summary>
    /// <param name="frames">Frames in non-decreasing start order, as produced by <see cref="Split"/>.</param>
    /// <returns>The reassembled mono buffer, or an empty array for an empty <paramref name="frames"/>.</returns>
    public static float[] ReconstructMono(IReadOnlyList<Frame> frames)
    {
        if (frames is null) throw new ArgumentNullException(nameof(frames));
        if (frames.Count == 0) return Array.Empty<float>();

        long origin = frames[0].Start.Samples;
        var pcm = new List<float>();
        long written = 0; // samples appended so far, relative to origin

        foreach (Frame frame in frames)
        {
            long start = frame.Start.Samples - origin;
            if (start < 0)
                throw new ArgumentException("Frames must be ordered by non-decreasing start position.", nameof(frames));
            if (start > written)
            {
                for (long i = written; i < start; i++) pcm.Add(0f); // gap -> silence (not expected for contiguous capture)
                written = start;
            }
            int skip = (int)(written - start); // overlap already emitted by the previous frame
            for (int i = skip; i < frame.Samples.Length; i++) pcm.Add(frame.Samples[i]);
            written = start + frame.Samples.Length;
        }

        return pcm.ToArray();
    }
}
