using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public sealed class OnsetPeakPickingTests
{
    private static readonly OnsetDetector Detector = new(new OnsetDetectorOptions
    {
        ThresholdWindowFrames = 4,
        ThresholdMultiplier = 1.0,
        ThresholdDelta = 0.1,
        LocalMaxRadiusFrames = 1,
        MinGapFrames = 3,
    });

    [Fact]
    [Trait("Category", "Fast")]
    public void SingleSpikeYieldsOneOnsetAtTheSpike()
    {
        var novelty = new double[] { 0, 0, 0, 1.0, 0, 0, 0, 0 };

        var onsets = Detector.PickPeaks(novelty);

        Assert.Equal(new[] { 3 }, onsets);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void TwoWellSeparatedSpikesYieldTwoOnsets()
    {
        var novelty = new double[] { 0, 0, 1.0, 0, 0, 0, 0, 0, 1.0, 0, 0 };

        var onsets = Detector.PickPeaks(novelty);

        Assert.Equal(new[] { 2, 8 }, onsets);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void AdjacentSpikesWithinMinGapCollapseToTheFirst()
    {
        var novelty = new double[] { 0, 0, 1.0, 0.9, 0, 0, 0, 0 };

        var onsets = Detector.PickPeaks(novelty);

        Assert.Equal(new[] { 2 }, onsets);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void FlatSilentNoveltyYieldsNoOnsets()
    {
        var novelty = new double[] { 0, 0, 0, 0, 0 };

        var onsets = Detector.PickPeaks(novelty);

        Assert.Empty(onsets);
    }
}
