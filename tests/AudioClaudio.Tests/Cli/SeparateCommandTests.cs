using System;
using System.IO;
using System.Linq;
using System.Text;
using AudioClaudio.Cli;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Cli;

/// <summary>
/// Stage 1.5 — the end-to-end <c>claudio separate &lt;mix.wav&gt;</c> CLI verb: builds the real
/// kernel via <see cref="AppBuilder"/> (mirroring <c>TranscribeHandlerTests</c>) and runs it
/// against the committed Spleeter golden mix, asserting the 5 stem WAVs land in the out-dir and
/// are each independently readable and non-empty.
/// </summary>
public class SeparateCommandTests
{
    private static readonly string[] StemNames = { "vocals", "piano", "drums", "bass", "other" };

    [Fact]
    [Trait("Category", "Slow")] // loads all 5 Spleeter ONNX models
    public void Separate_writes_exactly_five_readable_non_empty_stem_wavs()
    {
        string mixWav = RepoPaths.Fixture("models", "spleeter", "golden", "test_input_mono.wav");
        string dir = Path.Combine(Path.GetTempPath(), $"claudio-separate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var app = AppBuilder.Build(new StringBuilder(), noColor: true);
            int code = app.Run(
                new[] { "separate", mixWav, "--out-dir", dir },
                new StringWriter(), new StringWriter());

            Assert.Equal(0, code);

            string[] expectedFiles = StemNames.Select(n => $"{n}.wav").OrderBy(n => n, StringComparer.Ordinal).ToArray();
            string[] actualFiles = Directory.GetFiles(dir)
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray()!;
            Assert.Equal(expectedFiles, actualFiles);

            foreach (string name in StemNames)
            {
                string path = Path.Combine(dir, $"{name}.wav");
                using var source = WavAudioSource.FromFile(path, new FrameParameters(4096, 4096));
                float[] pcm = Framing.ReconstructMono(source.Frames.ToList());
                Assert.True(pcm.Length > 0, $"{name}.wav yielded no samples");
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
