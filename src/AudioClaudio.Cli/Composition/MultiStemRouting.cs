using System;
using System.Collections.Generic;
using AudioClaudio.Application.UseCases;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Infrastructure.Transcription;

namespace AudioClaudio.Cli.Composition;

/// <summary>
/// Builds the Stage 2 stem-to-transcriber routing table for <see cref="MultiStemTranscriber"/> --
/// the ONE place in the codebase that decides WHICH transcriber engine handles which separated stem
/// (DECISIONS.md "Multi-instrument -> piano"). <c>piano</c> routes to <see cref="TranskunTranscriber"/>
/// (its &#8805;99% piano-parity specialty vs. native PyTorch); <c>bass</c>/<c>other</c>/<c>vocals</c>
/// share a SINGLE <see cref="BasicPitchTranscriber"/> instance -- constructing three would triple the
/// ONNX session's load time/memory for a model that is stateless per call. GM programs tag each
/// stem for a later multi-track MIDI stage: piano 0 (acoustic grand piano), bass 32 (acoustic bass),
/// other 26 (jazz guitar -- a reasonable stand-in for an unclassified pitched stem), vocals 54
/// (synth voice -- the GM patch program 54 actually selects). <c>drums</c> is deliberately never given an entry -- <see cref="MultiStemTranscriber"/>
/// silently skips any stem absent from its routing table, which IS the documented drop-drums decision
/// (a drum stem has no pitched notes).
///
/// <see cref="MultiStemTranscriber"/> itself never constructs a concrete transcriber (it depends only
/// on the <see cref="AudioClaudio.Application.Ports.ITranscriber"/> port) -- that composition-root
/// knowledge lives here, in the Cli, exactly as <see cref="TranskunModelLocator"/>/
/// <see cref="ModelLocator"/>/<see cref="SeparatorModelLocator"/>'s own model-path resolution does.
/// </summary>
public static class MultiStemRouting
{
    public const int PianoGmProgram = 0;
    public const int BassGmProgram = 32;
    public const int OtherGmProgram = 26;
    public const int VocalsGmProgram = 54;

    /// <summary>
    /// Constructs the routing table plus an <see cref="IDisposable"/> that disposes every
    /// transcriber it constructed (the Transkun engine + the ONE shared Basic Pitch engine) exactly
    /// once. Callers own the returned handle and MUST dispose it (typically via <c>using</c>) once
    /// done, e.g. <c>using var routing = MultiStemRouting.Build();</c>.
    /// </summary>
    /// <param name="transkunModelDir">Explicit Transkun model directory, or null to resolve the
    /// committed fixture via <see cref="TranskunModelLocator"/>.</param>
    /// <param name="basicPitchModelPath">Explicit Basic Pitch model path, or null to resolve the
    /// committed fixture via <see cref="ModelLocator"/>.</param>
    public static (IReadOnlyList<StemRoute> Routing, IDisposable Transcribers) Build(
        string? transkunModelDir = null, string? basicPitchModelPath = null)
    {
        var transkun = new TranskunTranscriber(TranskunModelLocator.Resolve(transkunModelDir), new Radix2Fft());
        BasicPitchTranscriber basicPitch;
        try
        {
            basicPitch = new BasicPitchTranscriber(ModelLocator.Resolve(basicPitchModelPath));
        }
        catch
        {
            // Don't leak the already-constructed Transkun session if Basic Pitch construction fails (e.g.
            // a missing/corrupt Basic Pitch fixture): no caller ever receives the StemTranscribers handle
            // to dispose it, so clean it up here before rethrowing.
            transkun.Dispose();
            throw;
        }

        var routing = new List<StemRoute>
        {
            new("piano", PianoGmProgram, transkun),
            new("bass", BassGmProgram, basicPitch),
            new("other", OtherGmProgram, basicPitch),
            new("vocals", VocalsGmProgram, basicPitch),
        };

        return (routing, new StemTranscribers(transkun, basicPitch));
    }

    // Disposes the Transkun engine and the one shared Basic Pitch engine exactly once each.
    private sealed class StemTranscribers : IDisposable
    {
        private readonly TranskunTranscriber _transkun;
        private readonly BasicPitchTranscriber _basicPitch;

        public StemTranscribers(TranskunTranscriber transkun, BasicPitchTranscriber basicPitch)
        {
            _transkun = transkun;
            _basicPitch = basicPitch;
        }

        public void Dispose()
        {
            _transkun.Dispose();
            _basicPitch.Dispose();
        }
    }
}
