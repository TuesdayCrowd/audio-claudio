using System;
using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.Notation;

/// <summary>Which hand played a note — the ground truth the treble/bass split is scored against.</summary>
public enum Hand
{
    Left,
    Right,
}

/// <summary>One generated note with its full ground truth: pitch, onset (both in fine ticks and samples),
/// intended note value (fine ticks), hand, and velocity. "Fine ticks" are twelfths of a quarter — the
/// straight+triplet common grid, so every generated value is an exact integer (quarter = 12, eighth = 6,
/// sixteenth = 3, eighth-triplet = 4, sixteenth-triplet = 2, dotted quarter = 18, …).</summary>
public sealed record NotationNote(
    Pitch Pitch, int OnsetFine, long OnsetSamples, int ValueFine, Hand Hand, int Velocity);

/// <summary>
/// A generated notation case: a two-hand, tonal, straight-and-triplet performance whose <b>rhythm, key,
/// hand assignment and dynamics are all known</b>. The <see cref="Events"/> are fed straight into the
/// notation layer (quantizer / staff-split / key-detect), and each recovered note is scored against the
/// parallel <see cref="Truth"/> — isolating notation quality from the transcription engine's noise
/// (v2 Stage 3). Determinism: a plain seeded <see cref="Random"/>, integer sample onsets on the grid
/// (non-negotiable 1).
/// </summary>
public sealed record NotationCase(
    int Bpm, int Fifths, IReadOnlyList<NoteEvent> Events, IReadOnlyList<NotationNote> Truth);

/// <summary>Generates <see cref="NotationCase"/>s: the Stage-3 notation-quality corpus.</summary>
public static class NotationCorpusGen
{
    public const int SampleRateHz = 44100;
    public const int DefaultSeed = 5137;
    public const int FinePerQuarter = 12;                 // twelfths of a quarter — the common grid
    public const int FinePerMeasure = FinePerQuarter * 4; // 4/4

    // Left hand sits low, right hand high, split around middle C (60). The registers overlap slightly
    // at the seam so a fixed middle-C cut and a temporal tracker can disagree.
    public const int LeftLo = 40, LeftHi = 59;   // E2..B3
    public const int RightLo = 60, RightHi = 79; // C4..G5

    // Note values in fine ticks. Straight values dominate; triplet-eighths (4) appear as beat-filling
    // groups of three. This mix sets the note-value baseline (div-4 can't represent the 4) and target.
    private static readonly int[] StraightValues = { 3, 6, 9, 12, 18, 24 }; // 16th,8th,dotted-8th,qtr,dotted-qtr,half
    private const int TripletEighthFine = 4;                                // three fill one quarter (12)

    private static readonly int[] MajorScale = { 0, 2, 4, 5, 7, 9, 11 };

    /// <summary>A deterministic, seeded corpus of <paramref name="count"/> cases.</summary>
    public static IEnumerable<NotationCase> Cases(int count, int seed = DefaultSeed)
    {
        var rng = new Random(seed);
        for (int c = 0; c < count; c++)
        {
            yield return BuildRandom(rng);
        }
    }

    private static NotationCase BuildRandom(Random rng)
    {
        var rate = new SampleRate(SampleRateHz);
        int bpm = 80 + rng.Next(0, 41);        // 80–120
        int fifths = rng.Next(-4, 5);          // A♭..E major
        int measures = 2 + rng.Next(0, 2);     // 2–3 bars
        int totalFine = measures * FinePerMeasure;
        HashSet<int> scale = DiatonicPitchClasses(fifths);

        double samplesPerFine = 60.0 / bpm * rate.Hz / FinePerQuarter;
        long ToSamples(int fine) => (long)Math.Round(fine * samplesPerFine, MidpointRounding.AwayFromZero);

        int tonic = (((fifths * 7) % 12) + 12) % 12;
        var truth = new List<NotationNote>();
        WalkHand(rng, scale, tonic, Hand.Left, LeftLo, LeftHi, totalFine, ToSamples, rate, truth);
        WalkHand(rng, scale, tonic, Hand.Right, RightLo, RightHi, totalFine, ToSamples, rate, truth);

        // Each hand opens on the tonic (as tonal music establishes its key) — an rng-neutral post-hoc
        // override, so the rhythm/hand/crossing structure and every other baseline are unchanged.
        FrameOnTonic(truth, Hand.Left, tonic, LeftLo, LeftHi);
        FrameOnTonic(truth, Hand.Right, tonic, RightLo, RightHi);

        // Emit in chronological onset order (both hands interleaved) so a stateful/temporal consumer sees
        // the real timeline, not one hand then the other. The Events[i] ↔ Truth[i] pairing is preserved by
        // sorting the truth first, then projecting. (v2 Stage 3b review, finding 2.)
        truth.Sort((a, b) => a.OnsetSamples != b.OnsetSamples
            ? a.OnsetSamples.CompareTo(b.OnsetSamples)
            : a.Pitch.MidiNumber.CompareTo(b.Pitch.MidiNumber));

        var events = truth
            .Select(t => new NoteEvent(
                t.Pitch, new SamplePosition(t.OnsetSamples, rate),
                new SampleDuration(Math.Max(1, ToSamples(t.OnsetFine + t.ValueFine) - t.OnsetSamples), rate),
                t.Velocity))
            .ToList();

        return new NotationCase(bpm, fifths, events, truth);
    }

    // Lay one hand's monophonic line across the bar span: a note (value fits before the next onset, so
    // note-value scoring is not confounded by next-onset clipping), an optional rest, occasionally a
    // beat of eighth-triplets, occasionally a register crossing (still tagged with this hand).
    private static void WalkHand(
        Random rng, HashSet<int> scale, int tonic, Hand hand, int lo, int hi, int totalFine,
        Func<int, long> toSamples, SampleRate rate, List<NotationNote> truth)
    {
        int velocity = RepresentativeVelocity(rng); // one dynamic band per hand-phrase
        int cursor = 0;
        while (cursor < totalFine)
        {
            int beatRemainder = FinePerQuarter - (cursor % FinePerQuarter);
            bool onBeat = cursor % FinePerQuarter == 0;

            // A triplet group only when a whole beat is free (three eighth-triplets fill exactly 12).
            if (onBeat && cursor + FinePerQuarter <= totalFine && rng.Next(0, 4) == 0)
            {
                for (int k = 0; k < 3; k++)
                {
                    int onset = cursor + k * TripletEighthFine;
                    truth.Add(NoteAt(rng, scale, tonic, hand, lo, hi, onset, TripletEighthFine, velocity, toSamples, rate));
                }

                cursor += FinePerQuarter;
                continue;
            }

            int value = StraightValues[rng.Next(StraightValues.Length)];
            if (value > totalFine - cursor)
            {
                value = 3; // a sixteenth always fits; keeps the tail inside the span
            }

            truth.Add(NoteAt(rng, scale, tonic, hand, lo, hi, cursor, value, velocity, toSamples, rate));
            int rest = rng.Next(0, 3) == 0 ? 3 : 0; // sometimes a sixteenth rest between notes
            cursor += value + rest;
        }
    }

    // Replace a hand's first note with a tonic-class pitch inside the hand's own register band. Keeps the
    // onset/value/velocity/hand; does not consume rng, so all other ground truth is unchanged. Clamped to
    // [lo,hi] so framing never manufactures a spurious crossing. (v2 Stage 3b review, finding 3.)
    private static void FrameOnTonic(List<NotationNote> truth, Hand hand, int tonic, int lo, int hi)
    {
        int idx = truth.FindIndex(t => t.Hand == hand);
        if (idx < 0)
        {
            return;
        }

        int centre = (lo + hi) / 2;
        int midi = centre - ((centre - tonic) % 12 + 12) % 12; // nearest tonic-class ≤ centre
        if (midi < lo)
        {
            midi += 12; // lift into the band
        }

        if (midi > hi)
        {
            midi -= 12;
        }

        truth[idx] = truth[idx] with { Pitch = new Pitch(midi) };
    }

    private static NotationNote NoteAt(
        Random rng, HashSet<int> scale, int tonic, Hand hand, int lo, int hi, int onsetFine, int valueFine,
        int velocity, Func<int, long> toSamples, SampleRate rate)
    {
        // 1-in-8 notes cross into the other hand's register but keep this hand's tag (the crossing case
        // the fixed middle-C split gets wrong and a temporal tracker can get right).
        bool cross = rng.Next(0, 8) == 0;
        int midi = cross
            ? (hand == Hand.Left ? PickDiatonic(rng, scale, tonic, RightLo, RightLo + 5) : PickDiatonic(rng, scale, tonic, LeftHi - 5, LeftHi))
            : PickDiatonic(rng, scale, tonic, lo, hi);
        return new NotationNote(new Pitch(midi), onsetFine, toSamples(onsetFine), valueFine, hand, velocity);
    }

    // Pick a diatonic pitch in [lo,hi], weighting the tonic and dominant scale degrees so the corpus
    // has a real tonal centre (as tonal music does) rather than a flat diatonic wash — otherwise key
    // detection has no signal to find. The register/rhythm/hand structure is unchanged.
    private static int PickDiatonic(Random rng, HashSet<int> scale, int tonic, int lo, int hi)
    {
        var choices = new List<int>();
        for (int m = lo; m <= hi; m++)
        {
            int pc = ((m % 12) + 12) % 12;
            if (!scale.Contains(pc))
            {
                continue;
            }

            int degree = ((pc - tonic) % 12 + 12) % 12;
            int weight = degree switch
            {
                0 => 4,       // tonic (the strongest key cue)
                4 => 2,       // mediant — fixes the tonic-triad quality
                11 => 2,      // leading tone — resolves to the tonic, disambiguating from the dominant key
                7 => 1,       // dominant kept ordinary so it doesn't pull toward the dominant KEY
                _ => 1,
            };
            for (int w = 0; w < weight; w++)
            {
                choices.Add(m);
            }
        }

        return choices.Count > 0 ? choices[rng.Next(choices.Count)] : lo;
    }

    private static HashSet<int> DiatonicPitchClasses(int fifths)
    {
        int tonic = (((fifths * 7) % 12) + 12) % 12; // circle of fifths → tonic pitch class
        return MajorScale.Select(o => (tonic + o) % 12).ToHashSet();
    }

    // A representative velocity near the centre of a random dynamic band (pp..ff), so the emitted
    // dynamic mark is unambiguous for scoring.
    private static int RepresentativeVelocity(Random rng)
    {
        int[] bandCentres = { 24, 40, 56, 72, 88, 112 }; // pp, p, mp, mf, f, ff
        return bandCentres[rng.Next(bandCentres.Length)];
    }
}
