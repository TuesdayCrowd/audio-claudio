using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class LevenshteinTests
{
    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("", "", 0)]
    [InlineData("abc", "abc", 0)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("--modl", "--model", 1)]
    [InlineData("lsiten", "listen", 2)]
    public void Distance_MatchesTheClassicEditDistance(string a, string b, int expected)
    {
        Assert.Equal(expected, Levenshtein.Distance(a, b));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Distance_IsSymmetric()
    {
        Assert.Equal(
            Levenshtein.Distance("transcribe", "trascribe"),
            Levenshtein.Distance("trascribe", "transcribe"));
    }
}
