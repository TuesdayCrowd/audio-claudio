using System.Collections.Generic;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.ClosedLoop;

public sealed class ClosedLoopComparerTests
{
    private static NoteGridPosition N(int midi, int onset, int dur) =>
        new(new Pitch(midi), onset, dur);

    [Trait("Category", "Fast")]
    [Fact]
    public void Identical_grids_match()
    {
        var g = new List<NoteGridPosition> { N(60, 0, 4), N(62, 5, 2) };
        Assert.True(ClosedLoopComparer.Compare(g, g).IsMatch);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void Onset_off_by_one_subdivision_is_within_tolerance()
    {
        var exp = new List<NoteGridPosition> { N(60, 0, 4), N(62, 8, 4) };
        var act = new List<NoteGridPosition> { N(60, 0, 4), N(62, 9, 4) };
        Assert.True(ClosedLoopComparer.Compare(exp, act).IsMatch);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void Onset_off_by_two_subdivisions_fails()
    {
        var exp = new List<NoteGridPosition> { N(60, 0, 4) };
        var act = new List<NoteGridPosition> { N(60, 2, 4) };
        var r = ClosedLoopComparer.Compare(exp, act);
        Assert.False(r.IsMatch);
        Assert.Contains("onset", r.Detail);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void Duration_off_by_two_subdivisions_fails()
    {
        var exp = new List<NoteGridPosition> { N(60, 0, 4) };
        var act = new List<NoteGridPosition> { N(60, 0, 6) };
        Assert.False(ClosedLoopComparer.Compare(exp, act).IsMatch);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void Any_pitch_mismatch_fails_exactly()
    {
        var exp = new List<NoteGridPosition> { N(60, 0, 4) };
        var act = new List<NoteGridPosition> { N(61, 0, 4) };
        var r = ClosedLoopComparer.Compare(exp, act);
        Assert.False(r.IsMatch);
        Assert.Contains("pitch", r.Detail);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void Note_count_mismatch_fails()
    {
        var exp = new List<NoteGridPosition> { N(60, 0, 4), N(62, 5, 4) };
        var act = new List<NoteGridPosition> { N(60, 0, 4) };
        var r = ClosedLoopComparer.Compare(exp, act);
        Assert.False(r.IsMatch);
        Assert.Contains("count", r.Detail);
    }
}
