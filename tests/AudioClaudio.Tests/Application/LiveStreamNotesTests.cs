using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Application;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral; // Radix2Fft
using AudioClaudio.Tests.Signals;    // SignalGenerator
using Xunit;

namespace AudioClaudio.Tests.Application;

/// <summary>
/// Proves <see cref="TranscriptionPipeline.StreamNotes"/> is a genuinely incremental (lazy, causal)
/// live feed — NOT a batch dump — WITHOUT any audio device. It drives the feed from a finite,
/// in-memory frame source that counts how many frames have been pulled, so we can observe a note
/// being emitted while frames are still unconsumed (a batch implementation would pull every frame
/// before returning the first note).
/// </summary>
public class LiveStreamNotesTests
{
    private const int SR = 44100, N = 1024, H = 256;
    private static readonly SampleRate Rate = new SampleRate(SR);

    // Counts frames as the consumer pulls them, so the test can compare "frames pulled at first
    // note" against "total frames".
    private sealed class CountingFrameSource : IAudioSource
    {
        private readonly IReadOnlyList<Frame> _frames;
        public int Pulled { get; private set; }
        public int Total => _frames.Count;

        public CountingFrameSource(IReadOnlyList<Frame> frames) => _frames = frames;

        public IEnumerable<Frame> Frames
        {
            get
            {
                foreach (var f in _frames)
                {
                    Pulled++;
                    yield return f;
                }
            }
        }
    }

    // Three well-separated sine notes with silence between them, framed like the live path.
    private static IReadOnlyList<Frame> ThreeNoteFrames(out int[] expectedMidi)
    {
        expectedMidi = new[] { 60, 64, 67 };
        int noteLen = (int)(0.30 * SR);
        int gapLen = (int)(0.15 * SR);
        var mono = new List<float>();
        foreach (int midi in expectedMidi)
        {
            mono.AddRange(SignalGenerator.Sine(new Pitch(midi).Frequency(), noteLen, Rate));
            mono.AddRange(new float[gapLen]);
        }
        return Framing.Split(mono.ToArray(), Rate, new FrameParameters(N, H));
    }

    private static TranscriptionPipeline Pipeline() =>
        new TranscriptionPipeline(
            TranscriptionSettings.ForTempo(120) with { FrameSize = N, Hop = H }, new Radix2Fft());

    [Fact]
    [Trait("Category", "Fast")]
    public void FirstNoteIsYieldedBeforeAllFramesAreConsumed()
    {
        var frames = ThreeNoteFrames(out _);
        var source = new CountingFrameSource(frames);

        using var e = Pipeline().StreamNotes(source).GetEnumerator();
        bool gotFirst = e.MoveNext();

        Assert.True(gotFirst, "expected at least one note");
        // The heart of the proof: the first note surfaced while the source still had frames left,
        // so StreamNotes is incremental, not `Transcribe(source).RawEvents` (which pulls all frames
        // before returning anything).
        Assert.True(source.Pulled < source.Total,
            $"first note consumed all {source.Total} frames ({source.Pulled}) — not incremental");
        // And it arrives within a small bounded look-ahead of the first onset (low latency, R10.2).
        Assert.True(source.Pulled <= N / H + 8,
            $"first note took {source.Pulled} frames — beyond the bounded live look-ahead");
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void StreamsOneNotePerPlayedNoteWithCorrectPitchesIncrementally()
    {
        var frames = ThreeNoteFrames(out int[] expectedMidi);
        var source = new CountingFrameSource(frames);

        var streamed = Pipeline().StreamNotes(source).ToList();

        Assert.Equal(expectedMidi, streamed.Select(n => n.Pitch.MidiNumber).ToArray());
        // Onsets are strictly increasing (notes emitted in time order as detected).
        for (int i = 1; i < streamed.Count; i++)
            Assert.True(streamed[i].Onset.Samples > streamed[i - 1].Onset.Samples);
    }
}
