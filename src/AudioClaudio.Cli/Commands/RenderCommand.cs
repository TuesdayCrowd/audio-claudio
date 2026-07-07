using System.Collections.Generic;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Audio;

namespace AudioClaudio.Cli.Commands;

/// <summary>The <c>render</c> CLI command: notes in, a deterministic WAV out (R8.3).</summary>
public static class RenderCommand
{
    /// <summary>Renders notes to PCM via the synthesizer and writes a deterministic WAV.</summary>
    public static void RenderToWav(
        IReadOnlyList<NoteEvent> notes,
        ISynthesizer synthesizer,
        SampleRate sampleRate,
        string outputWavPath)
    {
        float[] pcm = synthesizer.Render(notes, sampleRate);
        WavFileWriter.Write(outputWavPath, pcm, sampleRate);
    }
}
