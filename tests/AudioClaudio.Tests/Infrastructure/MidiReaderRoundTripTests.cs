using System;
using System.IO;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi;
using CsCheck;
using NoteEvent = AudioClaudio.Domain.NoteEvent;
using Tempo = AudioClaudio.Domain.Tempo;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class MidiReaderRoundTripTests
{
    // Write a raw performance, read it back through MidiFileReader, and demand
    // tempo recovered, pitch/velocity exact, onset/duration within one tick.
    [Fact]
    [Trait("Category", "Fast")]
    public void ReadsBackRawEventsWithinOneTick()
    {
        var rate = new SampleRate(44100);
        var original = new[]
        {
            new NoteEvent(new Pitch(60), new SamplePosition(22050, rate), new SampleDuration(44100, rate), 80),
            new NoteEvent(new Pitch(64), new SamplePosition(88200, rate), new SampleDuration(22050, rate), 90),
        };

        var writer = new DryWetMidiWriter();
        using var stream = new MemoryStream();
        ((INoteEventWriter)writer).Write(original, new Tempo(120), stream);

        stream.Position = 0;
        MidiReadResult result = MidiFileReader.Read(stream, rate);

        Assert.Equal(120.0, result.Tempo.BeatsPerMinute, 3);   // tempo recovered from SetTempo
        Assert.Equal(original.Length, result.Events.Count);

        double samplesPerTick = 60.0 * rate.Hz / (DryWetMidiWriter.TicksPerQuarterNote * 120.0);
        long tolerance = (long)Math.Ceiling(samplesPerTick);
        for (int i = 0; i < original.Length; i++)
        {
            Assert.Equal(original[i].Pitch.MidiNumber, result.Events[i].Pitch.MidiNumber);   // exact
            Assert.Equal(original[i].Velocity, result.Events[i].Velocity);                    // exact
            Assert.True(Math.Abs(result.Events[i].Onset.Samples - original[i].Onset.Samples) <= tolerance,
                $"onset off by more than one tick: {result.Events[i].Onset.Samples} vs {original[i].Onset.Samples}");
            Assert.True(Math.Abs(result.Events[i].Duration.Samples - original[i].Duration.Samples) <= tolerance,
                $"duration off by more than one tick: {result.Events[i].Duration.Samples} vs {original[i].Duration.Samples}");
        }
    }

    // Tempos whose 60_000_000/BPM microsecond value is an integer, so the tempo
    // round-trips exactly and the reader/writer share one BPM (keeps the timing
    // tolerance a clean one tick).
    private static readonly int[] ExactTempos = { 60, 75, 96, 100, 120, 125, 150 };

    private static readonly Gen<(int Bpm, NoteEvent[] Events)> GenPerformance =
        from t in Gen.Int[0, ExactTempos.Length - 1]
        from count in Gen.Int[1, 6]
        from pitches in Gen.Int[33, 96].Array[count]
        from gaps in Gen.Int[0, 20000].Array[count]
        from lengths in Gen.Int[2000, 40000].Array[count]
        from velocities in Gen.Int[1, 127].Array[count]
        select (ExactTempos[t], BuildEvents(pitches, gaps, lengths, velocities));

    private static NoteEvent[] BuildEvents(int[] pitches, int[] gaps, int[] lengths, int[] velocities)
    {
        var rate = new SampleRate(44100);
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
    public void ReaderRoundTripPreservesPitchAndTimingWithinOneTick()
    {
        GenPerformance.Sample(sample =>
        {
            var (bpm, events) = sample;
            var rate = new SampleRate(44100);

            var writer = new DryWetMidiWriter();
            using var stream = new MemoryStream();
            ((INoteEventWriter)writer).Write(events, new Tempo(bpm), stream);

            stream.Position = 0;
            MidiReadResult result = MidiFileReader.Read(stream, rate);

            Assert.Equal((double)bpm, result.Tempo.BeatsPerMinute, 2);
            Assert.Equal(events.Length, result.Events.Count);

            double samplesPerTick = 60.0 * rate.Hz / (DryWetMidiWriter.TicksPerQuarterNote * bpm);
            long tolerance = (long)Math.Ceiling(samplesPerTick);
            for (int i = 0; i < events.Length; i++)
            {
                Assert.Equal(events[i].Pitch.MidiNumber, result.Events[i].Pitch.MidiNumber);
                Assert.Equal(events[i].Velocity, result.Events[i].Velocity);
                Assert.True(Math.Abs(result.Events[i].Onset.Samples - events[i].Onset.Samples) <= tolerance);
                Assert.True(Math.Abs(result.Events[i].Duration.Samples - events[i].Duration.Samples) <= tolerance);
            }
        }, iter: 300);
        // CsCheck prints a `seed:` on any failure — paste it back into Sample(...) to replay exactly.
    }
}
