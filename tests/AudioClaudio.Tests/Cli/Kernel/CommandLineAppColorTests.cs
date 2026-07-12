using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class CommandLineAppColorTests
{
    /// <summary>The control character (decimal 27) that starts every ANSI escape sequence.</summary>
    private const char Escape = (char)27;

    [Fact]
    [Trait("Category", "Fast")]
    public void InjectedInteractiveStyler_ColorsTheHelpHeadings()
    {
        var (app, _) = SampleCli.BuildApp();
        var stdout = new StringWriter();
        var interactive = new AnsiStyler(interactiveTerminal: true, noColorEnvSet: false, noColorFlag: false);

        app.Run(Array.Empty<string>(), stdout, new StringWriter(), styler: interactive);

        Assert.Contains($"{Escape}[1;36mUsage:{Escape}[0m", stdout.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void DefaultStyler_NeverColorsOutput_ForANonConsoleWriter()
    {
        var (app, _) = SampleCli.BuildApp();
        var stdout = new StringWriter();

        app.Run(Array.Empty<string>(), stdout, new StringWriter());

        // Plain help text legitimately contains "[" (e.g. "[options]"), so the real invariant is
        // the absence of an ANSI escape sequence, which always starts with the escape control
        // character (decimal 27) immediately followed by "[" -- never a bare "[". Ordinal
        // comparison is required here: the default (culture-aware) string search treats the
        // escape control character as ignorable, so it would falsely match a bare "[".
        Assert.DoesNotContain($"{Escape}[", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void InjectedInteractiveStyler_ColorsTheErrorKeyword()
    {
        var (app, _) = SampleCli.BuildApp();
        var stderr = new StringWriter();
        var interactive = new AnsiStyler(interactiveTerminal: true, noColorEnvSet: false, noColorFlag: false);

        var exitCode = app.Run(
            new[] { "render", "score.mid", "out.wav", "extra.txt" }, new StringWriter(), stderr, styler: interactive);

        // S5.6: the error keyword is colored (red) on an interactive terminal; the message text is
        // otherwise the same sentence the plain path emits. The offending token stays quoted, not colored.
        Assert.Equal(CommandLineApp.UsageErrorExitCode, exitCode);
        Assert.StartsWith($"{Escape}[1;31merror{Escape}[0m: ", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void NoColorFlag_IsStrippedBeforeCommandParsing_WhereverItAppears()
    {
        var (appBefore, invocationsBefore) = SampleCli.BuildApp();
        var (appAfter, invocationsAfter) = SampleCli.BuildApp();

        var exitBefore = appBefore.Run(
            new[] { "--no-color", "render", "score.mid", "out.wav" }, new StringWriter(), new StringWriter());
        var exitAfter = appAfter.Run(
            new[] { "render", "score.mid", "out.wav", "--no-color" }, new StringWriter(), new StringWriter());

        Assert.Equal(0, exitBefore);
        Assert.Equal(0, exitAfter);
        Assert.Single(invocationsBefore);
        Assert.Single(invocationsAfter);
    }
}
