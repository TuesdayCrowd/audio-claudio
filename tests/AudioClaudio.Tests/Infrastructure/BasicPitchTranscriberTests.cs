using System.Linq;
using AudioClaudio.Application;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Transcription;
using AudioClaudio.Tests.Signals;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

/// <summary>
/// End-to-end proof of polyphony: a sustained TWO-note chord (A4 + C5) played into the Basic Pitch
/// transcriber must come back with BOTH pitches — something the monophonic YIN pipeline can never do.
/// Uses harmonic (piano-like) tones at 44.1 kHz so the transcriber's resample-to-22.05 kHz path runs.
/// </summary>
public class BasicPitchTranscriberTests
{
    private static readonly SampleRate Rate = new(44100);

    [Fact]
    [Trait("Category", "Slow")] // loads + runs the ONNX model
    public void Transcribes_a_two_note_chord_as_both_pitches()
    {
        int n = (int)(2.5 * Rate.Hz); // 2.5 s so it clears the model's ~2 s window
        float[] a4 = SignalGenerator.HarmonicStack(440.0, n, Rate, partials: 6, decay: 1.0, amplitude: 0.4);
        float[] c5 = SignalGenerator.HarmonicStack(523.25, n, Rate, partials: 6, decay: 1.0, amplitude: 0.4);
        var mix = new float[n];
        for (int i = 0; i < n; i++)
        {
            mix[i] = a4[i] + c5[i]; // two simultaneous notes
        }

        var source = new InMemoryAudioSource(mix, Rate, new FrameParameters(1024, 256));
        using var transcriber = new BasicPitchTranscriber(RepoPaths.Fixture("models", "basic-pitch-nmp.onnx"));

        TranscriptionResult result = transcriber.Transcribe(source);

        var pitches = result.RawEvents.Select(e => e.Pitch.MidiNumber).ToHashSet();
        Assert.Contains(69, pitches); // A4
        Assert.Contains(72, pitches); // C5
    }
}
