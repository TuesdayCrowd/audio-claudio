using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class CliCommandBuilderTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void NameAndSummary_AreExposedAsDeclared()
    {
        var command = new CliCommand("render", "Render a MIDI file to a WAV file.");

        Assert.Equal("render", command.Name);
        Assert.Equal("Render a MIDI file to a WAV file.", command.Summary);
        Assert.Empty(command.Arguments);
        Assert.Empty(command.Options);
        Assert.Null(command.Example);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void WithArgument_AppendsInDeclarationOrder()
    {
        var inMid = new CliArgument("in.mid", "The MIDI file to render.");
        var outWav = new CliArgument("out.wav", "Where to write the rendered audio.");

        var command = new CliCommand("render", "x")
            .WithArgument(inMid)
            .WithArgument(outWav);

        Assert.Equal(new[] { inMid, outWav }, command.Arguments);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void WithOption_AppendsInDeclarationOrder()
    {
        var tempo = new CliOption("--tempo", OptionKind.Double, "x");
        var key = new CliOption("--key", OptionKind.Int, "y");

        var command = new CliCommand("transcribe", "x")
            .WithOption(tempo)
            .WithOption(key);

        Assert.Equal(new[] { tempo, key }, command.Options);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void WithExample_SetsTheWorkedExample()
    {
        var command = new CliCommand("render", "x").WithExample("claudio render score.mid out.wav");

        Assert.Equal("claudio render score.mid out.wav", command.Example);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Fluent_ReturnsTheSameInstance_ForChaining()
    {
        var command = new CliCommand("render", "x");

        Assert.Same(command, command.WithArgument(new CliArgument("a", "d")));
        Assert.Same(command, command.WithOption(new CliOption("--o", OptionKind.String, "d")));
        Assert.Same(command, command.WithExample("e"));
    }
}
