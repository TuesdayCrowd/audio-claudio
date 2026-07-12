using AudioClaudio.Cli.Composition;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Cli;

/// <summary>
/// The only device-free piece of the `listen --view --poly` prototype
/// (<see cref="AudioClaudio.Cli.Commands.LivePolyphonicView"/>) that can be unit-tested without a real
/// microphone: the thread-safe accumulator the mic-draining thread appends to while a background
/// transcribe loop snapshots it. The prototype's actual mic loop is device-dependent -> manual
/// acceptance only (same precedent as <c>PortAudioAudioSource.Start</c>).
/// </summary>
public class FrameAccumulatorTests
{
    private static readonly SampleRate Rate = new(44100);

    private static Frame MakeFrame(long start) =>
        new(new float[] { 0.1f, 0.2f }, new SamplePosition(start, Rate));

    [Fact]
    [Trait("Category", "Fast")]
    public void SnapshotReturnsEveryFrameAddedSoFarInOrder()
    {
        var accumulator = new FrameAccumulator();
        accumulator.Add(MakeFrame(0));
        accumulator.Add(MakeFrame(256));
        accumulator.Add(MakeFrame(512));

        IReadOnlyList<Frame> snapshot = accumulator.Snapshot();

        Assert.Equal(3, snapshot.Count);
        Assert.Equal(0, snapshot[0].Start.Samples);
        Assert.Equal(256, snapshot[1].Start.Samples);
        Assert.Equal(512, snapshot[2].Start.Samples);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void SnapshotIsAPointInTimeCopyUnaffectedByLaterAdds()
    {
        var accumulator = new FrameAccumulator();
        accumulator.Add(MakeFrame(0));

        IReadOnlyList<Frame> snapshot = accumulator.Snapshot();
        accumulator.Add(MakeFrame(256)); // added AFTER the snapshot was taken

        Assert.Single(snapshot);
        Assert.Equal(2, accumulator.Count);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void EmptyAccumulatorSnapshotsToAnEmptyList()
    {
        var accumulator = new FrameAccumulator();

        Assert.Empty(accumulator.Snapshot());
        Assert.Equal(0, accumulator.Count);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task ConcurrentAddsFromMultipleThreadsAreAllCaptured()
    {
        var accumulator = new FrameAccumulator();
        const int perTask = 200;
        const int taskCount = 4;
        var tasks = new Task[taskCount];
        for (int t = 0; t < taskCount; t++)
        {
            long taskBase = t * 100_000L;
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < perTask; i++)
                {
                    accumulator.Add(MakeFrame(taskBase + i));
                }
            });
        }

        await Task.WhenAll(tasks);

        Assert.Equal(taskCount * perTask, accumulator.Count);
        Assert.Equal(taskCount * perTask, accumulator.Snapshot().Count);
    }
}
