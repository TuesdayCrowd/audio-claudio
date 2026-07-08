using AudioClaudio.Infrastructure.MusicXml;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.MusicXml;

public class MusicXmlGoldenTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void EmitsByteIdenticalGoldenForTwinkleFixture()
    {
        var score = MusicXmlFixtures.Twinkle();

        using var ms = new MemoryStream();
        new MusicXmlScoreWriter().Write(score, ms);
        var actual = ms.ToArray();

        var expected = File.ReadAllBytes(RepoPaths.Fixture("golden", "musicxml", "twinkle.musicxml"));
        Assert.Equal(expected, actual);
    }
}
