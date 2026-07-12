using AudioClaudio.Cli.Cli;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class HelpRendererCommandTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void RenderCommand_MatchesTheGoldenFragment_ForRender()
    {
        var styler = new AnsiStyler(interactiveTerminal: false, noColorEnvSet: false, noColorFlag: false);

        var actual = HelpRenderer.RenderCommand(SampleCli.ToolName, SampleCli.Render, styler);

        var expected = File.ReadAllText(RepoPaths.Fixture("golden", "cli", "render-command-help.txt"));
        Assert.Equal(expected, actual);
    }
}
