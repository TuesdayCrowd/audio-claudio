using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class OptionAndArgumentTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void CliOption_Key_StripsLeadingDashes()
    {
        var option = new CliOption("--tempo", OptionKind.Double, "Declared tempo in BPM.");
        Assert.Equal("tempo", option.Key);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(OptionKind.Flag, "a flag")]
    [InlineData(OptionKind.String, "a value")]
    [InlineData(OptionKind.Int, "a whole number")]
    [InlineData(OptionKind.Double, "a number")]
    [InlineData(OptionKind.Path, "a file path")]
    public void CliOption_KindDescription_NamesTheExpectedShape(OptionKind kind, string expected)
    {
        var option = new CliOption("--x", kind, "irrelevant");
        Assert.Equal(expected, option.KindDescription);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void CliOption_EnumKindDescription_ListsTheChoices()
    {
        var option = new CliOption("--mode", OptionKind.Enum, "irrelevant", enumValues: new[] { "mono", "poly" });
        Assert.Equal("one of: mono, poly", option.KindDescription);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void CliOption_NameMustStartWithDoubleDash()
    {
        var ex = Assert.Throws<ArgumentException>(() => new CliOption("tempo", OptionKind.Double, "x"));
        Assert.Contains("--", ex.Message);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void CliOption_EnumKindWithoutValues_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CliOption("--mode", OptionKind.Enum, "x"));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void CliArgument_UsageToken_IsAngleBracketedWhenRequired()
    {
        var argument = new CliArgument("in.wav", "The input file.");
        Assert.Equal("<in.wav>", argument.UsageToken);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void CliArgument_UsageToken_IsSquareBracketedWhenOptional()
    {
        var argument = new CliArgument("out-dir", "Output directory.", required: false);
        Assert.Equal("[out-dir]", argument.UsageToken);
    }
}
