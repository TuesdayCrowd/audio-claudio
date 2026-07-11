using System.Collections.Generic;
using System.Linq;

namespace AudioClaudio.Domain.Polyphony;

/// <summary>
/// Splits chords across the grand staff by <b>temporal hand-tracking</b> rather than a fixed register
/// cut. Two running hand centres (an EMA of each hand's recent pitches) follow the two lines over time;
/// each chord is split at the contiguous boundary that best matches its low pitches to the left centre
/// and its high pitches to the right. Because the centres <i>move with the music</i>, a hand's line that
/// crosses middle C keeps its notes (the boundary rides up/down with it) — the case a fixed middle-C cut
/// (<see cref="StaffSplitter"/>) gets wrong. Deterministic (the split scan and tie-break are fixed);
/// pure per instance (state is the two centres, seeded from a middle-C prior). Stateless callers use the
/// static <see cref="Split"/> over a whole chord sequence.
///
/// It cannot recover an <i>isolated</i> leap into the other register (a note with no continuity to its
/// hand's line carries no signal of which hand played it) — only continuous crossings, which is what real
/// two-hand playing produces.
/// </summary>
public sealed class HandSplitter
{
    /// <summary>How fast a centre tracks its hand's line (0 = frozen, 1 = jumps to the latest note).
    /// 0.6 follows a stepwise line closely while damping a single outlier.</summary>
    public const double DefaultAlpha = 0.6;

    /// <summary>The middle-C-prior seeds: a perfect fifth around middle C (E3, G4).</summary>
    public const double DefaultLeftCentre = 52.0;
    public const double DefaultRightCentre = 67.0;

    private readonly double _alpha;
    private double _left;
    private double _right;

    public HandSplitter(
        double leftCentre = DefaultLeftCentre, double rightCentre = DefaultRightCentre, double alpha = DefaultAlpha)
    {
        _left = leftCentre;
        _right = rightCentre;
        _alpha = alpha;
    }

    /// <summary>Split one chord, advancing the hand centres. The treble fragment holds the pitches
    /// assigned to the right hand, the bass those to the left; either is null when empty.</summary>
    public (Chord? Treble, Chord? Bass) SplitNext(Chord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);

        IReadOnlyList<Pitch> pitches = chord.Pitches; // ascending MIDI (Chord invariant)
        int n = pitches.Count;

        // Choose the contiguous split boundary s (bass = [0,s), treble = [s,n)) minimising total distance
        // to the two centres. Scanning all s makes it a global choice for the chord, not a greedy per-note
        // one, so a wide chord divides sensibly between the hands. Ties (common right at a crossing, before
        // a centre has caught up) break toward the most BALANCED split — one note per hand for the usual
        // two-note texture — which is what keeps a crossing note on its own hand instead of doubling up.
        const double Epsilon = 1e-9;
        int bestSplit = 0;
        double bestCost = double.MaxValue;
        double bestBalance = double.MaxValue;
        for (int s = 0; s <= n; s++)
        {
            double cost = 0.0;
            for (int i = 0; i < s; i++)
            {
                cost += System.Math.Abs(pitches[i].MidiNumber - _left);
            }

            for (int i = s; i < n; i++)
            {
                cost += System.Math.Abs(pitches[i].MidiNumber - _right);
            }

            double balance = System.Math.Abs(s - n / 2.0);
            if (cost < bestCost - Epsilon
                || (cost < bestCost + Epsilon && balance < bestBalance))
            {
                bestCost = cost;
                bestBalance = balance;
                bestSplit = s;
            }
        }

        var bass = pitches.Take(bestSplit).ToList();
        var treble = pitches.Skip(bestSplit).ToList();

        // Advance the centres toward each hand's assigned pitches (only when that hand actually played).
        if (bass.Count > 0)
        {
            _left = (1.0 - _alpha) * _left + _alpha * bass.Average(p => p.MidiNumber);
        }

        if (treble.Count > 0)
        {
            _right = (1.0 - _alpha) * _right + _alpha * treble.Average(p => p.MidiNumber);
        }

        return (
            treble.Count > 0 ? chord with { Pitches = treble } : null,
            bass.Count > 0 ? chord with { Pitches = bass } : null);
    }

    /// <summary>Split a whole chord sequence (in the order given — normally onset order) with a fresh
    /// tracker. Pure and deterministic.</summary>
    public static IReadOnlyList<(Chord? Treble, Chord? Bass)> Split(
        IReadOnlyList<Chord> chords,
        double leftCentre = DefaultLeftCentre, double rightCentre = DefaultRightCentre, double alpha = DefaultAlpha)
    {
        ArgumentNullException.ThrowIfNull(chords);
        var splitter = new HandSplitter(leftCentre, rightCentre, alpha);
        return chords.Select(splitter.SplitNext).ToList();
    }
}
