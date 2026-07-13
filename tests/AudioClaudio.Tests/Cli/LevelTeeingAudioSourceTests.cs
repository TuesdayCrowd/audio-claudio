using AudioClaudio.Application.Ports;
using AudioClaudio.Cli.Composition;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Cli;

public class LevelTeeingAudioSourceTests
{
    private static readonly SampleRate Rate = new SampleRate(44100);

    private sealed class FakeSource : IAudioSource
    {
        private readonly Frame[] _frames;
        public FakeSource(params Frame[] frames) => _frames = frames;
        public IEnumerable<Frame> Frames => _frames;
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void PassesEveryFrameThroughUnchanged()
    {
        var frame = new Frame(new float[] { 1f, -1f, 1f, -1f }, new SamplePosition(0, Rate));
        var inner = new FakeSource(frame);
        var levels = new List<double>();
        var teed = new LevelTeeingAudioSource(inner, levels.Add);

        var outFrames = teed.Frames.ToList();

        Assert.Single(outFrames);
        Assert.Same(frame, outFrames[0]);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void ReportsTheRmsOfEachFrameAsItIsYielded()
    {
        var loud = new Frame(new float[] { 1f, -1f, 1f, -1f }, new SamplePosition(0, Rate));  // RMS = 1.0
        var quiet = new Frame(new float[] { 0f, 0f, 0f, 0f }, new SamplePosition(4, Rate));    // RMS = 0.0
        var inner = new FakeSource(loud, quiet);
        var levels = new List<double>();
        var teed = new LevelTeeingAudioSource(inner, levels.Add);

        teed.Frames.ToList();

        Assert.Equal(new[] { 1.0, 0.0 }, levels);
    }
}
