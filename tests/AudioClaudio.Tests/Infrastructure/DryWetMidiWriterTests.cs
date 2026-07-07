using System.IO;
using System.Linq;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using NoteEvent = AudioClaudio.Domain.NoteEvent;
using Tempo = AudioClaudio.Domain.Tempo;
using TimeSignature = AudioClaudio.Domain.TimeSignature;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class DryWetMidiWriterTests
{
    // A quarter-note-equivalent worth of samples: at 44100 Hz and 120 BPM,
    // 0.5 s = 22050 samples = exactly one beat = 480 ticks; 1.0 s = 44100 samples = 960 ticks.
    [Fact]
    [Trait("Category", "Fast")]
    public void WritesSingleRawEventToReadableMidi()
    {
        var rate = new SampleRate(44100);
        var events = new[]
        {
            new NoteEvent(
                new Pitch(60),                              // middle C
                new SamplePosition(22050, rate),            // 0.5 s in
                new SampleDuration(44100, rate),             // 1.0 s long
                velocity: 80),
        };

        INoteEventWriter writer = new DryWetMidiWriter();
        using var stream = new MemoryStream();
        writer.Write(events, new Tempo(120), stream);

        stream.Position = 0;
        var midi = MidiFile.Read(stream);

        // Tick resolution is fixed at 480 PPQN.
        var division = Assert.IsType<TicksPerQuarterNoteTimeDivision>(midi.TimeDivision);
        Assert.Equal((short)480, division.TicksPerQuarterNote);

        var notes = midi.GetNotes().OrderBy(n => n.Time).ToList();
        var note = Assert.Single(notes);
        Assert.Equal(60, (int)note.NoteNumber);   // pitch is bit-exact
        Assert.Equal(480L, note.Time);            // 0.5 s @ 120 BPM = 1 beat = 480 ticks
        Assert.Equal(960L, note.Length);          // 1.0 s = 2 beats = 960 ticks
        Assert.Equal(80, (int)note.Velocity);
    }

    // One 4/4 measure on a sixteenth grid: half note + quarter rest + quarter note.
    // Lengths are given in GRID ticks (LengthTicks); q = grid ticks per quarter.
    // half = 2q, rest = q, quarter = q -> 4q = a full 4/4 bar. In MIDI ticks
    // (LengthTicks * 480 / q, independent of q): 960, (skip) 480, 480.
    [Fact]
    [Trait("Category", "Fast")]
    public void WritesScoreMeasureToExactTicks()
    {
        int q = Subdivision.Sixteenth.TicksPerQuarter();   // grid ticks per quarter note
        var elements = new[]
        {
            ScoreElement.Note(new Pitch(60), velocity: 80, lengthTicks: 2 * q),   // half at bar start
            ScoreElement.Rest(lengthTicks: q),                                    // quarter rest
            ScoreElement.Note(new Pitch(64), velocity: 80, lengthTicks: q),       // quarter after the rest
        };
        var score = new Score(
            new Tempo(120),
            TimeSignature.FourFour,
            Subdivision.Sixteenth,
            new[] { new Measure(elements) });

        IScoreWriter writer = new DryWetMidiWriter();
        using var stream = new MemoryStream();
        writer.Write(score, stream);

        stream.Position = 0;
        var midi = MidiFile.Read(stream);

        // Tempo: 120 BPM = 500000 microseconds per quarter note.
        var setTempo = midi.GetTrackChunks()
            .SelectMany(c => c.Events)
            .OfType<SetTempoEvent>()
            .Single();
        Assert.Equal(500_000L, setTempo.MicrosecondsPerQuarterNote);

        var notes = midi.GetNotes().OrderBy(n => n.Time).ToList();
        var expected = new (int Midi, long Time, long Length)[]
        {
            (60, 0, 960),       // half at bar start
            (64, 1440, 480),    // quarter after half (960) + quarter rest (480)
        };
        Assert.Equal(expected.Length, notes.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Midi, (int)notes[i].NoteNumber);
            Assert.Equal(expected[i].Time, notes[i].Time);
            Assert.Equal(expected[i].Length, notes[i].Length);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void WriteIsDeterministicForScoreAndEvents()
    {
        var rate = new SampleRate(44100);
        var events = new[]
        {
            new NoteEvent(new Pitch(45), new SamplePosition(1000, rate), new SampleDuration(15000, rate), 90),
            new NoteEvent(new Pitch(57), new SamplePosition(30000, rate), new SampleDuration(15000, rate), 90),
        };
        int q = Subdivision.Sixteenth.TicksPerQuarter();   // grid ticks per quarter note
        var score = new Score(
            new Tempo(100),
            TimeSignature.FourFour,
            Subdivision.Sixteenth,
            new[]
            {
                new Measure(new[]
                {
                    ScoreElement.Note(new Pitch(60), velocity: 80, lengthTicks: q),        // quarter
                    ScoreElement.Rest(lengthTicks: q),                                     // quarter rest
                    ScoreElement.Note(new Pitch(62), velocity: 80, lengthTicks: 2 * q),    // half -> full 4/4 bar
                }),
            });

        var writer = new DryWetMidiWriter();

        Assert.Equal(BytesOfEvents(writer, events), BytesOfEvents(writer, events));
        Assert.Equal(BytesOfScore(writer, score), BytesOfScore(writer, score));
    }

    private static byte[] BytesOfEvents(DryWetMidiWriter writer, NoteEvent[] events)
    {
        using var stream = new MemoryStream();
        ((INoteEventWriter)writer).Write(events, new Tempo(120), stream);
        return stream.ToArray();
    }

    private static byte[] BytesOfScore(DryWetMidiWriter writer, Score score)
    {
        using var stream = new MemoryStream();
        ((IScoreWriter)writer).Write(score, stream);
        return stream.ToArray();
    }
}
