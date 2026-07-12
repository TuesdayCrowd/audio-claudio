using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class ParsedArgsTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Argument_ReturnsTheDeclaredPositional()
    {
        var args = new ParsedArgs(
            new Dictionary<string, string> { ["in.wav"] = "recording.wav" },
            new Dictionary<string, object>());

        Assert.Equal("recording.wav", args.Argument("in.wav"));
        Assert.Null(args.ArgumentOrNull("out-dir"));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Flag_IsTrueOnlyWhenPresent()
    {
        var present = new ParsedArgs(
            new Dictionary<string, string>(), new Dictionary<string, object> { ["mono"] = true });
        var absent = new ParsedArgs(new Dictionary<string, string>(), new Dictionary<string, object>());

        Assert.True(present.Flag("mono"));
        Assert.False(absent.Flag("mono"));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void TypedAccessors_ReturnTheStoredValue_OrNullWhenAbsent()
    {
        var args = new ParsedArgs(
            new Dictionary<string, string>(),
            new Dictionary<string, object>
            {
                ["tempo"] = 132.5,
                ["key"] = -1,
                ["soundfont"] = "/fixtures/soundfont/GeneralUser-GS.sf2",
                ["model"] = "transkun",
            });

        Assert.Equal(132.5, args.Double("tempo"));
        Assert.Equal(-1, args.Int("key"));
        Assert.Equal("/fixtures/soundfont/GeneralUser-GS.sf2", args.Path("soundfont"));
        Assert.Equal("transkun", args.Enum("model"));
        Assert.Null(args.Double("missing"));
        Assert.Null(args.Int("missing"));
        Assert.Null(args.String("missing"));
    }
}
