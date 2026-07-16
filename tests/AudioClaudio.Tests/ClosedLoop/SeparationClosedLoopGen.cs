using System;
using System.Collections.Generic;
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>
/// Generates random <b>multi-instrument</b> mixes for the source-separation closed-loop gate
/// (Stage 1.4) — the separation analogue of <see cref="PolyphonicClosedLoopGen"/>. A case is three
/// independent <b>monophonic</b> note sequences, each on its own General MIDI program and pitched in
/// its own band, so that the mix is a small but real separation problem: <b>bass</b> (GM 32, low
/// register) &#8594; Spleeter's "bass" stem, <b>piano</b> (GM 0, mid register) &#8594; "piano", and
/// <b>tenor sax</b> (GM 66) &#8594; "other". The three bands are kept <i>disjoint</i> (not just
/// non-overlapping "on average") so a wrong-stem separation is unambiguous, not a coin flip in a
/// shared octave. Each part is generated independently of the others (own pitch draws, own rhythm),
/// then rendered on its own synth and summed into a mix — the point of the corpus is realistic
/// concurrent instruments, not a shared melodic line.
/// </summary>
public static class SeparationClosedLoopGen
{
    public const int SampleRateHz = 44100;
    public const int DefaultSeed = 4242; // the committed gate corpus seed (plain Random, not CsCheck Sample)
    public const int SubdivisionsPerBeat = 4; // sixteenth grid
    public const int Velocity = 100; // MVP constant (R1.4)

    /// <summary>Standard note values in sixteenth subdivisions (eighth through half); all &gt;= 2
    /// (eighth), matching <c>ClosedLoopGen.StandardDurations</c>'s "&gt;= eighth" floor.</summary>
    private static readonly int[] StandardDurations = { 2, 3, 4, 6, 8 };

    // GM program numbers (0-indexed): 0 = Acoustic Grand Piano, 32 = Acoustic Bass, 66 = Tenor Sax.
    public const int BassGmProgram = 32;
    public const int PianoGmProgram = 0;
    public const int SaxGmProgram = 66;

    public const string BassTargetStem = "bass";
    public const string PianoTargetStem = "piano";
    public const string SaxTargetStem = "other";

    // Disjoint pitch bands (MIDI). Kept genuinely non-overlapping -- not merely "mostly separate" --
    // so a stem swap in the recovered output is an unambiguous separation failure, never a coincidence
    // of the two instruments sharing a pitch. Roughly low/mid/high, in the spirit of the suggested
    // ~28-48 / ~52-76 / ~60-84 bands, tightened at the boundaries so no two bands touch.
    public const int BassMidiLow = 28;
    public const int BassMidiHigh = 47;
    public const int PianoMidiLow = 52;
    public const int PianoMidiHigh = 63;
    public const int SaxMidiLow = 64;
    public const int SaxMidiHigh = 84;

    /// <summary>One instrument's monophonic note sequence, its GM program (for its own
    /// <c>MeltySynthSynthesizer</c>), and the Spleeter stem name it should recover as.</summary>
    public sealed record InstrumentPart(string TargetStem, int GmProgram, IReadOnlyList<NoteEvent> Notes);

    /// <summary>A mix: several instrument parts meant to be rendered independently and summed.</summary>
    public sealed record SeparationCase(IReadOnlyList<InstrumentPart> Parts);

    /// <summary>A deterministic, seeded corpus of <paramref name="count"/> three-instrument mixes.</summary>
    public static IEnumerable<SeparationCase> Cases(int count, int seed = DefaultSeed)
    {
        var rng = new Random(seed);
        for (int c = 0; c < count; c++)
        {
            yield return BuildRandom(rng);
        }
    }

    private static SeparationCase BuildRandom(Random rng)
    {
        int bpm = 80 + rng.Next(0, 41); // 80-120, shared tempo (as in a real ensemble)

        var parts = new[]
        {
            new InstrumentPart(
                BassTargetStem, BassGmProgram,
                BuildMonophonicSequence(rng, bpm, BassMidiLow, BassMidiHigh, NoteCount(rng))),
            new InstrumentPart(
                PianoTargetStem, PianoGmProgram,
                BuildMonophonicSequence(rng, bpm, PianoMidiLow, PianoMidiHigh, NoteCount(rng))),
            new InstrumentPart(
                SaxTargetStem, SaxGmProgram,
                BuildMonophonicSequence(rng, bpm, SaxMidiLow, SaxMidiHigh, NoteCount(rng))),
        };

        return new SeparationCase(parts);
    }

    private static int NoteCount(Random rng) => 5 + rng.Next(0, 4); // 5-8 notes -> a few seconds

    /// <summary>Builds one instrument's monophonic sequence: notes never overlap within the
    /// instrument (each separated by its own onset and at least one sixteenth of rest), all
    /// durations &gt;= an eighth note. Pure and deterministic given <paramref name="rng"/>'s state.</summary>
    private static IReadOnlyList<NoteEvent> BuildMonophonicSequence(
        Random rng, int bpm, int midiLow, int midiHigh, int noteCount)
    {
        var rate = new SampleRate(SampleRateHz);
        double samplesPerSub = 60.0 / bpm * rate.Hz / SubdivisionsPerBeat; // fractional
        long OnsetSamples(int sub) => (long)Math.Round(sub * samplesPerSub, MidpointRounding.AwayFromZero);

        var events = new List<NoteEvent>(noteCount);
        int cursorSub = 0;
        for (int i = 0; i < noteCount; i++)
        {
            int midi = midiLow + rng.Next(0, midiHigh - midiLow + 1);
            int dur = StandardDurations[rng.Next(StandardDurations.Length)];

            int onsetSub = cursorSub;
            int endSub = onsetSub + dur;
            long onset = OnsetSamples(onsetSub);
            long duration = OnsetSamples(endSub) - onset;

            events.Add(new NoteEvent(
                new Pitch(midi),
                new SamplePosition(onset, rate),
                new SampleDuration(duration, rate),
                Velocity));

            int gap = 1 + rng.Next(0, 3); // 1-3 sixteenths of rest -> non-overlapping (R5.4-style separation)
            cursorSub = endSub + gap;
        }

        return events;
    }
}
