using System.Collections.Generic;
using System.Linq;

namespace AudioClaudio.Domain.Polyphony;

/// <summary>
/// Groups a flat note list into chords: notes whose onset falls within <c>window</c> of a chord's
/// anchor (its earliest note) collapse into one vertical sonority. The window is measured from the
/// anchor, not chained note-to-note, so an arpeggio spread wider than the window becomes several
/// chords — a defined, deterministic rule (non-negotiable 3). Pure; the input list is not mutated.
/// </summary>
public static class ChordGrouper
{
    public static IReadOnlyList<Chord> Group(IReadOnlyList<NoteEvent> events, SampleDuration window)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return Array.Empty<Chord>();
        }

        // Deterministic order: onset, then pitch, then original index.
        List<NoteEvent> ordered = events
            .Select((e, i) => (e, i))
            .OrderBy(x => x.e.Onset.Samples)
            .ThenBy(x => x.e.Pitch.MidiNumber)
            .ThenBy(x => x.i)
            .Select(x => x.e)
            .ToList();

        SampleRate rate = ordered[0].Onset.Rate;
        if (!window.Rate.Equals(rate))
        {
            throw new ArgumentException(
                $"Window rate {window.Rate.Hz} Hz does not match event rate {rate.Hz} Hz.", nameof(window));
        }

        var chords = new List<Chord>();
        int k = 0;
        while (k < ordered.Count)
        {
            long anchor = ordered[k].Onset.Samples;

            // One representative note per pitch in this chord (the longest; louder wins ties on velocity).
            var byPitch = new SortedDictionary<int, NoteEvent>();
            while (k < ordered.Count && ordered[k].Onset.Samples - anchor <= window.Samples)
            {
                NoteEvent e = ordered[k];
                if (!byPitch.TryGetValue(e.Pitch.MidiNumber, out NoteEvent existing)
                    || e.Duration.Samples > existing.Duration.Samples)
                {
                    byPitch[e.Pitch.MidiNumber] = e;
                }

                k++;
            }

            var pitches = byPitch.Keys.Select(m => new Pitch(m)).ToList();
            long longest = byPitch.Values.Max(e => e.Duration.Samples);
            int velocity = byPitch.Values.Max(e => e.Velocity);
            chords.Add(new Chord(
                new SamplePosition(anchor, rate), pitches, new SampleDuration(longest, rate), velocity));
        }

        return chords;
    }
}
