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
    public static Result Collapse(
        IReadOnlyList<NoteEvent> notes, float[] audio, SampleRate rate, SampleDuration maxSilence)
    {
        if (notes is null) throw new ArgumentNullException(nameof(notes));
        if (audio is null) throw new ArgumentNullException(nameof(audio));

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

        return new Result(retimed, RemoveSpans(audio, cuts));
    }

    // Copy `audio` minus the given ordered, non-overlapping spans.
    private static float[] RemoveSpans(float[] audio, List<(long Start, long Count)> cuts)
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
            write += keep;
            read = start + count;
        }
        Array.Copy(audio, read, result, write, (int)(audio.Length - read));
        return result;
    }
}
