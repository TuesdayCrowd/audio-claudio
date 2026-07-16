using System;
using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Application.Ports;
using AudioClaudio.Application.UseCases;
using AudioClaudio.Cli.Composition;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Infrastructure.Separation;
using AudioClaudio.Infrastructure.Transcription;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Cli;

/// <summary>
/// <see cref="MultiStemRouting"/> -- the Cli composition helper that builds Stage 2's stem-to-
/// transcriber routing table (DECISIONS.md "Multi-instrument -> piano"): piano -> Transkun,
/// bass/other/vocals -> ONE shared Basic Pitch instance, drums never routed. Loads the real
/// committed ONNX models, so these are [Slow] structural/wiring smoke tests -- the transcribers'
/// own accuracy is proven elsewhere (TranskunTranscriberTests/BasicPitchTranscriberTests).
/// </summary>
public class MultiStemRoutingTests
{
    [Fact]
    [Trait("Category", "Slow")] // loads the Transkun + Basic Pitch ONNX models
    public void Build_routes_piano_to_Transkun_and_the_rest_to_one_shared_BasicPitch_instance()
    {
        (IReadOnlyList<StemRoute> routing, IDisposable transcribers) = MultiStemRouting.Build();
        try
        {
            Assert.Equal(4, routing.Count);

            StemRoute piano = routing.Single(r => r.StemName == "piano");
            StemRoute bass = routing.Single(r => r.StemName == "bass");
            StemRoute other = routing.Single(r => r.StemName == "other");
            StemRoute vocals = routing.Single(r => r.StemName == "vocals");

            Assert.IsType<TranskunTranscriber>(piano.Transcriber);
            Assert.IsType<BasicPitchTranscriber>(bass.Transcriber);
            // bass/other/vocals share ONE Basic Pitch instance -- constructing three would triple
            // the ONNX session's load time/memory for a model that is stateless per call.
            Assert.Same(bass.Transcriber, other.Transcriber);
            Assert.Same(bass.Transcriber, vocals.Transcriber);
            Assert.NotSame(piano.Transcriber, bass.Transcriber);

            Assert.Equal(0, piano.GmProgram);
            Assert.Equal(32, bass.GmProgram);
            Assert.Equal(26, other.GmProgram);
            Assert.Equal(54, vocals.GmProgram);

            Assert.DoesNotContain(routing, r => r.StemName == "drums");
        }
        finally
        {
            transcribers.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Slow")] // separation (5 Spleeter models) + piano/bass/other/vocals transcription
    public void EndToEnd_transcribes_every_pitched_stem_drops_drums_and_rescales_to_44100()
    {
        string mixWav = RepoPaths.Fixture("models", "spleeter", "golden", "test_input_mono.wav");
        using var source = WavAudioSource.FromFile(mixWav, new FrameParameters(4096, 4096));
        using var separator = new SpleeterSourceSeparator(SeparatorModelLocator.Resolve(null), new Radix2Fft());
        IReadOnlyList<SeparatedStem> stems = separator.Separate(source);

        (IReadOnlyList<StemRoute> routing, IDisposable transcribers) = MultiStemRouting.Build();
        try
        {
            var multiStem = new MultiStemTranscriber(routing, new SampleRate(44100));

            IReadOnlyList<StemTranscription> result = multiStem.Transcribe(stems);

            Assert.DoesNotContain(result, r => r.StemName == "drums");
            Assert.Contains(result, r => r.StemName == "piano");
            Assert.Contains(result, r => r.StemName == "bass");
            Assert.Contains(result, r => r.StemName == "other");
            Assert.Contains(result, r => r.StemName == "vocals");

            foreach (StemTranscription stem in result)
            {
                foreach (NoteEvent note in stem.Notes)
                {
                    Assert.Equal(44100, note.Onset.Rate.Hz);
                    Assert.Equal(44100, note.Duration.Rate.Hz);
                }
            }
        }
        finally
        {
            transcribers.Dispose();
        }
    }
}
