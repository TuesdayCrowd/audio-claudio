using AudioClaudio.Cli.Commands;
using Xunit;

namespace AudioClaudio.Tests.Cli;

/// <summary>
/// v2 Stage 3b review regression: the <c>--key</c> override must validate to a real key signature so a
/// nonsensical value fails cleanly instead of crashing the speller or emitting a garbage &lt;fifths&gt;.
/// </summary>
public class KeyOptionTests
{
    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("0", 0)]
    [InlineData("-4", -4)]
    [InlineData("7", 7)]
    [InlineData("-7", -7)]
    [InlineData("+2", 2)]
    public void ValidFifths_Parse(string raw, int expected)
    {
        Assert.True(KeyOption.TryParse(raw, out int fifths, out string? error));
        Assert.Equal(expected, fifths);
        Assert.Null(error);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("8")]
    [InlineData("-8")]
    [InlineData("40")]           // the fat-finger case (extra digit on -4)
    [InlineData("2147483647")]   // int.MaxValue — used to crash PitchSpeller via Math.Abs(int.MinValue)
    [InlineData("-2147483648")]
    [InlineData("C")]
    [InlineData("")]
    [InlineData("1.5")]
    public void InvalidFifths_AreRejectedCleanly(string raw)
    {
        Assert.False(KeyOption.TryParse(raw, out int fifths, out string? error));
        Assert.Equal(0, fifths);
        Assert.NotNull(error);
        Assert.Contains("--key", error);
    }
}
