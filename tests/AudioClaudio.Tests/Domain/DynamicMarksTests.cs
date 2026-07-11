using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

/// <summary>
/// Velocity → dynamic mark for notation. A transcription carries per-note velocity (Basic Pitch
/// amplitude, or a piano-specific model's real velocity); mapping it to <c>pp…ff</c> lets the engraved
/// score show dynamics. MuseScore-aligned boundaries so the marks land where a musician expects.
/// </summary>
public class DynamicMarksTests
{
    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(1, "pp")]
    [InlineData(20, "pp")]
    [InlineData(33, "p")]   // boundary: 33 is the first p
    [InlineData(45, "p")]
    [InlineData(60, "mp")]
    [InlineData(75, "mf")]
    [InlineData(90, "f")]
    [InlineData(96, "ff")]  // boundary: 96 is the first ff
    [InlineData(110, "ff")]
    [InlineData(127, "ff")]
    public void Maps_velocity_to_the_dynamic_mark(int velocity, string mark) =>
        Assert.Equal(mark, DynamicMarks.From(velocity));
}
