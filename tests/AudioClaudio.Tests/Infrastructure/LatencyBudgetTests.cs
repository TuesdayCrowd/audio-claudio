using AudioClaudio.Infrastructure.Capture;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class LatencyBudgetTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void DefaultParametersMeetSub150msBudget()
    {
        // The live `listen` defaults: 44.1 kHz, N = 1024, H = 256, peak-picker look-ahead = 3 hops.
        double ms = LatencyBudget.WorstCaseAlgorithmicMs(
            sampleRateHz: 44100, frameSize: 1024, hop: 256, onsetLookaheadFrames: 3);

        Assert.True(ms < 150.0, $"algorithmic latency {ms:F1} ms exceeds the 150 ms budget");
        Assert.Equal(40.63, ms, 2); // 1024 + 3*256 = 1792 samples => 40.63 ms
    }
}
