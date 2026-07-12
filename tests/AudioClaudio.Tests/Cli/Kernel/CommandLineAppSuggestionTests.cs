using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class CommandLineAppSuggestionTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void UnknownCommand_WithACloseMatch_SuggestsIt()
    {
        var (app, _) = SampleCli.BuildApp();
        var stderr = new StringWriter();

        var exitCode = app.Run(new[] { "trascribe" }, new StringWriter(), stderr);

        Assert.Equal(CommandLineApp.UsageErrorExitCode, exitCode);
        Assert.Equal("error: unknown command 'trascribe'. Did you mean 'transcribe'?\n", stderr.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void UnknownCommand_WithNothingClose_OmitsTheSuggestion()
    {
        var (app, _) = SampleCli.BuildApp();
        var stderr = new StringWriter();

        var exitCode = app.Run(new[] { "frobnicate" }, new StringWriter(), stderr);

        Assert.Equal(CommandLineApp.UsageErrorExitCode, exitCode);
        Assert.Equal("error: unknown command 'frobnicate'.\n", stderr.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void UnknownOption_WithACloseMatch_SuggestsIt()
    {
        var (app, _) = SampleCli.BuildApp();
        var stderr = new StringWriter();

        var exitCode = app.Run(
            new[] { "transcribe", "recording.wav", "--key", "0", "--modl", "transkun" },
            new StringWriter(), stderr);

        Assert.Equal(CommandLineApp.UsageErrorExitCode, exitCode);
        Assert.Equal("error: unknown option '--modl'. Did you mean '--model'?\n", stderr.ToString());
    }
}
