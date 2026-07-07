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

    // StreamNotes is the genuinely-incremental live feed (Step 10, R10.3) — a causal detector that
    // emits a note at its ONSET with a PROVISIONAL duration, NOT a batch alias of Transcribe. So it
    // agrees with the batch pass on the things the live view exists to show — how many notes, their
    // pitches, and their onset timing — but NOT on finalized duration (only the batch pass refines
    // that). (Before Step 10 this test asserted byte-identical equality; that only held because
    // StreamNotes was a stopgap `=> Transcribe(...).RawEvents`, which cannot print notes as they
    // occur. CONTRACTS §9 always defined StreamNotes as the incremental live feed.)
    [Trait("Category", "Fast")]
    [Fact]
    public void Pipeline_streams_notes_incrementally_agreeing_with_batch_on_pitch_and_onset()
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

            Assert.Equal(transcribed.Count, streamed.Count);
            for (int i = 0; i < transcribed.Count; i++)
            {
                Assert.Equal(transcribed[i].Pitch.MidiNumber, streamed[i].Pitch.MidiNumber);
                Assert.Equal(transcribed[i].Onset.Samples, streamed[i].Onset.Samples);
            }
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
