using System.IO;
using AudioClaudio.Cli;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class NotateHandlerTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Notate_writes_score_musicxml_and_score_mid()
    {
        var app = AppBuilder.Build(new System.Text.StringBuilder(), noColor: true);
        string dir = Path.Combine(Path.GetTempPath(), $"claudio-notate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            int code = app.Run(
                new[] { "notate", RepoPaths.Fixture("golden", "two-bar.mid"), "--out-dir", dir, "--tempo", "120" },
                new StringWriter(), new StringWriter());

            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(dir, "score.mid")));
            Assert.True(File.Exists(Path.Combine(dir, "score.musicxml")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Notate_rejects_an_out_of_range_key_with_a_clean_message()
    {
        var app = AppBuilder.Build(new System.Text.StringBuilder(), noColor: true);
        var stderr = new StringWriter();

        int code = app.Run(
            new[] { "notate", RepoPaths.Fixture("golden", "two-bar.mid"), "--key", "8" },
            new StringWriter(), stderr);

        Assert.Equal(1, code);
        Assert.Contains("--key", stderr.ToString());
    }
}
