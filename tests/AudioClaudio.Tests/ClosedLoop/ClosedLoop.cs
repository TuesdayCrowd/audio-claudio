using System;
using System.Collections.Generic;
using System.IO;
using AudioClaudio.Application;
using AudioClaudio.Application.Ports; // ISynthesizer
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral; // Radix2Fft
using AudioClaudio.Infrastructure.Audio; // WavAudioSource
using AudioClaudio.Infrastructure.Synthesis; // MeltySynthSynthesizer
using AudioClaudio.Tests.Signals; // WavWriter

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>Runs one trial of transcribe . synthesize ~ id and reports mismatches.</summary>
public static class ClosedLoop
{
    public static ISynthesizer CreateSynthesizer() => new MeltySynthSynthesizer(Fixtures.SoundFontPath);

    /// <summary>Strict R9.2 (count, pitch, onset, duration): for the audible-capped <see
    /// cref="ClosedLoopGen.Cases"/> corpus. Quarantines and throws on any divergence.</summary>
    public static void RunCase(ClosedLoopCase c)
        => Run(c, ClosedLoopComparer.Compare);

    /// <summary>Count + pitch + onset only (NOT duration): for the uncapped full-keyboard <see
    /// cref="ClosedLoopGen.FullRangeCases"/> corpus, where the highest pitches cannot sustain an
    /// audible eighth so duration is not asserted. Quarantines and throws on any divergence.</summary>
    public static void RunFullRangeCase(ClosedLoopCase c)
        => Run(c, ClosedLoopComparer.CompareCountPitchOnset);

    private static void Run(
        ClosedLoopCase c,
        Func<IReadOnlyList<NoteGridPosition>, IReadOnlyList<NoteGridPosition>, int, ClosedLoopComparison> compare)
    {
        float[] pcm = CreateSynthesizer().Render(c.Events, c.Rate);

        var settings = TranscriptionSettings.ForTempo(c.TempoBpm) with
        {
            TimeSignature = c.TimeSignature,
            Subdivision = c.Subdivision,
        };

        // Reference: quantizing the grid-exact performance reproduces it exactly (Step 6 property).
        var referenceGrid = new QuantizationGrid(
            c.Rate, new Tempo(c.TempoBpm), c.TimeSignature, c.Subdivision);
        var expected = ScoreGrid.From(Quantizer.Quantize(c.Events, referenceGrid));

        string wav = Path.Combine(Path.GetTempPath(), $"acl_cl_{c.Id()}.wav");
        WavWriter.WriteMonoFile(wav, pcm, c.Rate);
        try
        {
            using var source = WavAudioSource.FromFile(wav, new FrameParameters(settings.FrameSize, settings.Hop));
            var pipeline = new TranscriptionPipeline(settings, new Radix2Fft());
            var actual = ScoreGrid.From(pipeline.Transcribe(source).Score);

            var cmp = compare(expected, actual, 1);
            if (!cmp.IsMatch)
            {
                string dir = Quarantine.Persist(c, pcm);
                throw new ClosedLoopMismatchException(
                    $"case {c.Id()} quarantined in {dir}: {cmp.Detail}\n{c.Describe()}");
            }
        }
        finally
        {
            if (File.Exists(wav))
            {
                File.Delete(wav); // temp copy only; quarantine wrote its own
            }
        }
    }
}
