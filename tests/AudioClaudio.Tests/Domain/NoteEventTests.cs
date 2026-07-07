using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class NoteEventTests
{
    private static readonly SampleRate R44 = new(44100);

    [Fact]
    [Trait("Category", "Fast")]
    public void Constructor_CarriesAllFields()
    {
        var pitch = new Pitch(60); // middle C
        var onset = new SamplePosition(1000, R44);
        var duration = new SampleDuration(22050, R44);
        var note = new NoteEvent(pitch, onset, duration, velocity: 100);

        Assert.Equal(pitch, note.Pitch);
        Assert.Equal(onset, note.Onset);
        Assert.Equal(duration, note.Duration);
        Assert.Equal(100, note.Velocity);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(-1)]
    [InlineData(128)]
    public void Constructor_RejectsVelocityOutOfRange(int velocity)
    {
        var pitch = new Pitch(60);
        var onset = new SamplePosition(0, R44);
        var duration = new SampleDuration(100, R44);
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => new NoteEvent(pitch, onset, duration, velocity));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Constructor_RejectsOnsetDurationRateMismatch()
    {
        var pitch = new Pitch(60);
        var onset = new SamplePosition(0, new SampleRate(44100));
        var duration = new SampleDuration(100, new SampleRate(48000));
        Assert.Throws<System.InvalidOperationException>(
            () => new NoteEvent(pitch, onset, duration, 64));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void DefaultVelocity_IsConstant()
    {
        var note = new NoteEvent(new Pitch(60), new SamplePosition(0, R44), new SampleDuration(100, R44));
        Assert.Equal(NoteEvent.DefaultVelocity, note.Velocity);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void NoteEvent_ComparesByValue()
    {
        var a = new NoteEvent(new Pitch(60), new SamplePosition(0, R44), new SampleDuration(100, R44), 64);
        var b = new NoteEvent(new Pitch(60), new SamplePosition(0, R44), new SampleDuration(100, R44), 64);
        Assert.Equal(a, b);
    }
}
