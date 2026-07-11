using System;
using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>
/// Generates random <b>polyphonic</b> scores for the closed-loop fidelity gate — the chord-wise
/// analogue of <see cref="ClosedLoopGen"/>. A case is a sequence of chords (each 1–4 distinct pitches
/// sharing one onset), separated by rests, sustained ~a quarter note.
///
/// Range is capped at <see cref="MidiCeiling"/> (C5) on purpose. Above it, the committed GeneralUser GS
/// SoundFont's notes decay below detectability (~128 ms, the decoder's min-note-length) before an attack
/// can register — so including them would measure the <i>SoundFont's decay</i>, not the <i>engine</i>.
/// This is the same physical-audibility discipline <see cref="ClosedLoopGen"/> applies (DECISIONS.md,
/// "closed-loop corpus constrained to physically-audible note durations"). Onset+pitch are what the
/// gate scores (durations ignored), so a fast decay past the declared note-off is harmless here as
/// long as the attack is detectable — which the cap guarantees.
/// </summary>
public static class PolyphonicClosedLoopGen
{
    public const int SampleRateHz = 44100;
    public const int DefaultSeed = 4242;        // the committed gate corpus seed (fully reproducible: plain Random, not CsCheck Sample)
    public const int SubdivisionsPerBeat = 4;   // sixteenth grid
    public const int MidiLow = 40;              // E2
    public const int RootCeiling = 60;          // chord roots stay ≤ middle C…
    public const int MidiCeiling = 72;          // …so with a ≤ +12 top interval no note exceeds C5
    public const int Velocity = 100;            // MVP constant (R1.4)

    // Chord-tone-ish intervals above the root (thirds/fourths/fifths/sixths/sevenths/octave).
    private static readonly int[] Intervals = { 3, 4, 5, 7, 8, 9, 10, 12 };

    /// <summary>
    /// Builds a polyphonic score from explicit chords. <paramref name="chordPitches"/>[i] holds chord i's
    /// pitches (all sharing one onset); <paramref name="durs"/>[i] is its sustain and
    /// <paramref name="gaps"/>[i] the rest after it, both in sixteenth subdivisions. Pure and
    /// deterministic; onsets are integer samples on the tempo grid (non-negotiable 1).
    /// </summary>
    public static IReadOnlyList<NoteEvent> Build(int bpm, int[][] chordPitches, int[] durs, int[] gaps)
    {
        var rate = new SampleRate(SampleRateHz);
        double samplesPerSub = 60.0 / bpm * rate.Hz / SubdivisionsPerBeat; // fractional
        long OnsetSamples(int sub) => (long)Math.Round(sub * samplesPerSub, MidpointRounding.AwayFromZero);

        var events = new List<NoteEvent>();
        int cursorSub = 0;
        for (int i = 0; i < chordPitches.Length; i++)
        {
            int onsetSub = cursorSub;
            int endSub = onsetSub + durs[i];
            long onset = OnsetSamples(onsetSub);
            long duration = OnsetSamples(endSub) - onset;
            foreach (int pitch in chordPitches[i])
            {
                events.Add(new NoteEvent(
                    new Pitch(pitch),
                    new SamplePosition(onset, rate),
                    new SampleDuration(duration, rate),
                    Velocity));
            }

            cursorSub = endSub + gaps[i]; // rest ≥ 1 subdivision separates chord onsets
        }

        return events;
    }

    /// <summary>A deterministic, seeded corpus of <paramref name="count"/> polyphonic cases — each a
    /// handful of chords totalling a few seconds of audio (short enough for repeated ONNX inference).</summary>
    public static IEnumerable<IReadOnlyList<NoteEvent>> Cases(int count, int seed = DefaultSeed)
    {
        var rng = new Random(seed);
        for (int c = 0; c < count; c++)
        {
            yield return BuildRandom(rng);
        }
    }

    private static IReadOnlyList<NoteEvent> BuildRandom(Random rng)
    {
        int bpm = 80 + rng.Next(0, 41);      // 80–120
        int chords = 5 + rng.Next(0, 3);     // 5–7 chords → ~4–6 s of audio
        var chordPitches = new int[chords][];
        var durs = new int[chords];
        var gaps = new int[chords];
        for (int i = 0; i < chords; i++)
        {
            chordPitches[i] = RandomChord(rng);
            durs[i] = rng.Next(0, 3) == 0 ? 8 : 4;  // mostly quarter, some half
            gaps[i] = 2 + rng.Next(0, 3);           // 2–4 sixteenths of rest
        }

        return Build(bpm, chordPitches, durs, gaps);
    }

    private static int[] RandomChord(Random rng)
    {
        int voices = 1 + rng.Next(0, 4); // 1–4 notes
        int root = MidiLow + rng.Next(0, RootCeiling - MidiLow + 1);
        var set = new SortedSet<int> { root };
        int guard = 0;
        while (set.Count < voices && guard++ < 16)
        {
            int p = root + Intervals[rng.Next(Intervals.Length)];
            if (p <= MidiCeiling)
            {
                set.Add(p);
            }
        }

        return set.ToArray();
    }
}
