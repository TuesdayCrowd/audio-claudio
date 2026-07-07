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
}
