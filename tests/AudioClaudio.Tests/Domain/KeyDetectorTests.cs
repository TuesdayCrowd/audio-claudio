using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Tests.Notation;
using Xunit;
using Xunit.Abstractions;

namespace AudioClaudio.Tests.Domain;

/// <summary>
/// v2 Stage 3b — Krumhansl-Schmuckler key detection. Verified on tonic-emphasized diatonic content
/// across keys (the standard K-S validation), the relative-key invariant, and the Stage-3 corpus target.
/// </summary>
public class KeyDetectorTests
{
    private readonly ITestOutputHelper _out;

    public KeyDetectorTests(ITestOutputHelper output) => _out = output;

    private static readonly int[] MajorScale = { 0, 2, 4, 5, 7, 9, 11 };
    private static readonly int[] NaturalMinorScale = { 0, 2, 3, 5, 7, 8, 10 };

    // Diatonic content in a key, with the tonic and dominant emphasized as tonal music does (so the
    // correlation resolves the tonic, not just the scale collection). Pitch classes only matter.
    private static IReadOnlyList<Pitch> Content(int tonicPc, int[] scale)
    {
        var list = new List<Pitch>();
        foreach (int s in scale)
        {
            list.Add(new Pitch(60 + (tonicPc + s) % 12));
        }

        for (int i = 0; i < 3; i++)
        {
            list.Add(new Pitch(60 + tonicPc % 12)); // tonic
        }

        for (int i = 0; i < 2; i++)
        {
            list.Add(new Pitch(60 + (tonicPc + 7) % 12)); // dominant
        }

        return list;
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(0, 0)]    // C major → 0
    [InlineData(7, 1)]    // G major → +1
    [InlineData(2, 2)]    // D major → +2
    [InlineData(9, 3)]    // A major → +3
    [InlineData(4, 4)]    // E major → +4
    [InlineData(5, -1)]   // F major → −1
    [InlineData(10, -2)]  // B♭ major → −2
    [InlineData(3, -3)]   // E♭ major → −3
    [InlineData(8, -4)]   // A♭ major → −4
    public void MajorKeys_AreDetected(int tonicPc, int expectedFifths)
    {
        Assert.Equal(expectedFifths, KeyDetector.Detect(Content(tonicPc, MajorScale)));
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(9, 0)]    // A minor → relative C → 0
    [InlineData(4, 1)]    // E minor → relative G → +1
    [InlineData(2, -1)]   // D minor → relative F → −1
    public void MinorKeys_TakeTheirRelativeMajorSignature(int tonicPc, int expectedFifths)
    {
        Assert.Equal(expectedFifths, KeyDetector.Detect(Content(tonicPc, NaturalMinorScale)));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void EmptyInput_IsCMajor()
    {
        Assert.Equal(0, KeyDetector.Detect(System.Array.Empty<Pitch>()));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void IsDeterministic()
    {
        IReadOnlyList<Pitch> content = Content(2, MajorScale);
        Assert.Equal(KeyDetector.Detect(content), KeyDetector.Detect(content));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void InvalidProfileLength_Throws()
    {
        Assert.Throws<System.ArgumentException>(() => KeyDetector.DetectFromProfile(new double[11]));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Target_KeyAccuracy_OnTheCorpus_BeatsTheBaseline()
    {
        var corpus = NotationCorpusGen.Cases(40).ToList();
        double acc = NotationMetrics.KeyAccuracy(corpus, KeyDetector.Detect);
        _out.WriteLine($"key accuracy (KeyDetector): {acc:P1} (baseline was 10.0%)");

        // Target: the detector must recover the great majority of the seeded corpus's keys (baseline 10%).
        Assert.True(acc >= 0.85, $"key accuracy {acc:P1} below the 85% target");
    }
}
