using System;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Evaluation;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Tests.Signals;
using Xunit;

namespace AudioClaudio.Tests.Domain;

/// <summary>
/// Chroma similarity: mean per-frame cosine of two chromagrams, with a bounded frame-offset search so a
/// constant latency between the two recordings doesn't sink the score. This is the objective side of
/// "does the re-synthesis sound like the original" — 1.0 for identical pitch content over time, ~0 for
/// unrelated pitches, and robust to a small time shift.
/// </summary>
public class ChromaSimilarityTests
{
    private static readonly SampleRate Rate = new(44100);

    [Fact]
    [Trait("Category", "Fast")]
    public void A_signal_is_maximally_similar_to_itself()
    {
        var x = Chromagram.FromSamples(SignalGenerator.Sine(440.0, 20000, Rate), Rate);
        Assert.True(ChromaSimilarity.Compare(x, x) > 0.99);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Different_pitch_classes_are_dissimilar()
    {
        var a = Chromagram.FromSamples(SignalGenerator.Sine(440.0, 20000, Rate), Rate);    // A
        var c = Chromagram.FromSamples(SignalGenerator.Sine(261.63, 20000, Rate), Rate);   // C
        Assert.True(ChromaSimilarity.Compare(a, c) < 0.2);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void A_constant_time_offset_is_recovered_by_the_search()
    {
        float[] sig = SignalGenerator.Sine(440.0, 20000, Rate);
        var delayed = new float[sig.Length + (2 * 2048)]; // shift by two hops
        Array.Copy(sig, 0, delayed, 2 * 2048, sig.Length);

        var a = Chromagram.FromSamples(sig, Rate);
        var b = Chromagram.FromSamples(delayed, Rate);
        Assert.True(ChromaSimilarity.Compare(a, b) > 0.95); // the offset search realigns them
    }
}
