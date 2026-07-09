using System.Collections.Generic;
using System.Linq;

namespace AudioClaudio.Domain.Polyphony;

/// <summary>
/// Flattens a quantized <see cref="GrandStaffScore"/> back into a flat <see cref="NoteEvent"/> list
/// (e.g. to write a polyphonic quantized MIDI). Ties are merged: a chord split across barlines
/// (a run of elements each <see cref="ChordElement.TiedToNext"/>) becomes one note per pitch
/// spanning the whole run. Deterministic; converts grid ticks to samples through the grid.
/// </summary>
public static class GrandStaffFlattener
{
    public static IReadOnlyList<NoteEvent> ToNoteEvents(GrandStaffScore score, QuantizationGrid grid)
    {
        ArgumentNullException.ThrowIfNull(score);

        SampleRate rate = grid.SampleRate;
        double samplesPerTick = grid.SamplesPerTick;
        int ticksPerMeasure = grid.TicksPerMeasure;
        var events = new List<NoteEvent>();

        EmitStaff(AbsoluteTimeline(score, ticksPerMeasure, m => m.Treble), rate, samplesPerTick, events);
        EmitStaff(AbsoluteTimeline(score, ticksPerMeasure, m => m.Bass), rate, samplesPerTick, events);

        events.Sort((a, b) => a.Onset.Samples != b.Onset.Samples
            ? a.Onset.Samples.CompareTo(b.Onset.Samples)
            : a.Pitch.MidiNumber.CompareTo(b.Pitch.MidiNumber));
        return events;
    }

    // A staff's elements laid on an absolute tick timeline across all measures.
    private static List<(long StartTick, ChordElement Element)> AbsoluteTimeline(
        GrandStaffScore score, int ticksPerMeasure, System.Func<GrandStaffMeasure, IReadOnlyList<ChordElement>> pick)
    {
        var timeline = new List<(long, ChordElement)>();
        for (int mi = 0; mi < score.Measures.Count; mi++)
        {
            long position = (long)mi * ticksPerMeasure;
            foreach (ChordElement element in pick(score.Measures[mi]))
            {
                timeline.Add((position, element));
                position += element.LengthTicks;
            }
        }

        return timeline;
    }

    private static void EmitStaff(
        List<(long StartTick, ChordElement Element)> timeline, SampleRate rate, double samplesPerTick, List<NoteEvent> events)
    {
        int i = 0;
        while (i < timeline.Count)
        {
            if (timeline[i].Element.Kind == ElementKind.Rest)
            {
                i++;
                continue;
            }

            long startTick = timeline[i].StartTick;
            IReadOnlyList<Pitch> pitches = timeline[i].Element.Pitches;
            int velocity = timeline[i].Element.Velocity;
            long totalTicks = 0;
            while (true)
            {
                totalTicks += timeline[i].Element.LengthTicks;
                bool tied = timeline[i].Element.TiedToNext;
                i++;
                if (!tied || i >= timeline.Count)
                {
                    break;
                }
            }

            long onset = (long)System.Math.Round(startTick * samplesPerTick, System.MidpointRounding.AwayFromZero);
            long end = (long)System.Math.Round((startTick + totalTicks) * samplesPerTick, System.MidpointRounding.AwayFromZero);
            long duration = System.Math.Max(1, end - onset);
            foreach (Pitch pitch in pitches)
            {
                events.Add(new NoteEvent(
                    pitch, new SamplePosition(onset, rate), new SampleDuration(duration, rate), velocity));
            }
        }
    }
}
