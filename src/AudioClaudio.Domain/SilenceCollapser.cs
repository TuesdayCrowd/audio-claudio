using System;
using System.Collections.Generic;
using System.Linq;

namespace AudioClaudio.Domain;

/// <summary>
/// Collapses over-long silences out of a recorded performance so the audio plays as one continuous
/// piece: the leading gap, the gaps between notes, and the trailing gap that are longer than a
/// threshold are shrunk down to that threshold, and the SAME sample spans are removed from BOTH the
/// note timeline and the audio so the two stay aligned. Pure; no I/O. The notes' positions and the
/// audio MUST share <paramref name="rate"/>. Short gaps (&lt;= threshold) are left untouched, so the
/// piece's own phrasing/rhythm survives.
/// </summary>
public static class SilenceCollapser
{
    /// <summary>The de-silenced notes (onsets shifted earlier) and audio (matching spans removed).</summary>
    public readonly record struct Result(IReadOnlyList<NoteEvent> Notes, float[] Audio);

    /// <param name="notes">The performance's notes; any order (sorted internally by onset).</param>
    /// <param name="audio">Mono PCM the notes were transcribed from, at <paramref name="rate"/>.</param>
    /// <param name="rate">The shared sample rate of the notes' positions and the audio.</param>
    /// <param name="maxSilence">Silences longer than this are shrunk to it; shorter gaps are kept.</param>
    /// <param name="fadeSamples">Length of the de-click fade applied to the tail of each kept segment
    /// before a splice, in samples. Defaults to ~5 ms of <paramref name="rate"/> when null; a caller may
    /// pass 0 to disable it (e.g. alignment tests that assert exact sample values across a splice).</param>
    public static Result Collapse(
        IReadOnlyList<NoteEvent> notes, float[] audio, SampleRate rate, SampleDuration maxSilence, int? fadeSamples = null)
    {
        if (notes is null) throw new ArgumentNullException(nameof(notes));
        if (audio is null) throw new ArgumentNullException(nameof(audio));

        int fade = fadeSamples ?? Math.Max(1, rate.Hz / 200);
        long threshold = maxSilence.Samples;
        var sorted = notes.OrderBy(n => n.Onset.Samples).ToList();

        var cuts = new List<(long Start, long Count)>();
        var retimed = new List<NoteEvent>(sorted.Count);
        long prevEnd = 0;   // end of the latest note so far, in ORIGINAL samples
        long removed = 0;   // cumulative samples removed before the current point

        foreach (NoteEvent n in sorted)
        {
            long onset = n.Onset.Samples;
            long gap = onset - prevEnd;
            if (gap > threshold)
            {
                cuts.Add((prevEnd + threshold, gap - threshold));
                removed += gap - threshold;
            }

            retimed.Add(new NoteEvent(n.Pitch, new SamplePosition(onset - removed, rate), n.Duration, n.Velocity));

            long end = onset + n.Duration.Samples;
            if (end > prevEnd) prevEnd = end;
        }

        // Trailing silence past the last note (audio only; no notes follow).
        long trailing = audio.Length - prevEnd;
        if (trailing > threshold)
            cuts.Add((prevEnd + threshold, trailing - threshold));

        return new Result(retimed, RemoveSpans(audio, cuts, fade));
    }

    // Copy `audio` minus the given ordered, non-overlapping spans, fading the tail of each kept
    // segment so a splice that truncates a still-ringing note settles smoothly instead of clicking.
    private static float[] RemoveSpans(float[] audio, List<(long Start, long Count)> cuts, int fade)
    {
        if (cuts.Count == 0) return (float[])audio.Clone();

        long totalRemoved = cuts.Sum(c => c.Count);
        var result = new float[audio.Length - totalRemoved];
        int write = 0;
        long read = 0;
        foreach ((long start, long count) in cuts)
        {
            int keep = (int)(start - read);
            Array.Copy(audio, read, result, write, keep);
            FadeOutTail(result, write, keep, fade);
            write += keep;
            read = start + count;
        }
        Array.Copy(audio, read, result, write, (int)(audio.Length - read));
        FadeOutTail(result, write, (int)(audio.Length - read), fade);
        return result;
    }

    // Linearly ramps the last `fade` samples of result[offset, offset+length) down to zero, so a cut
    // that truncates a still-ringing note settles smoothly instead of clicking. Tail-only: the next
    // segment starts at a note attack (~0), so the join is smooth without altering attacks.
    private static void FadeOutTail(float[] result, int offset, int length, int fade)
    {
        int f = Math.Min(fade, length);
        for (int i = 0; i < f; i++)
        {
            double gain = 1.0 - (i + 1.0) / f; // 1 -> 0 across the last f samples; last sample = 0
            result[offset + length - f + i] *= (float)gain;
        }
    }
}
