using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class SuggestTests
{
    private static readonly string[] Commands = { "transcribe", "listen", "play", "render", "evaluate" };

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("trascribe", "transcribe")]
    [InlineData("lsiten", "listen")]
    [InlineData("evaluete", "evaluate")]
    [InlineData("rendr", "render")]
    public void NearestMatch_PinsTheSuggestionForCommonTypos(string typo, string expected)
    {
        Assert.Equal(expected, Suggest.NearestMatch(typo, Commands));
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("frobnicate")]
    [InlineData("xyzzyplugh")]
    public void NearestMatch_ReturnsNull_WhenNothingIsClose(string input)
    {
        Assert.Null(Suggest.NearestMatch(input, Commands));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void NearestMatch_OnOptionNames_PinsTheDesignDocExample()
    {
        var options = new[] { "--tempo", "--key", "--model" };

        Assert.Equal("--model", Suggest.NearestMatch("--modl", options));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void NearestMatch_BreaksTiesOnEarliestCandidate()
    {
        // Both "play" and "stay" are edit-distance 1 from "slay"; "play" is declared first.
        var candidates = new[] { "play", "stay" };

        Assert.Equal("play", Suggest.NearestMatch("slay", candidates));
    }
}
