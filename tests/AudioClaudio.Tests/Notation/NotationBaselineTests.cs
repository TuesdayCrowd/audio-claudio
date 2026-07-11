using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;
using Xunit;
using Xunit.Abstractions;

namespace AudioClaudio.Tests.Notation;

/// <summary>
/// v2 Stage 3a — the notation-quality harness and its recorded <b>baselines</b>. These pin today's
/// numbers (with the fixed middle-C split, no key detection, and the straight sixteenth grid) so the
/// later levers ratchet them up (the "baseline → target" mandate of the workplan). Fast: pure
/// quantization, no synthesis.
/// </summary>
public class NotationBaselineTests
{
    private const int CorpusSize = 40;
    private static IReadOnlyList<NotationCase> Corpus => NotationCorpusGen.Cases(CorpusSize).ToList();

    private readonly ITestOutputHelper _out;

    public NotationBaselineTests(ITestOutputHelper output) => _out = output;

    [Fact]
    [Trait("Category", "Fast")]
    public void Generator_IsDeterministic()
    {
        var a = NotationCorpusGen.Cases(CorpusSize).ToList();
        var b = NotationCorpusGen.Cases(CorpusSize).ToList();

        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Fifths, b[i].Fifths);
            Assert.Equal(a[i].Bpm, b[i].Bpm);
            Assert.Equal(a[i].Events.Count, b[i].Events.Count);
            Assert.Equal(
                a[i].Events.Select(e => (e.Pitch.MidiNumber, e.Onset.Samples, e.Duration.Samples, e.Velocity)),
                b[i].Events.Select(e => (e.Pitch.MidiNumber, e.Onset.Samples, e.Duration.Samples, e.Velocity)));
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Corpus_ContainsTripletsAndCrossings_SoTheLeversHaveSignal()
    {
        var corpus = Corpus;

        // At least some triplet-eighth notes (value 4 fine) exist — the note-value target's reason to exist.
        int triplets = corpus.Sum(c => c.Truth.Count(t => t.ValueFine == 4));
        // At least some register crossings (a note tagged with a hand but sitting in the other's register).
        int crossings = corpus.Sum(c => c.Truth.Count(t =>
            (t.Hand == Hand.Left && t.Pitch.MidiNumber >= StaffSplitterMidC) ||
            (t.Hand == Hand.Right && t.Pitch.MidiNumber < StaffSplitterMidC)));
        // The corpus spans several keys, not just C major.
        int distinctKeys = corpus.Select(c => c.Fifths).Distinct().Count();

        _out.WriteLine($"triplet notes: {triplets}, crossings: {crossings}, distinct keys: {distinctKeys}");
        Assert.True(triplets >= 20, $"too few triplets ({triplets})");
        Assert.True(crossings >= 20, $"too few crossings ({crossings})");
        Assert.True(distinctKeys >= 5, $"too few distinct keys ({distinctKeys})");
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Baseline_NoteValueAccuracy_StraightGrid_MissesTriplets()
    {
        double acc = NotationMetrics.NoteValueAccuracy(Corpus, Subdivision.Sixteenth);
        _out.WriteLine($"note-value baseline (sixteenth grid): {acc:P1}");

        // Straight values recover exactly; triplet-eighths cannot exist on a sixteenth grid, so they
        // pull the number below 1. This is the baseline the triplet-capable grid (Stage 3d) must beat.
        Assert.InRange(acc, 0.60, 0.95);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Baseline_KeyAccuracy_NoDetector_IsChanceLevel()
    {
        // No detector yet: default key 0 (C major / no accidentals). Only the C-major cases "match".
        double acc = NotationMetrics.KeyAccuracy(Corpus, _ => 0);
        _out.WriteLine($"key baseline (no detector, always 0): {acc:P1}");
        Assert.InRange(acc, 0.0, 0.30);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Baseline_HandAccuracy_MiddleCSplit_MissesCrossings()
    {
        double acc = NotationMetrics.HandAccuracy(Corpus, NotationMetrics.MiddleCSplit);
        _out.WriteLine($"hand baseline (fixed middle-C split): {acc:P1}");

        // The fixed cut is right for every non-crossing note and wrong for every crossing one, so it is
        // high but < 1 — the number the temporal tracker (Stage 3c) must beat.
        Assert.InRange(acc, 0.80, 0.99);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Dynamics_Generalize_EachBandCentreMapsToItsMark()
    {
        // The six representative velocities the generator uses land on the six distinct marks (pp..ff).
        Assert.Equal("pp", DynamicMarks.From(24));
        Assert.Equal("p", DynamicMarks.From(40));
        Assert.Equal("mp", DynamicMarks.From(56));
        Assert.Equal("mf", DynamicMarks.From(72));
        Assert.Equal("f", DynamicMarks.From(88));
        Assert.Equal("ff", DynamicMarks.From(112));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void AutoTempo_OnTheCorpus_IsPositiveAndDeterministic()
    {
        foreach (NotationCase c in Corpus)
        {
            Tempo a = TempoEstimator.Estimate(c.Events, new Tempo(c.Bpm));
            Tempo b = TempoEstimator.Estimate(c.Events, new Tempo(c.Bpm));
            Assert.Equal(a.BeatsPerMinute, b.BeatsPerMinute);
            Assert.True(a.BeatsPerMinute > 0);
        }
    }

    private const int StaffSplitterMidC = 60;
}
