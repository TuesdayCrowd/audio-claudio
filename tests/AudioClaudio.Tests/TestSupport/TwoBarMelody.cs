using System.Collections.Generic;
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.TestSupport;

/// <summary>
/// A fixed, deterministic two-bar monophonic line (C major scale, one note per beat,
/// 4/4 at 120 BPM, 44.1 kHz). Used as the Step 8 synthesis golden fixture and by the
/// CLI render/play path (<c>fixtures/golden/two-bar.mid</c>, generated from these same
/// notes via the Step 7 <c>DryWetMidiWriter</c>).
/// </summary>
public static class TwoBarMelody
{
    /// <summary>The sample rate the committed golden fixtures are pinned to.</summary>
    public static readonly SampleRate Rate = new(44100);

    /// <summary>The tempo the committed <c>two-bar.mid</c> fixture is written at.</summary>
    public static readonly Tempo Tempo = new(120);

    public static IReadOnlyList<NoteEvent> Notes(SampleRate rate)
    {
        int[] midi = { 60, 62, 64, 65, 67, 69, 71, 72 }; // C4..C5, all within MIDI 33..96
        const long step = 22050;    // one quarter note at 120 BPM @ 44.1 kHz

        // 19845 = 735 * 27 samples. At 44.1 kHz / 120 BPM / 480 ticks-per-quarter (the
        // Step 7 DryWetMidiWriter's PPQN), one MIDI tick is 735/16 samples; both `step`
        // (22050 = 735*30) and this duration are exact multiples of 735 samples, so every
        // onset AND duration in this melody round-trips through the committed MIDI fixture
        // bit-exactly in the sample domain (no tick-rounding jitter) — the golden WAV
        // comparison (Task 5) and the CLI render-over-MIDI comparison (Task 6) can
        // therefore share one committed reference and one tolerance. This still leaves a
        // ~50 ms gap (2205 samples) before the next onset, i.e. detached, not legato, notes.
        const long noteLen = 19845;
        const int velocity = 100;

        var notes = new List<NoteEvent>(midi.Length);
        for (int i = 0; i < midi.Length; i++)
        {
            var onset = new SamplePosition(step * i, rate);
            var duration = new SampleDuration(noteLen, rate);
            notes.Add(new NoteEvent(new Pitch(midi[i]), onset, duration, velocity));
        }
        return notes;
    }
}
