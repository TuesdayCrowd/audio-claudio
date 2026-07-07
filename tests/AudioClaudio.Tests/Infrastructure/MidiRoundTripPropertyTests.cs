using System;
using System.IO;
using System.Linq;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi;
using CsCheck;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using NoteEvent = AudioClaudio.Domain.NoteEvent;
using Tempo = AudioClaudio.Domain.Tempo;
using TimeSignature = AudioClaudio.Domain.TimeSignature;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class MidiRoundTripPropertyTests
{
    // A monotonic, non-overlapping monophonic performance: pitch 33..96,
    // a gap then a positive duration, all at one sample rate.
    private static readonly Gen<(int Bpm, int RateHz, NoteEvent[] Events)> GenPerformance =
        from bpm in Gen.Int[60, 140]
        from rateHz in Gen.Const(44100)
        from count in Gen.Int[1, 6]
        from pitches in Gen.Int[33, 96].Array[count]
        from gaps in Gen.Int[0, 20000].Array[count]
        from lengths in Gen.Int[2000, 40000].Array[count]
        from velocities in Gen.Int[1, 127].Array[count]
        select (bpm, rateHz, BuildEvents(rateHz, pitches, gaps, lengths, velocities));

    private static NoteEvent[] BuildEvents(int rateHz, int[] pitches, int[] gaps, int[] lengths, int[] velocities)
    {
        var rate = new SampleRate(rateHz);
        var events = new NoteEvent[pitches.Length];
        long cursor = 0;
        for (int i = 0; i < pitches.Length; i++)
        {
            cursor += gaps[i];
            events[i] = new NoteEvent(
                new Pitch(pitches[i]),
                new SamplePosition(cursor, rate),
                new SampleDuration(lengths[i], rate),
                velocities[i]);
            cursor += lengths[i];
        }

        return events;
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void RawEventRoundTripPreservesPitchAndTimingWithinOneTick()
    {
        GenPerformance.Sample(sample =>
        {
            var (bpm, rateHz, events) = sample;

            INoteEventWriter writer = new DryWetMidiWriter();
            using var stream = new MemoryStream();
            writer.Write(events, new Tempo(bpm), stream);

            stream.Position = 0;
            var notes = MidiFile.Read(stream).GetNotes().OrderBy(n => n.Time).ToList();

            Assert.Equal(events.Length, notes.Count);   // note count matches

            // One tick, expressed in samples, is the R7.2 tolerance.
            double samplesPerTick = 60.0 * rateHz / (DryWetMidiWriter.TicksPerQuarterNote * bpm);
            long tolerance = (long)Math.Ceiling(samplesPerTick);

            for (int i = 0; i < events.Length; i++)
            {
                Assert.Equal(events[i].Pitch.MidiNumber, (int)notes[i].NoteNumber);   // exact
                Assert.Equal(events[i].Velocity, (int)notes[i].Velocity);             // exact

                long onsetBack = (long)Math.Round(notes[i].Time * samplesPerTick);
                long durationBack = (long)Math.Round(notes[i].Length * samplesPerTick);
                Assert.True(Math.Abs(onsetBack - events[i].Onset.Samples) <= tolerance,
                    $"onset off by more than one tick: {onsetBack} vs {events[i].Onset.Samples}");
                Assert.True(Math.Abs(durationBack - events[i].Duration.Samples) <= tolerance,
                    $"duration off by more than one tick: {durationBack} vs {events[i].Duration.Samples}");
            }
        }, iter: 500);
        // CsCheck prints a `seed:` on any failure — paste it back into Sample(...) to replay exactly.
    }

    // Each measure is four quarter-slots (note or rest) → always a full 4/4 bar.
    // Lengths are one quarter of GRID ticks per slot, on a sixteenth grid.
    private static readonly int QuarterGridTicks = Subdivision.Sixteenth.TicksPerQuarter();

    private static readonly Gen<ScoreElement> GenSlot =
        from isNote in Gen.Bool
        from midi in Gen.Int[33, 96]
        from velocity in Gen.Int[1, 127]
        select isNote
            ? ScoreElement.Note(new Pitch(midi), velocity, QuarterGridTicks)
            : ScoreElement.Rest(QuarterGridTicks);

    private static readonly Gen<Measure> GenMeasure =
        GenSlot.Array[4].Select(elements => new Measure(elements));

    private static readonly Gen<Score> GenScore =
        from bpm in Gen.Int[60, 140]
        from measures in GenMeasure.Array[1, 4]
        select new Score(new Tempo(bpm), TimeSignature.FourFour, Subdivision.Sixteenth, measures);

    [Fact]
    [Trait("Category", "Slow")]
    public void ScoreRoundTripIsExactOnTheGrid()
    {
        GenScore.Sample(score =>
        {
            IScoreWriter writer = new DryWetMidiWriter();
            using var stream = new MemoryStream();
            writer.Write(score, stream);

            stream.Position = 0;
            var notes = MidiFile.Read(stream).GetNotes().OrderBy(n => n.Time).ToList();

            // Expected ticks recomputed independently of the writer's internals:
            // one quarter slot = PPQN MIDI ticks, measure = 4·PPQN, notes emit, rests skip.
            long ticksPerMeasure = 4L * DryWetMidiWriter.TicksPerQuarterNote;
            var expected = new List<(int Midi, long Time, long Length)>();
            for (int m = 0; m < score.Measures.Count; m++)
            {
                long cursor = m * ticksPerMeasure;
                foreach (var element in score.Measures[m].Elements)
                {
                    if (element.Kind == ElementKind.Note)
                    {
                        expected.Add((element.Pitch!.Value.MidiNumber, cursor, DryWetMidiWriter.TicksPerQuarterNote));
                    }

                    cursor += DryWetMidiWriter.TicksPerQuarterNote;
                }
            }

            Assert.Equal(expected.Count, notes.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Midi, (int)notes[i].NoteNumber);
                Assert.Equal(expected[i].Time, notes[i].Time);        // exact — grid positions are integer ticks
                Assert.Equal(expected[i].Length, notes[i].Length);    // exact
            }
        }, iter: 300);
    }
}
