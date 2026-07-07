using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AudioClaudio.Application;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral; // Radix2Fft
using AudioClaudio.Infrastructure.Audio; // WavAudioSource
using AudioClaudio.Tests.Signals; // WavWriter
using Xunit;

namespace AudioClaudio.Tests.ClosedLoop;

public sealed class ClosedLoopDeterminismTests
{
    [Trait("Category", "Fast")]
    [Fact]
    public void Domain_output_is_deterministic_for_a_fixed_case()
    {
        var c = ClosedLoopGen.Fixed();

        float[] pcm1 = ClosedLoop.CreateSynthesizer().Render(c.Events, c.Rate);
        float[] pcm2 = ClosedLoop.CreateSynthesizer().Render(c.Events, c.Rate);
        Assert.Equal(Sha(pcm1), Sha(pcm2)); // synthesis determinism (R8.2)

        Assert.Equal(TranscribeToGrid(c, pcm1), TranscribeToGrid(c, pcm2)); // transcription determinism
    }

    private static IReadOnlyList<NoteGridPosition> TranscribeToGrid(ClosedLoopCase c, float[] pcm)
    {
        var settings = TranscriptionSettings.ForTempo(c.TempoBpm) with
        {
            TimeSignature = c.TimeSignature,
            Subdivision = c.Subdivision,
        };
        string wav = Path.Combine(Path.GetTempPath(), $"acl_det_{Guid.NewGuid():N}.wav");
        WavWriter.WriteMonoFile(wav, pcm, c.Rate);
        try
        {
            using var source = WavAudioSource.FromFile(wav, new FrameParameters(settings.FrameSize, settings.Hop));
            return ScoreGrid.From(new TranscriptionPipeline(settings, new Radix2Fft()).Transcribe(source).Score);
        }
        finally
        {
            if (File.Exists(wav))
            {
                File.Delete(wav);
            }
        }
    }

    private static string Sha(float[] pcm) =>
        Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(pcm.AsSpan())));
}
