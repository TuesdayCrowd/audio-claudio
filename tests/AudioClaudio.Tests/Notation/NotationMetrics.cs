using System;
using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Polyphony;

namespace AudioClaudio.Tests.Notation;

/// <summary>
/// Scores the notation layer against a <see cref="NotationCase"/>'s ground truth (v2 Stage 3). Each
/// metric takes the algorithm under test as a delegate, so Stage 3a records the <b>baseline</b> with
/// today's algorithms and Stages 3b–3d raise the bar by passing the new ones. Determinism follows the
/// generator's (non-negotiable 3).
/// </summary>
public static class NotationMetrics
{
    /// <summary>
    /// Fraction of notes whose quantized note <b>value</b> matches the intended value (in fine ticks =
    /// twelfths of a quarter). Each hand's monophonic line is quantized independently at
    /// <paramref name="subdivision"/> and flattened; because a hand's onsets strictly increase, the
    /// flattened order equals the truth order, so values compare position-for-position — no fuzzy
    /// matching. The straight sixteenth grid cannot represent a triplet-eighth (4 fine), so triplets miss
    /// at the baseline and a triplet-capable grid recovers them (the Stage-3d target).
    /// </summary>
    public static double NoteValueAccuracy(NotationCase c, Subdivision subdivision)
    {
        ArgumentNullException.ThrowIfNull(c);
        var rate = new SampleRate(NotationCorpusGen.SampleRateHz);
        double samplesPerFine = 60.0 / c.Bpm * rate.Hz / NotationCorpusGen.FinePerQuarter;
        var grid = new QuantizationGrid(rate, new Tempo(c.Bpm), TimeSignature.FourFour, subdivision);
        var window = new SampleDuration(rate.Hz / 50, rate); // ~20 ms; monophonic per hand, so it never groups

        int correct = 0, total = 0;
        foreach (Hand hand in new[] { Hand.Left, Hand.Right })
        {
            List<NotationNote> handTruth = TruthForHand(c, hand);
            if (handTruth.Count == 0)
            {
                continue;
            }

            List<NoteEvent> handEvents = EventsForHand(c, hand);
            GrandStaffScore gs = PolyphonicQuantizer.Quantize(handEvents, grid, window);
            List<NoteEvent> flat = GrandStaffFlattener.ToNoteEvents(gs, grid)
                .OrderBy(e => e.Onset.Samples).ToList();

            total += handTruth.Count;
            for (int i = 0; i < handTruth.Count; i++)
            {
                if (i >= flat.Count)
                {
                    break; // missing notes count as misses
                }

                long recoveredFine = (long)Math.Round(flat[i].Duration.Samples / samplesPerFine, MidpointRounding.AwayFromZero);
                if (recoveredFine == handTruth[i].ValueFine)
                {
                    correct++;
                }
            }
        }

        return total == 0 ? 1.0 : (double)correct / total;
    }

    /// <summary>Corpus-mean note-value accuracy.</summary>
    public static double NoteValueAccuracy(IReadOnlyList<NotationCase> cases, Subdivision subdivision) =>
        cases.Count == 0 ? 1.0 : cases.Average(c => NoteValueAccuracy(c, subdivision));

    /// <summary>Fraction of cases whose key signature <paramref name="detect"/> recovers exactly.</summary>
    public static double KeyAccuracy(IReadOnlyList<NotationCase> cases, Func<IReadOnlyList<Pitch>, int> detect)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(detect);
        if (cases.Count == 0)
        {
            return 1.0;
        }

        int correct = 0;
        foreach (NotationCase c in cases)
        {
            var pitches = c.Events.Select(e => e.Pitch).ToList();
            if (detect(pitches) == c.Fifths)
            {
                correct++;
            }
        }

        return (double)correct / cases.Count;
    }

    /// <summary>
    /// Fraction of notes assigned to the correct hand. <paramref name="split"/> receives the case's
    /// events (in their original, truth-aligned order) and returns one <see cref="Hand"/> per event in
    /// that same order; because <c>Events[i]</c> was built from <c>Truth[i]</c>, comparison is by index.
    /// </summary>
    public static double HandAccuracy(NotationCase c, Func<IReadOnlyList<NoteEvent>, IReadOnlyList<Hand>> split)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(split);
        IReadOnlyList<Hand> assigned = split(c.Events);
        if (assigned.Count != c.Events.Count)
        {
            throw new InvalidOperationException(
                $"Split returned {assigned.Count} assignments for {c.Events.Count} events; they must align by index.");
        }

        int correct = 0;
        for (int i = 0; i < c.Events.Count; i++)
        {
            if (assigned[i] == c.Truth[i].Hand)
            {
                correct++;
            }
        }

        return c.Events.Count == 0 ? 1.0 : (double)correct / c.Events.Count;
    }

    /// <summary>Corpus-mean hand accuracy.</summary>
    public static double HandAccuracy(
        IReadOnlyList<NotationCase> cases, Func<IReadOnlyList<NoteEvent>, IReadOnlyList<Hand>> split) =>
        cases.Count == 0 ? 1.0 : cases.Average(c => HandAccuracy(c, split));

    /// <summary>The fixed middle-C split expressed as a hand-assignment delegate — the Stage-3c baseline.</summary>
    public static IReadOnlyList<Hand> MiddleCSplit(IReadOnlyList<NoteEvent> events) =>
        events.Select(e => e.Pitch.MidiNumber >= StaffSplitter.DefaultSplitMidi ? Hand.Right : Hand.Left).ToList();

    private static List<NotationNote> TruthForHand(NotationCase c, Hand hand) =>
        c.Truth.Where(t => t.Hand == hand).ToList();

    private static List<NoteEvent> EventsForHand(NotationCase c, Hand hand) =>
        c.Events.Where((_, i) => c.Truth[i].Hand == hand).ToList();
}
