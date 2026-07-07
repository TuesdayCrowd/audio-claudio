using System.Collections.Generic;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public sealed class OnsetDetectorTests
{
    private static readonly SampleRate Rate = new(44100);
    private const long Hop = 512;

    // 8 frames: silence, silence, note (3 sustain frames), silence, note (2 frames).
    private static List<IReadOnlyList<double>> TwoNoteSpectra() => new()
    {
        new double[] { 0, 0, 0, 0 },   // 0 silence
        new double[] { 0, 0, 0, 0 },   // 1 silence
        new double[] { 1, 1, 1, 1 },   // 2 note A attack
        new double[] { 1, 1, 1, 1 },   // 3 sustain
        new double[] { 1, 1, 1, 1 },   // 4 sustain
        new double[] { 0, 0, 0, 0 },   // 5 silence
        new double[] { 1, 1, 1, 1 },   // 6 note B attack
        new double[] { 1, 1, 1, 1 },   // 7 sustain
    };

    [Fact]
    [Trait("Category", "Fast")]
    public void DetectFindsOneOnsetPerNoteStart()
    {
        var onsets = new OnsetDetector().Detect(TwoNoteSpectra());

        Assert.Equal(new[] { 2, 6 }, onsets);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void OnsetsAreReportedAtTheirFrameStartPosition()
    {
        var spectra = TwoNoteSpectra();
        var starts = new List<SamplePosition>();
        for (int i = 0; i < spectra.Count; i++)
        {
            starts.Add(new SamplePosition(i * Hop, Rate));
        }

        var positions = new OnsetDetector().DetectOnsetPositions(spectra, starts);

        Assert.Equal(2, positions.Count);
        Assert.Equal(2 * Hop, positions[0].Samples);
        Assert.Equal(6 * Hop, positions[1].Samples);
    }
}
