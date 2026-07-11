using System;
using System.Diagnostics;
using System.Linq;
using AudioClaudio.Application;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Infrastructure.Transcription;
using AudioClaudio.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace AudioClaudio.Tests.Transcription;

/// <summary>
/// v2 Stage 4d — the Transkun engine (<see cref="TranskunTranscriber"/>) end-to-end on the two-bar render.
/// Core-first: frame-resolution timing, no velocity. Diagnostic here; the ≥99 % PyTorch parity gate is
/// <c>TranskunParityTests</c>.
/// </summary>
public class TranskunTranscriberTests
{
    private readonly ITestOutputHelper _out;

    public TranskunTranscriberTests(ITestOutputHelper output) => _out = output;

    private static string ModelDir => RepoPaths.Fixture("models", "transkun");

    [Fact]
    [Trait("Category", "Slow")] // loads the 53 MB ONNX; runs 3 × 16 s segments
    public void TranscribesTwoBar_ToTheCMajorScale()
    {
        using var source = WavAudioSource.FromFile(
            RepoPaths.Fixture("golden", "two-bar.wav"), new FrameParameters(1024, 256));
        using var engine = new TranskunTranscriber(ModelDir, new Radix2Fft());

        var sw = Stopwatch.StartNew();
        (System.Collections.Generic.IReadOnlyList<NoteEvent> notes, var pedal) = engine.TranscribeDetailed(source);
        sw.Stop();

        _out.WriteLine($"transcribed in {sw.Elapsed.TotalSeconds:F1}s -> {notes.Count} notes, {pedal.Count} pedal changes");
        foreach (NoteEvent n in notes)
        {
            _out.WriteLine($"  midi {n.Pitch.MidiNumber,3}  on {n.Onset.Samples / 44100.0:F3}s  dur {n.Duration.Samples / 44100.0:F3}s");
        }

        // Native transkun recovers the C-major scale 60,62,64,65,67,69,71,72 (+ one spurious short 76).
        var pitches = notes.Select(n => n.Pitch.MidiNumber).ToHashSet();
        foreach (int expected in new[] { 60, 62, 64, 65, 67, 69, 71, 72 })
        {
            Assert.Contains(expected, pitches);
        }

        // Onsets land near the half-second grid the scale was played on.
        NoteEvent first = notes.First(n => n.Pitch.MidiNumber == 60);
        Assert.True(first.Onset.Samples / 44100.0 < 0.06, "C4 should start at ~0 s");
    }
}
