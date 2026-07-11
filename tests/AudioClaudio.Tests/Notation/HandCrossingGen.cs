using System;
using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.Notation;

/// <summary>
/// A focused corpus for the treble/bass split (v2 Stage 3c): two <b>continuous</b> diatonic lines (a left
/// and a right hand) that occasionally cross middle C for a short run before returning — the case a fixed
/// middle-C cut gets wrong and a temporal tracker can follow. Both hands play each beat (a two-note
/// vertical), with the true hand tagged, so the split is scored per note. Deterministic (plain seeded
/// <see cref="Random"/>); C major, straight quarters — key/rhythm are irrelevant to hand-tracking, so they
/// are held constant to keep the signal clean.
/// </summary>
public static class HandCrossingGen
{
    public const int SampleRateHz = 44100;
    public const int DefaultSeed = 90210;
    public const int Bpm = 100;

    private static readonly int[] CMajor = { 0, 2, 4, 5, 7, 9, 11 };

    // The two hands are a pair straddling a shared register centre r that WANDERS slowly across the
    // keyboard (a continuous random walk). left = r − gap/2, right = r + gap/2, so left < right always
    // (the split is unambiguous) — but as r drifts high the whole pair rises so the LEFT note climbs over
    // middle C (60), and as r drifts low the RIGHT note dips under it. Those are the notes a fixed middle-C
    // cut mis-assigns; because both lines are continuous, a temporal tracker follows them. r ∈ [50,72] so
    // both crossings occur.
    private const int RegisterLo = 50, RegisterHi = 72;

    public static IEnumerable<NotationCase> Cases(int count, int seed = DefaultSeed)
    {
        var rng = new Random(seed);
        for (int c = 0; c < count; c++)
        {
            yield return Build(rng);
        }
    }

    private static NotationCase Build(Random rng)
    {
        var rate = new SampleRate(SampleRateHz);
        int beats = 14 + rng.Next(0, 7); // 14–20 beats
        double samplesPerBeat = 60.0 / Bpm * rate.Hz;
        long OnsetSamples(int beat) => (long)Math.Round(beat * samplesPerBeat, MidpointRounding.AwayFromZero);
        var dur = new SampleDuration((long)Math.Round(samplesPerBeat), rate);

        var truth = new List<NotationNote>();
        int register = 60;
        for (int b = 0; b < beats; b++)
        {
            register = Math.Clamp(register + rng.Next(-2, 3), RegisterLo, RegisterHi); // ±2 slow walk
            int half = 4 + rng.Next(0, 3); // 4–6 semitones each side (gap 8–12)
            int left = SnapDiatonic(register - half);
            int right = SnapDiatonic(register + half);
            if (right <= left)
            {
                right = SnapDiatonic(left + 2); // keep left < right after snapping (defensive)
            }

            long onset = OnsetSamples(b);
            truth.Add(new NotationNote(new Pitch(left), b * 12, onset, 12, Hand.Left, 80));
            truth.Add(new NotationNote(new Pitch(right), b * 12, onset, 12, Hand.Right, 80));
        }

        var events = truth
            .Select(t => new NoteEvent(t.Pitch, new SamplePosition(t.OnsetSamples, rate), dur, t.Velocity))
            .ToList();
        return new NotationCase(Bpm, 0, events, truth);
    }

    // Nearest C-major pitch to a target MIDI (searching outward), so each line stays diatonic and continuous.
    private static int SnapDiatonic(int midi)
    {
        for (int d = 0; d <= 6; d++)
        {
            foreach (int cand in new[] { midi + d, midi - d })
            {
                if (Array.IndexOf(CMajor, ((cand % 12) + 12) % 12) >= 0)
                {
                    return cand;
                }
            }
        }

        return midi;
    }
}
