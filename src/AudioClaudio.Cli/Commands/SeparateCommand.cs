using System.Collections.Generic;
using System.IO;
using System.Linq;
using AudioClaudio.Application.Ports;
using AudioClaudio.Cli.Composition;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral; // Radix2Fft
using AudioClaudio.Infrastructure.Audio; // WavAudioSource, WavFileWriter
using AudioClaudio.Infrastructure.Separation; // SpleeterSourceSeparator

namespace AudioClaudio.Cli.Commands;

/// <summary>
/// Wires the Spleeter source-separation adapter for
/// <c>claudio separate &lt;mix.wav&gt; [--out-dir out] [--model dir]</c>: splits a mixed
/// recording into its 5 stems (vocals/piano/drums/bass/other) and writes each as its own WAV
/// at Spleeter's fixed 44100 Hz working rate. Root-only output, mirroring
/// <see cref="TranscribeCommand"/> — no timestamped session archive (Stage 1.5).
/// </summary>
public static class SeparateCommand
{
    // Framing used only to READ the input mix; SpleeterSourceSeparator reconstructs the whole
    // buffer via Framing.ReconstructMono internally and resamples as needed, so any size/hop
    // works here -- non-overlapping keeps reconstruction trivial and bounds tail padding to
    // one frame's worth of trailing silence.
    private static readonly FrameParameters InputFrameParameters = new(4096, 4096);

    // Spleeter's fixed working rate (SpleeterSourceSeparator.TargetSampleRateHz, private to that
    // class); every stem it returns is already at this rate regardless of the input file's rate.
    private static readonly SampleRate OutputRate = new(44100);

    public static IReadOnlyList<SeparatedStem> Run(string mixWav, string outDir, string? modelDir)
    {
        Directory.CreateDirectory(outDir);

        using var source = WavAudioSource.FromFile(mixWav, InputFrameParameters);
        using var separator = new SpleeterSourceSeparator(SeparatorModelLocator.Resolve(modelDir), new Radix2Fft());

        IReadOnlyList<SeparatedStem> stems = separator.Separate(source);
        foreach (SeparatedStem stem in stems)
        {
            float[] pcm = Framing.ReconstructMono(stem.Audio.Frames.ToList());
            WavFileWriter.Write(Path.Combine(outDir, $"{stem.Name}.wav"), pcm, OutputRate);
        }

        return stems;
    }
}
