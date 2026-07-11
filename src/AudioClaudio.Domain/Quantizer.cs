using System.Linq;

namespace AudioClaudio.Domain;

/// <summary>
/// Quantizes a continuous-time performance (a list of <see cref="NoteEvent"/>s)
/// onto a tempo grid, producing a <see cref="Score"/> of measures, notes and rests.
/// Pure and deterministic: same input, same score, every run and machine
/// (non-negotiable 3). The input list is never mutated (R6.2).
/// </summary>
public static class Quantizer
{
    /// <param name="coarseGridTicks">The coarse-grid note-off unit (v2 Stage 2): note values are snapped
    /// to standard values aligned to this grid (whole multiples of it), rounding jittery rhythm to cleaner
    /// values. 0 (default) keeps the full standard-value set — the proven behavior the closed loop runs on.</param>
    public static Score Quantize(IReadOnlyList<NoteEvent> events, QuantizationGrid grid, int coarseGridTicks = 0)
    {
        ArgumentNullException.ThrowIfNull(events);

        // 1. Snap every event to (onsetTick, durationTicks), validating the sample rate.
        var notes = new List<GridNote>(events.Count);
        foreach (NoteEvent ev in events)
        {
            if (!ev.Onset.Rate.Equals(grid.SampleRate) || !ev.Duration.Rate.Equals(grid.SampleRate))
            {
                throw new ArgumentException(
                    $"Event sample rate does not match grid sample rate {grid.SampleRate.Hz} Hz.", nameof(events));
            }

            long onsetTick = grid.SamplesToTick(ev.Onset.Samples);
            int durationTicks = grid.NearestStandardValueTicks(ev.Duration.Samples / grid.SamplesPerTick, coarseGridTicks);
            notes.Add(new GridNote(onsetTick, durationTicks, ev.Pitch, ev.Velocity));
        }

        // 2. Deterministic order: by onset, then pitch, then original index (defined tie-break).
        List<GridNote> ordered = notes
            .Select((note, index) => (note, index))
            .OrderBy(x => x.note.OnsetTick)
            .ThenBy(x => x.note.Pitch.MidiNumber)
            .ThenBy(x => x.index)
            .Select(x => x.note)
            .ToList();

        // 3. Monophonic overlap resolution: clip each note to the next onset;
        //    a note left with no room (same-tick collision) is dropped.
        var laid = new List<GridNote>(ordered.Count);
        for (int k = 0; k < ordered.Count; k++)
        {
            GridNote current = ordered[k];
            long end = current.OnsetTick + current.DurationTicks;
            if (k + 1 < ordered.Count && ordered[k + 1].OnsetTick < end)
            {
                end = ordered[k + 1].OnsetTick;
            }

            int length = (int)(end - current.OnsetTick);
            if (length <= 0)
            {
                continue; // collapsed by collision; dropped deterministically
            }

            laid.Add(current with { DurationTicks = length });
        }

        if (laid.Count == 0)
        {
            return new Score(grid.Tempo, grid.TimeSignature, grid.Subdivision, Array.Empty<Measure>());
        }

        // 4. Build a gap-filled timeline of runs and pad the tail to a whole measure.
        long totalTicks = laid[^1].OnsetTick + laid[^1].DurationTicks;
        int ticksPerMeasure = grid.TicksPerMeasure;
        long measureCount = (totalTicks + ticksPerMeasure - 1) / ticksPerMeasure;
        long paddedTotal = measureCount * ticksPerMeasure;

        var runs = new List<Run>();
        long cursor = 0;
        foreach (GridNote note in laid)
        {
            if (note.OnsetTick > cursor)
            {
                runs.Add(Run.Rest(note.OnsetTick - cursor));
            }

            runs.Add(Run.Note(note.Pitch, note.Velocity, note.DurationTicks));
            cursor = note.OnsetTick + note.DurationTicks;
        }

        if (paddedTotal > cursor)
        {
            runs.Add(Run.Rest(paddedTotal - cursor));
        }

        // 5. Split runs at barlines and group into measures.
        var measures = new List<Measure>((int)measureCount);
        var currentElements = new List<ScoreElement>();
        long positionInMeasure = 0;
        foreach (Run run in runs)
        {
            long remaining = run.Length;
            while (remaining > 0)
            {
                long room = ticksPerMeasure - positionInMeasure;
                long take = Math.Min(room, remaining);
                bool crossesBarline = take < remaining;
                currentElements.Add(run.ToElement((int)take, tiedToNext: run.IsNote && crossesBarline));

                remaining -= take;
                positionInMeasure += take;
                if (positionInMeasure == ticksPerMeasure)
                {
                    measures.Add(new Measure(currentElements));
                    currentElements = new List<ScoreElement>();
                    positionInMeasure = 0;
                }
            }
        }

        // paddedTotal is a whole number of measures, so currentElements is always flushed.
        return new Score(grid.Tempo, grid.TimeSignature, grid.Subdivision, measures);
    }

    private readonly record struct GridNote(long OnsetTick, int DurationTicks, Pitch Pitch, int Velocity);

    private readonly record struct Run(bool IsNote, Pitch Pitch, int Velocity, long Length)
    {
        public static Run Note(Pitch pitch, int velocity, long length) => new(true, pitch, velocity, length);

        public static Run Rest(long length) => new(false, default, 0, length);

        public ScoreElement ToElement(int length, bool tiedToNext) =>
            IsNote
                ? ScoreElement.Note(Pitch, Velocity, length, tiedToNext)
                : ScoreElement.Rest(length);
    }
}
