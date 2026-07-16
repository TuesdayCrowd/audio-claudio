using System.Linq;
using System.Text;
using AudioClaudio.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class AppBuilderTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Build_registers_exactly_the_nine_commands()
    {
        var app = AppBuilder.Build(new StringBuilder(), noColor: true);

        var names = app.Commands.Select(c => c.Name).OrderBy(n => n).ToArray();

        Assert.Equal(
            new[] { "evaluate", "evaluate-audio", "listen", "notate", "pianize", "play", "render", "separate", "transcribe" },
            names);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("transcribe", new[] { "--tempo", "--out-dir", "--note-names", "--mono", "--model", "--key",
        "--onset-threshold", "--frame-threshold", "--min-note-len", "--legato", "--coarse-rhythm", "--triplets" })]
    [InlineData("notate", new[] { "--out-dir", "--tempo", "--key", "--note-names", "--triplets" })]
    [InlineData("render", new[] { "--soundfont" })]
    [InlineData("play", new[] { "--soundfont" })]
    [InlineData("evaluate", new[] { "--onset-tolerance-ms", "--align", "--warp" })]
    [InlineData("evaluate-audio", new string[0])]
    [InlineData("listen", new[] { "--tempo", "--out-dir", "--view", "--record", "--note-names", "--time-signature", "--soundfont", "--mono" })]
    [InlineData("separate", new[] { "--out-dir", "--model" })]
    [InlineData("pianize", new[] { "--out-dir", "--model", "--tempo", "--key", "--include-vocals", "--note-names", "--triplets", "--soundfont" })]
    public void Each_command_declares_exactly_its_option_surface(string commandName, string[] expectedOptions)
    {
        var app = AppBuilder.Build(new StringBuilder(), noColor: true);
        var cmd = app.Commands.Single(c => c.Name == commandName);

        var actual = cmd.Options.Select(o => o.Name).OrderBy(n => n).ToArray();
        var expected = expectedOptions.OrderBy(n => n).ToArray();

        Assert.Equal(expected, actual);
    }
}
