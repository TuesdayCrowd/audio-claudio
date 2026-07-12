using AudioClaudio.Cli.Cli;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class HelpRendererTopLevelTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void RenderTopLevel_MatchesTheGoldenFragment()
    {
        var commands = new[] { SampleCli.Transcribe, SampleCli.Render };
        var styler = new AnsiStyler(interactiveTerminal: false, noColorEnvSet: false, noColorFlag: false);

        var actual = HelpRenderer.RenderTopLevel(SampleCli.ToolName, SampleCli.ToolSummary, commands, styler);

        var expected = File.ReadAllText(RepoPaths.Fixture("golden", "cli", "top-level-help.txt"));
        Assert.Equal(expected, actual);
    }
}
