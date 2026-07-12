using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class AnsiStylerTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Enabled_WhenInteractiveAndNoOverridesSuppressIt()
    {
        var styler = new AnsiStyler(interactiveTerminal: true, noColorEnvSet: false, noColorFlag: false);

        Assert.True(styler.Enabled);
        Assert.Equal("\u001b[1;36mUsage:\u001b[0m", styler.Heading("Usage:"));
        Assert.Equal("\u001b[1;31merror\u001b[0m", styler.Error("error"));
        Assert.Equal("\u001b[33mfoo.wav\u001b[0m", styler.Token("foo.wav"));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Disabled_WhenNotAnInteractiveTerminal()
    {
        var styler = new AnsiStyler(interactiveTerminal: false, noColorEnvSet: false, noColorFlag: false);

        Assert.False(styler.Enabled);
        Assert.Equal("Usage:", styler.Heading("Usage:"));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Disabled_WhenNoColorEnvironmentVariableIsSet()
    {
        var styler = new AnsiStyler(interactiveTerminal: true, noColorEnvSet: true, noColorFlag: false);

        Assert.False(styler.Enabled);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Disabled_WhenNoColorFlagIsPassed()
    {
        var styler = new AnsiStyler(interactiveTerminal: true, noColorEnvSet: false, noColorFlag: true);

        Assert.False(styler.Enabled);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void FromEnvironment_IsDisabled_ForAnyNonConsoleWriter()
    {
        var writer = new StringWriter();

        Assert.False(AnsiStyler.FromEnvironment(writer, noColorFlag: false).Enabled);
        Assert.False(AnsiStyler.FromEnvironment(writer, noColorFlag: true).Enabled);
    }
}
