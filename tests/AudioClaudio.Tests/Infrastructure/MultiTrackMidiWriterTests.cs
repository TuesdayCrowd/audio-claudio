using System.IO;
using System.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using NoteEvent = AudioClaudio.Domain.NoteEvent;
using Tempo = AudioClaudio.Domain.Tempo;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class MultiTrackMidiWriterTests
{
    private static readonly SampleRate Rate = new(44100);

    // Three tracks, distinct GM programs, distinct notes/channels — the Stage-3 fixture from the
    // multi-instrument plan (bass 32 / piano 0 / other 26).
    [Fact]
    [Trait("Category", "Fast")]
    public void WritesThreeTracksWithNamesProgramsAndNotes()
    {
        var tracks = new (string Name, int GmProgram, IReadOnlyList<NoteEvent> Notes)[]
        {
            ("bass", 32, new[] { new NoteEvent(new Pitch(33), new SamplePosition(0, Rate), new SampleDuration(22050, Rate), 90) }),
            ("piano", 0, new[] { new NoteEvent(new Pitch(60), new SamplePosition(22050, Rate), new SampleDuration(22050, Rate), 80) }),
            ("other", 26, new[] { new NoteEvent(new Pitch(72), new SamplePosition(44100, Rate), new SampleDuration(11025, Rate), 70) }),
        };

        var writer = new MultiTrackMidiWriter();
        using var stream = new MemoryStream();
        writer.Write(tracks, new Tempo(120), stream);

        stream.Position = 0;
        var midi = MidiFile.Read(stream);

        var division = Assert.IsType<TicksPerQuarterNoteTimeDivision>(midi.TimeDivision);
        Assert.Equal((short)480, division.TicksPerQuarterNote);

        var chunks = midi.GetTrackChunks().ToList();
        Assert.Equal(3, chunks.Count);

        for (int i = 0; i < tracks.Length; i++)
        {
            var (name, gmProgram, notes) = tracks[i];
            var chunk = chunks[i];

            var trackName = chunk.Events.OfType<SequenceTrackNameEvent>().Single();
            Assert.Equal(name, trackName.Text);

            var programChange = chunk.Events.OfType<ProgramChangeEvent>().Single();
            Assert.Equal((byte)gmProgram, (byte)programChange.ProgramNumber);
            Assert.Equal((FourBitNumber)i, programChange.Channel);

            var chunkNotes = chunk.Events.OfType<Melanchall.DryWetMidi.Core.NoteOnEvent>()
                .Select(e => e.Channel)
                .Distinct()
                .ToList();
            Assert.All(chunkNotes, ch => Assert.Equal((FourBitNumber)i, ch));

            var expectedNote = notes[0];
            var actualNotes = chunk.GetNotes().ToList();
            var actualNote = Assert.Single(actualNotes);
            Assert.Equal(expectedNote.Pitch.MidiNumber, (int)actualNote.NoteNumber);
            Assert.Equal(expectedNote.Velocity, (int)actualNote.Velocity);
            Assert.Equal((FourBitNumber)i, actualNote.Channel);

            long expectedOnsetTicks = (long)System.Math.Round(
                (double)expectedNote.Onset.Samples * 480 * 120 / (60.0 * Rate.Hz));
            long expectedLengthTicks = (long)System.Math.Round(
                (double)expectedNote.Duration.Samples * 480 * 120 / (60.0 * Rate.Hz));
            Assert.Equal(expectedOnsetTicks, actualNote.Time);
            Assert.Equal(expectedLengthTicks, actualNote.Length);
        }

        // The global tempo event lives on the FIRST (conductor) track only.
        Assert.Single(chunks[0].Events.OfType<SetTempoEvent>());
        Assert.Empty(chunks[1].Events.OfType<SetTempoEvent>());
        Assert.Empty(chunks[2].Events.OfType<SetTempoEvent>());

        var setTempo = chunks[0].Events.OfType<SetTempoEvent>().Single();
        Assert.Equal(500_000L, setTempo.MicrosecondsPerQuarterNote);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void EmptyTrackListWritesReadableEmptyMidiFile()
    {
        var tracks = System.Array.Empty<(string Name, int GmProgram, IReadOnlyList<NoteEvent> Notes)>();

        var writer = new MultiTrackMidiWriter();
        using var stream = new MemoryStream();
        writer.Write(tracks, new Tempo(120), stream);

        stream.Position = 0;
        var midi = MidiFile.Read(stream);

        Assert.Empty(midi.GetTrackChunks());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void SingleTrackCarriesTempoAndItsOwnNotes()
    {
        var tracks = new (string Name, int GmProgram, IReadOnlyList<NoteEvent> Notes)[]
        {
            ("piano", 0, new[] { new NoteEvent(new Pitch(60), new SamplePosition(0, Rate), new SampleDuration(44100, Rate), 64) }),
        };

        var writer = new MultiTrackMidiWriter();
        using var stream = new MemoryStream();
        writer.Write(tracks, new Tempo(120), stream);

        stream.Position = 0;
        var midi = MidiFile.Read(stream);

        var chunks = midi.GetTrackChunks().ToList();
        var chunk = Assert.Single(chunks);

        Assert.Equal("piano", chunk.Events.OfType<SequenceTrackNameEvent>().Single().Text);
        Assert.Single(chunk.Events.OfType<SetTempoEvent>());
        var note = Assert.Single(chunk.GetNotes());
        Assert.Equal(60, (int)note.NoteNumber);
        Assert.Equal(960L, note.Length); // 1.0s @ 120bpm = 2 beats = 960 ticks
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void WriteIsDeterministic()
    {
        var tracks = new (string Name, int GmProgram, IReadOnlyList<NoteEvent> Notes)[]
        {
            ("bass", 32, new[] { new NoteEvent(new Pitch(33), new SamplePosition(0, Rate), new SampleDuration(22050, Rate), 90) }),
            ("piano", 0, new[] { new NoteEvent(new Pitch(60), new SamplePosition(22050, Rate), new SampleDuration(22050, Rate), 80) }),
        };

        var writer = new MultiTrackMidiWriter();

        byte[] BytesOf()
        {
            using var stream = new MemoryStream();
            writer.Write(tracks, new Tempo(120), stream);
            return stream.ToArray();
        }

        Assert.Equal(BytesOf(), BytesOf());
    }
}
