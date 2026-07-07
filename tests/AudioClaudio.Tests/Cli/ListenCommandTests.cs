using AudioClaudio.Application;
using AudioClaudio.Application.Ports;
using AudioClaudio.Application.UseCases;
using AudioClaudio.Cli.Commands;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Cli;

public class ListenCommandTests
{
    private static readonly SampleRate Rate = new SampleRate(44100);

    private sealed class FakeSource : IAudioSource
    {
        public IEnumerable<Frame> Frames { get { yield break; } }
    }

    // CONTRACTS: writers are Stream-based. DryWetMidiWriter implements both ports,
    // so one spy stands in for the raw-event + score MIDI writer.
    private sealed class SpyMidiWriter : IScoreWriter, INoteEventWriter
    {
        public int ScoreWrites;
        public int EventWrites;
        public void Write(Score score, Stream destination) { ScoreWrites++; destination.WriteByte(1); }
        public void Write(IReadOnlyList<NoteEvent> events, Tempo tempo, Stream destination) { EventWrites++; destination.WriteByte(1); }
    }

    private sealed class SpyScoreWriter : IScoreWriter
    {
        public int Writes;
        public void Write(Score score, Stream destination) { Writes++; destination.WriteByte(1); }
    }

    private static NoteEvent Note(int midi, long onset) =>
        new NoteEvent(new Pitch(midi), new SamplePosition(onset, Rate), new SampleDuration(22050, Rate), 100);

    // The session's two seams (Step 10): `notes` is the incremental live-print feed AND (via a
    // real quantize) the accurate batch result the files are written from. Production binds these
    // to TranscriptionPipeline.StreamNotes / .Transcribe.
    private static LiveTranscriptionSession Session(IReadOnlyList<NoteEvent> notes)
    {
        IEnumerable<NoteEvent> Stream(IAudioSource s) { foreach (var n in notes) yield return n; }
        TranscriptionResult Transcribe(IAudioSource s)
        {
            var grid = new QuantizationGrid(Rate, new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth);
            return new TranscriptionResult(Quantizer.Quantize(notes, grid), notes);
        }
        return new LiveTranscriptionSession(Stream, Transcribe);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void PrintsEachNoteAndWritesMidiTrio()
    {
        var notes = new[] { Note(60, 0), Note(62, 22050), Note(64, 44100) };
        var midi = new SpyMidiWriter();
        var printed = new List<string>();
        string dir = Path.Combine(Path.GetTempPath(), $"claudio_listen_{Guid.NewGuid():N}");
        try
        {
            var cmd = new ListenCommand(Session(notes), midi, midi, printed.Add, musicXmlWriter: null);
            var result = cmd.Run(new FakeSource(), tempoBpm: 120, outDir: dir);

            Assert.Equal(3, printed.Count(l => l.StartsWith("note ")));      // one line per note
            Assert.True(File.Exists(Path.Combine(dir, "raw.mid")));          // raw performance written
            Assert.True(File.Exists(Path.Combine(dir, "score.mid")));        // quantized score written
            Assert.False(File.Exists(Path.Combine(dir, "score.musicxml")));  // no MusicXML writer -> no file
            Assert.Equal(1, midi.EventWrites);
            Assert.Equal(1, midi.ScoreWrites);
            Assert.Equal(3, result.Events.Count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    // The MusicXML seam: emitted iff a writer is supplied (real writer arrives in Step 11).
    [Fact]
    [Trait("Category", "Fast")]
    public void WritesMusicXmlOnlyWhenWriterProvided()
    {
        var notes = new[] { Note(60, 0) };
        var midi = new SpyMidiWriter();
        var xml = new SpyScoreWriter();
        string dirA = Path.Combine(Path.GetTempPath(), $"claudio_listen_{Guid.NewGuid():N}");
        string dirB = Path.Combine(Path.GetTempPath(), $"claudio_listen_{Guid.NewGuid():N}");
        try
        {
            new ListenCommand(Session(notes), midi, midi, _ => { }, musicXmlWriter: null)
                .Run(new FakeSource(), 120, dirA);
            Assert.False(File.Exists(Path.Combine(dirA, "score.musicxml")));

            new ListenCommand(Session(notes), midi, midi, _ => { }, musicXmlWriter: xml)
                .Run(new FakeSource(), 120, dirB);
            Assert.True(File.Exists(Path.Combine(dirB, "score.musicxml")));
            Assert.Equal(1, xml.Writes);
        }
        finally
        {
            if (Directory.Exists(dirA)) Directory.Delete(dirA, recursive: true);
            if (Directory.Exists(dirB)) Directory.Delete(dirB, recursive: true);
        }
    }
}
