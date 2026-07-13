using AudioClaudio.Cli.Cli;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class CommandLineAppDispatchTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void NoArgs_And_TopLevelHelp_PrintTheSameTopLevelPage()
    {
        var (app, _) = SampleCli.BuildApp();
        var expected = File.ReadAllText(RepoPaths.Fixture("golden", "cli", "top-level-help.txt"));

        foreach (var args in new[] { Array.Empty<string>(), new[] { "--help" } })
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = app.Run(args, stdout, stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(expected, stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Version_PrintsToolNameAndVersion()
    {
        var (app, _) = SampleCli.BuildApp();
        var stdout = new StringWriter();

        var exitCode = app.Run(new[] { "--version" }, stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Equal("claudio 0.2.1\n", stdout.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void CommandHelp_PrintsThePerCommandPage_AndNeverCallsTheHandler()
    {
        var (app, invocations) = SampleCli.BuildApp();
        var stdout = new StringWriter();

        var exitCode = app.Run(new[] { "render", "--help" }, stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Equal(
            File.ReadAllText(RepoPaths.Fixture("golden", "cli", "render-command-help.txt")), stdout.ToString());
        Assert.Empty(invocations);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void ValidCommand_ParsesTypedOptionsAndDispatchesToItsHandler()
    {
        var (app, invocations) = SampleCli.BuildApp();

        var exitCode = app.Run(
            new[] { "transcribe", "recording.wav", "--key", "-1", "--tempo", "132.5" },
            new StringWriter(), new StringWriter());

        Assert.Equal(0, exitCode);
        var (command, parsed) = Assert.Single(invocations);
        Assert.Equal("transcribe", command);
        Assert.Equal("recording.wav", parsed.Argument("in.wav"));
        Assert.Equal(-1, parsed.Int("key"));
        Assert.Equal(132.5, parsed.Double("tempo"));
        Assert.Equal("basicpitch", parsed.String("model")); // declared default, never typed by the user
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void HandlerReturnValue_BecomesTheProcessExitCode()
    {
        var (app, _) = SampleCli.BuildApp(renderHandler: (_, _, _) => 3);

        var exitCode = app.Run(
            new[] { "render", "score.mid", "out.wav" }, new StringWriter(), new StringWriter());

        Assert.Equal(3, exitCode);
    }
}
