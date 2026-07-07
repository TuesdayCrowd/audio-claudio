using System;
using System.IO;
using System.Linq;
using AudioClaudio.Application;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral; // Radix2Fft
using AudioClaudio.Infrastructure.Audio; // WavAudioSource (composition root role — test only)
using AudioClaudio.Tests.Signals; // SignalGenerator, WavWriter (Step 2 test utilities)
using Xunit;

namespace AudioClaudio.Tests.ClosedLoop;

public sealed class PipelineIntegrationTests
{
    [Trait("Category", "Fast")]
    [Fact]
    public void Pipeline_transcribes_a_single_sustained_note_to_one_event()
    {
        var rate = new SampleRate(44100);
        float[] pcm = SignalGenerator.HarmonicStack(
            new Pitch(69).Frequency(), (int)(1.0 * rate.Hz), rate, partials: 6, decay: 1.0);

        var wav = Path.Combine(Path.GetTempPath(), $"acl_pipeline_{Guid.NewGuid():N}.wav");
        try
        {
            WavWriter.WriteMonoFile(wav, pcm, rate);

            var pipeline = new TranscriptionPipeline(TranscriptionSettings.ForTempo(120), new Radix2Fft());
            using var source = WavAudioSource.FromFile(wav, new FrameParameters(2048, 512));

            TranscriptionResult result = pipeline.Transcribe(source);

            Assert.Single(result.RawEvents);
            Assert.Equal(69, result.RawEvents[0].Pitch.MidiNumber);

            // The quantized score carries exactly one note element (flattened inline here;
            // the reusable ScoreGrid.From helper arrives in Task 3).
            var notes = result.Score.Measures
                .SelectMany(m => m.Elements)
                .Where(e => e.Kind == ElementKind.Note)
                .ToList();
            Assert.Single(notes);
            Assert.Equal(69, notes[0].Pitch!.Value.MidiNumber);
        }
        finally
        {
            if (File.Exists(wav))
            {
                File.Delete(wav);
            }
        }
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void Pipeline_streams_the_same_raw_events_as_Transcribe()
    {
        var rate = new SampleRate(44100);
        float[] pcm = SignalGenerator.HarmonicStack(
            new Pitch(60).Frequency(), (int)(0.5 * rate.Hz), rate, partials: 6, decay: 1.0);

        var wav = Path.Combine(Path.GetTempPath(), $"acl_pipeline_{Guid.NewGuid():N}.wav");
        try
        {
            WavWriter.WriteMonoFile(wav, pcm, rate);

            var pipeline = new TranscriptionPipeline(TranscriptionSettings.ForTempo(120), new Radix2Fft());

            using var source1 = WavAudioSource.FromFile(wav, new FrameParameters(2048, 512));
            var streamed = pipeline.StreamNotes(source1).ToList();

            using var source2 = WavAudioSource.FromFile(wav, new FrameParameters(2048, 512));
            var transcribed = pipeline.Transcribe(source2).RawEvents;

            Assert.Equal(transcribed, streamed);
        }
        finally
        {
            if (File.Exists(wav))
            {
                File.Delete(wav);
            }
        }
    }
}
