using System.Collections.Generic;
using System.Linq;

namespace AudioClaudio.Domain.Polyphony;

/// <summary>
/// Quantizes overlapping <see cref="NoteEvent"/>s into a <see cref="GrandStaffScore"/>: group notes
/// into chords (<see cref="ChordGrouper"/>), split each across the treble/bass staves
/// (<see cref="StaffSplitter"/>), then lay out each staff independently as a homophonic sequence of
/// chords — snapping onsets and durations to the grid, clipping each chord to the next onset,
/// filling gaps with rests, and barring. Both staves are padded to the SAME measure count so they
/// stay aligned. Pure and deterministic; the input list is never mutated (R6.2). The monophonic
/// <see cref="Quantizer"/> is untouched — this is a parallel path for the polyphonic engine.
/// </summary>
public static class PolyphonicQuantizer
{
    public static GrandStaffScore Quantize(
        IReadOnlyList<NoteEvent> events,
        QuantizationGrid grid,
        SampleDuration chordWindow,
        int splitMidi = StaffSplitter.DefaultSplitMidi)
    {
        ArgumentNullException.ThrowIfNull(events);

        var trebleChords = new List<Chord>();
        var bassChords = new List<Chord>();
        foreach (Chord chord in ChordGrouper.Group(events, chordWindow))
        {
            if (!chord.Onset.Rate.Equals(grid.SampleRate))
            {
                throw new ArgumentException(
                    $"Event sample rate does not match grid sample rate {grid.SampleRate.Hz} Hz.", nameof(events));
            }

            (Chord? trebleFragment, Chord? bassFragment) = StaffSplitter.Split(chord, splitMidi);
            if (trebleFragment is not null)
            {
                trebleChords.Add(trebleFragment);
            }

            if (bassFragment is not null)
            {
                bassChords.Add(bassFragment);
            }
        }

        List<LaidChord> treble = Lay(trebleChords, grid);
        List<LaidChord> bass = Lay(bassChords, grid);

        if (treble.Count == 0 && bass.Count == 0)
        {
            return new GrandStaffScore(
                grid.Tempo, grid.TimeSignature, grid.Subdivision, Array.Empty<GrandStaffMeasure>());
        }

        int ticksPerMeasure = grid.TicksPerMeasure;
        long maxEnd = System.Math.Max(EndTick(treble), EndTick(bass));
        long measureCount = (maxEnd + ticksPerMeasure - 1) / ticksPerMeasure;
        long paddedTotal = measureCount * ticksPerMeasure;

        List<List<ChordElement>> trebleMeasures = BuildMeasures(treble, paddedTotal, ticksPerMeasure);
        List<List<ChordElement>> bassMeasures = BuildMeasures(bass, paddedTotal, ticksPerMeasure);

        var measures = new List<GrandStaffMeasure>((int)measureCount);
        for (int i = 0; i < measureCount; i++)
        {
            measures.Add(new GrandStaffMeasure(trebleMeasures[i], bassMeasures[i]));
        }

        return new GrandStaffScore(grid.Tempo, grid.TimeSignature, grid.Subdivision, measures);
    }

    private readonly record struct LaidChord(long OnsetTick, int LengthTicks, IReadOnlyList<Pitch> Pitches, int Velocity);

    // Snap each chord to (onsetTick, durationTicks), merge chords that snap to the same onset tick
    // (union their pitches), then clip each chord to the next onset so a staff never overlaps itself.
    private static List<LaidChord> Lay(List<Chord> staffChords, QuantizationGrid grid)
    {
        var byTick = new SortedDictionary<long, (SortedSet<int> Pitches, int Duration, int Velocity)>();
        foreach (Chord chord in staffChords)
        {
            long onsetTick = grid.SamplesToTick(chord.Onset.Samples);
            int durationTicks = grid.NearestStandardValueTicks(chord.Duration.Samples / grid.SamplesPerTick);
            if (!byTick.TryGetValue(onsetTick, out (SortedSet<int> Pitches, int Duration, int Velocity) agg))
            {
                agg = (new SortedSet<int>(), 0, 0);
            }

            foreach (Pitch p in chord.Pitches)
            {
                agg.Pitches.Add(p.MidiNumber);
            }

            agg.Duration = System.Math.Max(agg.Duration, durationTicks);
            agg.Velocity = System.Math.Max(agg.Velocity, chord.Velocity);
            byTick[onsetTick] = agg;
        }

        var entries = byTick.ToList();
        var laid = new List<LaidChord>(entries.Count);
        for (int k = 0; k < entries.Count; k++)
        {
            long onset = entries[k].Key;
            (SortedSet<int> pitches, int duration, int velocity) = entries[k].Value;
            long end = onset + duration;
            if (k + 1 < entries.Count && entries[k + 1].Key < end)
            {
                end = entries[k + 1].Key;
            }

            int length = (int)(end - onset);
            if (length <= 0)
            {
                continue;
            }

            laid.Add(new LaidChord(onset, length, pitches.Select(m => new Pitch(m)).ToList(), velocity));
        }

        return laid;
    }

    private static long EndTick(List<LaidChord> laid) =>
        laid.Count == 0 ? 0 : laid[^1].OnsetTick + laid[^1].LengthTicks;

    // Fill a staff's laid chords (plus gap/leading/trailing rests) up to paddedTotal, then split at
    // barlines into exactly paddedTotal/ticksPerMeasure measures of chord elements.
    private static List<List<ChordElement>> BuildMeasures(List<LaidChord> laid, long paddedTotal, int ticksPerMeasure)
    {
        var runs = new List<(bool IsNote, IReadOnlyList<Pitch> Pitches, int Velocity, long Length)>();
        long cursor = 0;
        foreach (LaidChord n in laid)
        {
            if (n.OnsetTick > cursor)
            {
                runs.Add((false, Array.Empty<Pitch>(), 0, n.OnsetTick - cursor));
            }

            runs.Add((true, n.Pitches, n.Velocity, n.LengthTicks));
            cursor = n.OnsetTick + n.LengthTicks;
        }

        if (paddedTotal > cursor)
        {
            runs.Add((false, Array.Empty<Pitch>(), 0, paddedTotal - cursor));
        }

        var measures = new List<List<ChordElement>>();
        var current = new List<ChordElement>();
        long positionInMeasure = 0;
        foreach ((bool isNote, IReadOnlyList<Pitch> pitches, int velocity, long length) in runs)
        {
            long remaining = length;
            while (remaining > 0)
            {
                long room = ticksPerMeasure - positionInMeasure;
                long take = System.Math.Min(room, remaining);
                bool crossesBarline = take < remaining;
                current.Add(isNote
                    ? ChordElement.Note(pitches, velocity, (int)take, tiedToNext: crossesBarline)
                    : ChordElement.Rest((int)take));

                remaining -= take;
                positionInMeasure += take;
                if (positionInMeasure == ticksPerMeasure)
                {
                    measures.Add(current);
                    current = new List<ChordElement>();
                    positionInMeasure = 0;
                }
            }
        }

        return measures;
    }
}
