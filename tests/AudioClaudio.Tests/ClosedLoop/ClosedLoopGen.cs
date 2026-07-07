using System;
using System.Collections.Generic;
using AudioClaudio.Domain;
using CsCheck;

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>
/// Generates the closed-loop corpora and one fixed case for the determinism guard.
///
/// R9.1 (MIDI 33-96, notes ≥ eighth, 60-140 BPM, ≥1 grid rest) is preserved in full, with ONE
/// physically-motivated refinement decided by Cornelius (see DECISIONS.md, "Step 9 — closed-loop
/// corpus constrained to physically-audible note durations"): in <see cref="Cases"/> a note's
/// declared duration is additionally capped so the note is still clearly sounding for its whole
/// declared length. A piano note rendered by the committed SoundFont decays past the pipeline's
/// offset-detection threshold in a pitch-dependent time <see cref="AudibleSeconds"/>; declaring a
/// duration longer than that asks the transcriber to recover a sustain the audio physically does
/// not contain. The cap is derived directly from that measured decay curve, at the SAME threshold
/// the detector uses — no R9.2 tolerance is weakened.
///
/// <see cref="FullRangeCases"/> is the UNCAPPED corpus (full MIDI 33-96, any standard duration): it
/// backs the "count/pitch/onset across the whole keyboard" claim, since offset refinement only ever
/// changes durations — never note count, pitch, or onset — so those three are recoverable across the
/// full range even for pitches that cannot sustain an audible eighth (their notes are still
/// note-ON long enough to detect an attack and a stable pitch).
/// </summary>
public static class ClosedLoopGen
{
    public const int SubdivisionsPerBeat = 4; // sixteenth-note grid
    public const int MidiLow = 33; // A1
    public const int MidiHigh = 96; // C7
    public const int Velocity = 100; // MVP constant (R1.4)
    public const int SampleRateHz = 44100; // fixed so MeltySynth renders are deterministic

    /// <summary>
    /// Fraction of a pitch's raw audible time that a declared note duration may occupy. Below 1.0
    /// so a note is comfortably above the offset threshold at its declared note-off (not right at
    /// it). Chosen with the settle/ratio pair by an empirical joint sweep over the real pipeline
    /// (DECISIONS.md); at this value the constrained corpus recovers DURATION within R9.2 tolerance
    /// across the corpus (zero duration failures observed over the validation batches).
    /// </summary>
    public const double DurationSafetyMargin = 0.85;

    /// <summary>The standard note values the corpus draws from, in sixteenth subdivisions
    /// (eighth, dotted-eighth, quarter, dotted-quarter, half). All ≥ 2 (R9.1: ≥ eighth).</summary>
    public static readonly int[] StandardDurations = { 2, 3, 4, 6, 8 };

    /// <summary>
    /// T_audible(p) in seconds, indexed by <c>midi - MidiLow</c>: how long a held note of that
    /// pitch stays above the pipeline's offset threshold — the point where its RMS falls to
    /// <c>OffsetReleaseRatio</c> (0.50) of the reference level sampled <c>OffsetSettleFrames</c>
    /// (5) frames after onset, matching
    /// <see cref="AudioClaudio.Application.TranscriptionSettings"/> exactly. Measured once against
    /// the committed GeneralUser GS SoundFont (derivation + full table in DECISIONS.md). Values
    /// are self-consistent with the detector, so a note declared ≤ margin·T_audible is guaranteed
    /// above threshold at its note-off.
    /// </summary>
    private static readonly double[] AudibleSeconds =
    {
        1.556, 1.416, 1.161, 1.161, // 33-36
        1.231, 1.347, 1.068, 1.335, // 37-40
        0.836, 0.801, 0.743, 0.720, // 41-44
        0.708, 0.685, 0.929, 0.894, // 45-48
        0.975, 0.871, 0.731, 0.731, // 49-52
        0.697, 0.650, 0.673, 0.650, // 53-56
        0.650, 0.952, 0.998, 0.998, // 57-60
        0.522, 0.580, 0.546, 0.546, // 61-64
        0.743, 0.731, 0.708, 0.708, // 65-68
        0.685, 0.290, 0.279, 0.232, // 69-72
        0.232, 0.232, 0.221, 0.221, // 73-76
        0.174, 0.174, 0.174, 0.163, // 77-80
        0.163, 0.139, 0.128, 0.128, // 81-84
        0.128, 0.116, 0.116, 0.151, // 85-88
        0.139, 0.139, 0.139, 0.128, // 89-92
        0.081, 0.081, 0.081, 0.081, // 93-96
    };

    /// <summary>The audible-duration-capped corpus (main strict closed loop): proves all four R9.2
    /// criteria (count, pitch, onset, duration). Its top pitch is limited by audibility at each
    /// tempo (see DECISIONS.md); use <see cref="FullRangeCases"/> for whole-keyboard coverage.</summary>
    public static readonly Gen<ClosedLoopCase> Cases =
        from bpm in Gen.Int[60, 140]
        from count in Gen.Int[3, 8]
            // Selectors mapped through the bpm-dependent valid sets in Build (total, no rejection
            // sampling — keeps generation deterministic for the direct Gen.Generate path).
        from pitchSel in Gen.Int[0, 1_000_000].Array[count]
        from durSel in Gen.Int[0, 1_000_000].Array[count]
        from rests in Gen.Int[1, 4].Array[count] // >= 1 grid subdivision of rest between notes
        select BuildFromSelectors(bpm, pitchSel, durSel, rests);

    /// <summary>The uncapped corpus (full MIDI 33-96, any standard duration ≥ eighth): backs the
    /// count/pitch/onset coverage claim across the whole keyboard (duration NOT asserted for it —
    /// high pitches cannot sustain an audible eighth, which is exactly what <see cref="Cases"/>
    /// exists to respect).</summary>
    public static readonly Gen<ClosedLoopCase> FullRangeCases =
        from bpm in Gen.Int[60, 140]
        from count in Gen.Int[3, 8]
        from pitches in Gen.Int[MidiLow, MidiHigh].Array[count]
        from durSel in Gen.Int[0, 1_000_000].Array[count]
        from rests in Gen.Int[1, 4].Array[count]
        select BuildUncapped(bpm, pitches, durSel, rests);

    /// <summary>Four quarter notes at 120 BPM — RNG-free, for the determinism guard (Task 6).
    /// Each pitch/duration here is within its audible cap at 120 BPM.</summary>
    public static ClosedLoopCase Fixed() =>
        Build(120, new[] { 60, 64, 67, 72 }, new[] { 4, 4, 4, 4 }, new[] { 1, 1, 1, 1 });

    /// <summary>The raw audible-decay time (seconds) measured for a pitch.</summary>
    public static double AudibleSecondsFor(int midi) => AudibleSeconds[midi - MidiLow];

    /// <summary>
    /// The largest declared duration, in sixteenth subdivisions, that stays audible for
    /// <paramref name="midi"/> at <paramref name="bpm"/>: <c>floor(margin · T_audible · bpm / 15)</c>
    /// (one sixteenth = 15/bpm seconds). May be 0 or 1 for the fastest-decaying pitches — those
    /// pitches are simply not offered at that tempo in <see cref="Cases"/> (see <see cref="ValidPitches"/>).
    /// </summary>
    public static int MaxDurationSub(int midi, int bpm, double margin = DurationSafetyMargin)
    {
        double capSeconds = margin * AudibleSecondsFor(midi);
        return (int)Math.Floor(capSeconds * bpm / (60.0 / SubdivisionsPerBeat));
    }

    /// <summary>
    /// Pitches that can hold at least an eighth note (2 subdivisions) audibly at
    /// <paramref name="bpm"/>. At fast tempos this reaches higher up the keyboard; the fastest-
    /// decaying pitches never qualify at any tempo in range (documented in DECISIONS.md). Never
    /// empty.
    /// </summary>
    public static IReadOnlyList<int> ValidPitches(int bpm, double margin = DurationSafetyMargin)
    {
        var list = new List<int>();
        for (int m = MidiLow; m <= MidiHigh; m++)
        {
            if (MaxDurationSub(m, bpm, margin) >= 2)
            {
                list.Add(m);
            }
        }

        return list;
    }

    /// <summary>The standard durations allowed for a pitch/tempo: <see cref="StandardDurations"/>
    /// capped to those that stay audible. Non-empty whenever <paramref name="midi"/> came from
    /// <see cref="ValidPitches"/> (its max is ≥ 2, and 2 is the smallest standard value).</summary>
    public static IReadOnlyList<int> AllowedDurations(int midi, int bpm, double margin = DurationSafetyMargin)
    {
        int max = MaxDurationSub(midi, bpm, margin);
        var list = new List<int>();
        foreach (int d in StandardDurations)
        {
            if (d <= max)
            {
                list.Add(d);
            }
        }

        return list;
    }

    /// <summary>Resolves per-note pitch/duration selectors against the bpm-dependent audible caps,
    /// then builds the case (the capped <see cref="Cases"/> corpus).</summary>
    internal static ClosedLoopCase BuildFromSelectors(int bpm, int[] pitchSel, int[] durSel, int[] rests)
        => BuildFromSelectors(bpm, pitchSel, durSel, rests, DurationSafetyMargin);

    internal static ClosedLoopCase BuildFromSelectors(int bpm, int[] pitchSel, int[] durSel, int[] rests, double margin)
    {
        IReadOnlyList<int> validPitches = ValidPitches(bpm, margin);
        int count = pitchSel.Length;
        var pitches = new int[count];
        var durs = new int[count];
        for (int i = 0; i < count; i++)
        {
            int midi = validPitches[pitchSel[i] % validPitches.Count];
            IReadOnlyList<int> allowed = AllowedDurations(midi, bpm, margin);
            pitches[i] = midi;
            durs[i] = allowed[durSel[i] % allowed.Count];
        }

        return Build(bpm, pitches, durs, rests);
    }

    /// <summary>Builds the uncapped <see cref="FullRangeCases"/> case: full-range pitches, any
    /// standard duration (no audible cap).</summary>
    internal static ClosedLoopCase BuildUncapped(int bpm, int[] pitches, int[] durSel, int[] rests)
    {
        int count = pitches.Length;
        var durs = new int[count];
        for (int i = 0; i < count; i++)
        {
            durs[i] = StandardDurations[durSel[i] % StandardDurations.Length];
        }

        return Build(bpm, pitches, durs, rests);
    }

    internal static ClosedLoopCase Build(int bpm, int[] pitches, int[] durs, int[] rests)
    {
        var rate = new SampleRate(SampleRateHz);
        double sps = 60.0 / bpm * rate.Hz / SubdivisionsPerBeat; // samples per sixteenth (fractional)
        long OnsetSamples(int sub) => (long)Math.Round(sub * sps, MidpointRounding.AwayFromZero);

        var events = new List<NoteEvent>(pitches.Length);
        int cursorSub = 0;
        for (int i = 0; i < pitches.Length; i++)
        {
            int onsetSub = cursorSub;
            int endSub = onsetSub + durs[i];
            long onset = OnsetSamples(onsetSub);
            long duration = OnsetSamples(endSub) - onset;

            events.Add(new NoteEvent(
                new Pitch(pitches[i]),
                new SamplePosition(onset, rate),
                new SampleDuration(duration, rate),
                Velocity));

            cursorSub = endSub + rests[i]; // rest >= 1 subdivision keeps it monophonic + separated
        }

        return new ClosedLoopCase(rate, bpm, TimeSignature.FourFour, Subdivision.Sixteenth, events);
    }
}
