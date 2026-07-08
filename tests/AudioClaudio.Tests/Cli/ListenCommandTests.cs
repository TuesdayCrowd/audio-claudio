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

    [Fact]
    [Trait("Category", "Fast")]
    public void InvokesOnLiveNoteForEveryLiveNoteAndOnFinalScoreOnceWithTheBatchScore()
    {
        var notes = new[] { Note(60, 0), Note(62, 22050) };
        var midi = new SpyMidiWriter();
        var liveNotes = new List<int>();
        Score? finalScore = null;
        string dir = Path.Combine(Path.GetTempPath(), $"claudio_listen_{Guid.NewGuid():N}");
        try
        {
            var cmd = new ListenCommand(Session(notes), midi, midi, _ => { }, musicXmlWriter: null,
                                        onLiveNote: n => liveNotes.Add(n.Pitch.MidiNumber),
                                        onFinalScore: s => finalScore = s);
            var result = cmd.Run(new FakeSource(), tempoBpm: 120, outDir: dir);

            Assert.Equal(new[] { 60, 62 }, liveNotes);      // fired once per live note, in order
            Assert.NotNull(finalScore);
            Assert.Equal(result.Score, finalScore);          // the BATCH score, not a live approximation
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    // The optional live-view hooks must never be able to break CORE listen (transcribe + write the
    // MIDI/MusicXML trio). A hook that throws on every note -- e.g. the live server died mid-session
    // -- must still let Run complete, still write all three files, and still fire onFinalScore.
    [Fact]
    [Trait("Category", "Fast")]
    public void AThrowingOnLiveNoteHookStillWritesTheFileTrioAndFiresOnFinalScore()
    {
        var notes = new[] { Note(60, 0), Note(62, 22050) };
        var midi = new SpyMidiWriter();
        var xml = new SpyScoreWriter();
        Score? finalScore = null;
        string dir = Path.Combine(Path.GetTempPath(), $"claudio_listen_{Guid.NewGuid():N}");
        try
        {
            var cmd = new ListenCommand(Session(notes), midi, midi, _ => { }, musicXmlWriter: xml,
                                        onLiveNote: _ => throw new InvalidOperationException("view died"),
                                        onFinalScore: s => finalScore = s);

            var result = cmd.Run(new FakeSource(), tempoBpm: 120, outDir: dir); // must not throw

            Assert.True(File.Exists(Path.Combine(dir, "raw.mid")));         // core output intact
            Assert.True(File.Exists(Path.Combine(dir, "score.mid")));
            Assert.True(File.Exists(Path.Combine(dir, "score.musicxml")));
            Assert.Equal(1, midi.EventWrites);
            Assert.Equal(1, midi.ScoreWrites);
            Assert.Equal(1, xml.Writes);
            Assert.NotNull(finalScore);                                     // onFinalScore still fired
            Assert.Equal(result.Score, finalScore);
            Assert.Equal(2, result.Events.Count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    // The two ORIGINAL tests above construct ListenCommand without onLiveNote/onFinalScore --
    // this confirms both new hooks are genuinely optional and behavior is unaffected when absent.
    [Fact]
    [Trait("Category", "Fast")]
    public void OnLiveNoteAndOnFinalScoreDefaultToNullWithoutError()
    {
        var notes = new[] { Note(60, 0) };
        var midi = new SpyMidiWriter();
        string dir = Path.Combine(Path.GetTempPath(), $"claudio_listen_{Guid.NewGuid():N}");
        try
        {
            var cmd = new ListenCommand(Session(notes), midi, midi, _ => { }); // no new params at all
            var result = cmd.Run(new FakeSource(), tempoBpm: 120, outDir: dir);
            Assert.Single(result.Events);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
