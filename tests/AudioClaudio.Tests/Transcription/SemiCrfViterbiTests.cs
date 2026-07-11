using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AudioClaudio.Domain;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Transcription;

/// <summary>
/// v2 Stage 4c — the C# semi-CRF Viterbi decode reproduces PyTorch's <c>viterbiBackward</c> exactly, on a
/// hand-built multi-track synthetic <c>S</c>, the same <c>S</c> with a <c>forcedStartPos</c> (the segment-
/// stitching path), and a real model <c>S</c> from the two-bar render (the committed <c>ref3c</c> fixtures).
/// </summary>
public class SemiCrfViterbiTests
{
    private const int NBatch = 90;
    private static string ModelDir => RepoPaths.Fixture("models", "transkun");

    private static float[] ReadF32(string name)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(ModelDir, name));
        var arr = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, arr, 0, arr.Length * sizeof(float));
        return arr;
    }

    // Parse {"5": [[1,3],[5,5]], ...} → track → intervals.
    private static Dictionary<int, List<SemiCrfViterbi.Interval>> ParseIntervals(JsonElement obj)
    {
        var map = new Dictionary<int, List<SemiCrfViterbi.Interval>>();
        foreach (JsonProperty track in obj.EnumerateObject())
        {
            var list = new List<SemiCrfViterbi.Interval>();
            foreach (JsonElement pair in track.Value.EnumerateArray())
            {
                list.Add(new SemiCrfViterbi.Interval(pair[0].GetInt32(), pair[1].GetInt32()));
            }

            map[int.Parse(track.Name)] = list;
        }

        return map;
    }

    private static int TFromLength(int length) => (int)Math.Round(Math.Sqrt(length / (double)NBatch));

    private static void AssertMatches(
        IReadOnlyList<IReadOnlyList<SemiCrfViterbi.Interval>> actual, Dictionary<int, List<SemiCrfViterbi.Interval>> expected)
    {
        for (int k = 0; k < NBatch; k++)
        {
            var want = expected.TryGetValue(k, out List<SemiCrfViterbi.Interval>? v) ? v : new List<SemiCrfViterbi.Interval>();
            Assert.True(want.SequenceEqual(actual[k]),
                $"track {k}: expected [{string.Join(",", want)}], got [{string.Join(",", actual[k])}]");
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void SyntheticMultiTrack_DecodesExactly()
    {
        float[] s = ReadF32("ref3c_syn_S.f32");
        int t = TFromLength(s.Length);
        var actual = SemiCrfViterbi.Decode(s, t, NBatch);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ModelDir, "ref3c_syn_intervals.json")));
        AssertMatches(actual, ParseIntervals(doc.RootElement));

        // The designed cases (sanity that the fixture is what we think): t5=(1,3)+(5,5), t10=(0,4), t20=(2,2).
        Assert.Equal(new[] { new SemiCrfViterbi.Interval(1, 3), new SemiCrfViterbi.Interval(5, 5) }, actual[5]);
        Assert.Equal(new[] { new SemiCrfViterbi.Interval(0, 4) }, actual[10]);
        Assert.Equal(new[] { new SemiCrfViterbi.Interval(2, 2) }, actual[20]);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void ForcedStartPos_SkipsTheEarlierInterval()
    {
        float[] s = ReadF32("ref3c_syn_S.f32");
        int t = TFromLength(s.Length);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ModelDir, "ref3c_forced_intervals.json")));
        int[] forced = doc.RootElement.GetProperty("forcedStartPos").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var actual = SemiCrfViterbi.Decode(s, t, NBatch, forced);

        AssertMatches(actual, ParseIntervals(doc.RootElement.GetProperty("intervals")));
        Assert.Equal(new[] { new SemiCrfViterbi.Interval(5, 5) }, actual[5]); // (1,3) skipped by forcing start=4
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void RealModelS_DecodesExactly()
    {
        float[] s = ReadF32("ref3c_S.f32");
        int t = TFromLength(s.Length);
        var actual = SemiCrfViterbi.Decode(s, t, NBatch);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ModelDir, "ref3c_intervals.json")));
        AssertMatches(actual, ParseIntervals(doc.RootElement));

        // The real S decoded to exactly one note (E4 = track 45, frames 43..63) — a non-trivial round-trip.
        Assert.Equal(new[] { new SemiCrfViterbi.Interval(43, 63) }, actual[45]);
    }
}
