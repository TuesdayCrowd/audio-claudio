using System.Threading;
using AudioClaudio.Application;
using AudioClaudio.Application.Ports;
using AudioClaudio.Application.UseCases;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Application;

public class LiveTranscriptionSessionTests
{
    private static readonly SampleRate Rate = new SampleRate(44100);

    private static NoteEvent Note(int midi, long onset, long dur) =>
        new NoteEvent(new Pitch(midi),
                      new SamplePosition(onset, Rate),
                      new SampleDuration(dur, Rate),
                      velocity: 100);

    private static TranscriptionResult Batch(IReadOnlyList<NoteEvent> events)
    {
        var grid = new QuantizationGrid(Rate, new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth);
        return new TranscriptionResult(Quantizer.Quantize(events, grid), events);
    }

    private sealed class FrameListSource : IAudioSource
    {
        private readonly IReadOnlyList<Frame> _frames;
        public FrameListSource(IReadOnlyList<Frame> frames) => _frames = frames;
        public IEnumerable<Frame> Frames => _frames;
    }

    // The live print channel (streamNotes) and the accurate-files channel (batch transcribe)
    // are SEPARATE: the batch result — not the live print — drives result.Events/.Score (R10.3).
    [Fact]
    [Trait("Category", "Fast")]
    public void PrintsEachLiveNoteThenReturnsTheBatchTranscription()
    {
        var liveNotes = new[] { Note(60, 0, 100), Note(62, 22050, 100), Note(64, 44100, 100) };
        var batchEvents = new[] { Note(60, 0, 22050) }; // deliberately different from the live print
        IEnumerable<NoteEvent> Stream(IAudioSource s) { foreach (var n in liveNotes) yield return n; }
        var session = new LiveTranscriptionSession(Stream, _ => Batch(batchEvents));

        var printed = new List<int>();
        var result = session.Run(new FrameListSource(Array.Empty<Frame>()),
                                 onNote: n => printed.Add(n.Pitch.MidiNumber));

        Assert.Equal(new[] { 60, 62, 64 }, printed);                    // printed live, in order
        Assert.Single(result.Events);                                    // files come from the BATCH
        Assert.Equal(60, result.Events[0].Pitch.MidiNumber);
        Assert.Equal(120.0, result.Score.Tempo.BeatsPerMinute);          // batch score carries the grid tempo
    }

    // The recorder tees every frame from the (once-only) live source to the batch pass.
    [Fact]
    [Trait("Category", "Fast")]
    public void TeesEveryFrameFromTheLiveSourceToTheBatchPass()
    {
        var frames = new List<Frame>();
        for (int i = 0; i < 5; i++)
            frames.Add(new Frame(new float[8], new SamplePosition(i * 8, Rate)));

        // A stand-in for the real incremental detector: it fully drains the source's frames.
        IEnumerable<NoteEvent> DrainingStream(IAudioSource s) { foreach (var _ in s.Frames) { } yield break; }

        int batchFrameCount = -1;
        TranscriptionResult CountingBatch(IAudioSource s)
        {
            batchFrameCount = s.Frames.Count();
            return Batch(Array.Empty<NoteEvent>());
        }

        var session = new LiveTranscriptionSession(DrainingStream, CountingBatch);
        session.Run(new FrameListSource(frames), onNote: _ => { });

        Assert.Equal(5, batchFrameCount); // all 5 frames were replayed to the batch transcription
    }

    // The result surfaces the exact captured frames too (used by `listen --record` to write
    // input.wav via Framing.ReconstructMono) — same frames, same order, independent of the
    // batch note/score output.
    [Fact]
    [Trait("Category", "Fast")]
    public void ResultExposesTheCapturedFramesInOrder()
    {
        var frames = new List<Frame>();
        for (int i = 0; i < 5; i++)
            frames.Add(new Frame(new float[8], new SamplePosition(i * 8, Rate)));

        // A stand-in for the real incremental detector: it fully drains the source's frames.
        IEnumerable<NoteEvent> DrainingStream(IAudioSource s) { foreach (var _ in s.Frames) { } yield break; }

        var session = new LiveTranscriptionSession(DrainingStream, _ => Batch(Array.Empty<NoteEvent>()));
        var result = session.Run(new FrameListSource(frames), onNote: _ => { });

        Assert.Equal(frames, result.CapturedFrames); // same frames, same order (reference equality)
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void StopsPrintingWhenCancellationRequested()
    {
        var liveNotes = new[] { Note(60, 0, 100), Note(62, 100, 100), Note(64, 200, 100) };
        IEnumerable<NoteEvent> Stream(IAudioSource s) { foreach (var n in liveNotes) yield return n; }
        var session = new LiveTranscriptionSession(Stream, _ => Batch(Array.Empty<NoteEvent>()));
        using var cts = new CancellationTokenSource();

        var printed = new List<int>();
        session.Run(new FrameListSource(Array.Empty<Frame>()),
                    onNote: n => { printed.Add(n.Pitch.MidiNumber); cts.Cancel(); }, ct: cts.Token);

        Assert.Single(printed); // cancelled after the first live note
        Assert.Equal(60, printed[0]);
    }
}
