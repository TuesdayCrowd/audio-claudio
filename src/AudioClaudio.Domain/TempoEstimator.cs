using System;
using System.Collections.Generic;
using System.Linq;

namespace AudioClaudio.Domain;

/// <summary>
/// Estimates tempo (BPM) from note onsets by the median inter-onset interval: the most common gap
/// between successive note starts is taken to be one beat. Deterministic; pure. Works for a mostly
/// even, single-line melody dominated by one note value (a beginner's quarter-note tune) and is the
/// Phase-2 replacement for the user-declared tempo (CLAUDE.md §8). Known ambiguity: it cannot tell
/// "quarters at T" from "eighths at 2T", so the result is folded into a plausible BPM range and the
/// caller may still override with an explicit tempo. Validated on real recordings (~118 and ~131 BPM).
/// </summary>
public static class TempoEstimator
{
    // Inter-onset gaps outside this range are dropped before the median: below is a detection blip
    // or ornament, above is a phrase pause — neither is the beat.
    private const double MinGapSeconds = 0.12;
    private const double MaxGapSeconds = 1.5;

    /// <param name="notes">Detected notes; only their onsets are used (any order).</param>
    /// <param name="fallback">Returned when there is too little rhythm to estimate (fewer than 3
    /// notes, or no gaps in the plausible range).</param>
    /// <param name="minBpm">Low fold bound (inclusive).</param>
    /// <param name="maxBpm">High fold bound (inclusive).</param>
    public static Tempo Estimate(IReadOnlyList<NoteEvent> notes, Tempo fallback,
                                 double minBpm = 50.0, double maxBpm = 180.0)
    {
        if (notes is null) throw new ArgumentNullException(nameof(notes));
        if (notes.Count < 3) return fallback;

        var onsets = notes.Select(n => n.Onset).OrderBy(o => o.Samples).ToList();
        var gaps = new List<double>();
        for (int i = 1; i < onsets.Count; i++)
        {
            double secs = (onsets[i].Samples - onsets[i - 1].Samples) / (double)onsets[i].Rate.Hz;
            if (secs >= MinGapSeconds && secs <= MaxGapSeconds)
            {
                gaps.Add(secs);
            }
        }

        if (gaps.Count == 0)
        {
            return fallback;
        }

        gaps.Sort();
        double beat = gaps.Count % 2 == 1
            ? gaps[gaps.Count / 2]
            : (gaps[gaps.Count / 2 - 1] + gaps[gaps.Count / 2]) / 2.0;

        double bpm = 60.0 / beat;
        while (bpm > maxBpm) bpm /= 2.0;
        while (bpm < minBpm) bpm *= 2.0;
        if (bpm < minBpm || bpm > maxBpm)
        {
            return fallback; // could not fold into range
        }

        return new Tempo(Math.Round(bpm));
    }
}
