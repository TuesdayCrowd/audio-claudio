using System.IO;
using AudioClaudio.Cli;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class RenderPlayHandlerTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Render_command_writes_a_wav_matching_the_golden_within_tolerance()
    {
        var app = AppBuilder.Build(new System.Text.StringBuilder(), noColor: true);
        string midiPath = RepoPaths.Fixture("golden", "two-bar.mid");
        string outPath = Path.Combine(Path.GetTempPath(), $"claudio-render-{Guid.NewGuid():N}.wav");

        try
        {
            var stdout = new StringWriter();
            int code = app.Run(new[] { "render", midiPath, outPath }, stdout, new StringWriter());

            Assert.Equal(0, code);
            Assert.True(File.Exists(outPath));
            byte[] actualWav = File.ReadAllBytes(outPath);
            byte[] expectedWav = File.ReadAllBytes(RepoPaths.Fixture("golden", "two-bar.wav"));
            WavGoldenComparer.AssertWithinTolerance(expectedWav, actualWav);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Render_reports_a_friendly_error_for_a_missing_input_file()
    {
        var app = AppBuilder.Build(new System.Text.StringBuilder(), noColor: true);
        var stderr = new StringWriter();

        int code = app.Run(new[] { "render", "does-not-exist.mid", "out.wav" }, new StringWriter(), stderr);

        Assert.Equal(1, code);
        Assert.Contains("input file 'does-not-exist.mid' not found", stderr.ToString());
    }
}
