using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class CommandLineAppValidationTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void MalformedNumber_NamesTheFlagTheKindAndTheToken()
    {
        var (app, _) = SampleCli.BuildApp();
        var stderr = new StringWriter();

        var exitCode = app.Run(
            new[] { "transcribe", "recording.wav", "--key", "0", "--tempo", "abc" },
            new StringWriter(), stderr);

        Assert.Equal(CommandLineApp.UsageErrorExitCode, exitCode);
        Assert.Equal("error: --tempo expects a number, got 'abc'.\n", stderr.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void MissingValue_NamesTheFlag()
    {
        var (app, _) = SampleCli.BuildApp();
        var stderr = new StringWriter();

        var exitCode = app.Run(new[] { "transcribe", "recording.wav", "--key" }, new StringWriter(), stderr);

        Assert.Equal(CommandLineApp.UsageErrorExitCode, exitCode);
        Assert.Equal("error: --key expects a value.\n", stderr.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void MissingRequiredOption_NamesTheFlag()
    {
        var (app, _) = SampleCli.BuildApp();
        var stderr = new StringWriter();

        var exitCode = app.Run(new[] { "transcribe", "recording.wav" }, new StringWriter(), stderr);

        Assert.Equal(CommandLineApp.UsageErrorExitCode, exitCode);
        Assert.Equal("error: --key is required.\n", stderr.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void MissingRequiredArgument_NamesItInAngleBrackets()
    {
        var (app, _) = SampleCli.BuildApp();
        var stderr = new StringWriter();

        var exitCode = app.Run(new[] { "transcribe" }, new StringWriter(), stderr);

        Assert.Equal(CommandLineApp.UsageErrorExitCode, exitCode);
        Assert.Equal("error: missing required argument <in.wav>.\n", stderr.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void UnexpectedExtraArgument_NamesTheOffendingToken()
    {
        var (app, _) = SampleCli.BuildApp();
        var stderr = new StringWriter();

        var exitCode = app.Run(
            new[] { "render", "score.mid", "out.wav", "extra.txt" }, new StringWriter(), stderr);

        Assert.Equal(CommandLineApp.UsageErrorExitCode, exitCode);
        Assert.Equal("error: unexpected argument 'extra.txt'.\n", stderr.ToString());
    }
}
