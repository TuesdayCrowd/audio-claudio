using System.IO;
using AudioClaudio.Application;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral; // Radix2Fft
using AudioClaudio.Infrastructure.Audio; // WavAudioSource
using AudioClaudio.Infrastructure.Midi; // DryWetMidiWriter

namespace AudioClaudio.Cli.Commands;

/// <summary>
/// Wires the file-based transcription pipeline for
/// <c>claudio transcribe &lt;in.wav&gt; --tempo N [--out-dir .]</c>. Emits <c>raw.mid</c> (the
/// unquantized performance) and <c>score.mid</c> (the quantized score). <c>score.musicxml</c> is
/// added by Step 11 once <c>MusicXmlScoreWriter</c> exists (CONTRACTS §9).
/// </summary>
public static class TranscribeCommand
{
    public static void Run(string inputWav, double tempoBpm, string outDir)
    {
        Directory.CreateDirectory(outDir);

        var settings = TranscriptionSettings.ForTempo(tempoBpm);
        var pipeline = new TranscriptionPipeline(settings, new Radix2Fft()); // Domain FFT (Option A)

        TranscriptionResult result;
        using (var source = WavAudioSource.FromFile(inputWav, new FrameParameters(settings.FrameSize, settings.Hop)))
        {
            result = pipeline.Transcribe(source);
        }

        var tempo = new Tempo(tempoBpm);
        var writer = new DryWetMidiWriter();

        using (var raw = File.Create(Path.Combine(outDir, "raw.mid")))
        {
            writer.Write(result.RawEvents, tempo, raw); // INoteEventWriter: raw performance
        }

        using (var score = File.Create(Path.Combine(outDir, "score.mid")))
        {
            writer.Write(result.Score, score); // IScoreWriter: quantized score
        }
    }
}
