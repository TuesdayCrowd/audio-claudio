using AudioClaudio.Domain;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using NoteEvent = AudioClaudio.Domain.NoteEvent;
using Tempo = AudioClaudio.Domain.Tempo;

namespace AudioClaudio.Infrastructure.Midi;

/// <summary>
/// The events recovered from a MIDI file plus the tempo read from its SetTempo event.
/// Sample rate is not stored in MIDI, so the caller supplies one to denominate the
/// recovered <see cref="SamplePosition"/>s.
/// </summary>
public readonly record struct MidiReadResult
{
    public IReadOnlyList<NoteEvent> Events { get; }
    public Tempo Tempo { get; }

    /// <summary>The sustain-pedal (CC64) presses/releases, in sample positions — for notation (pedal marks).</summary>
    public IReadOnlyList<SustainPedal.Change> PedalChanges { get; }

    public MidiReadResult(IReadOnlyList<NoteEvent> events, Tempo tempo, IReadOnlyList<SustainPedal.Change> pedalChanges)
    {
        Events = events;
        Tempo = tempo;
        PedalChanges = pedalChanges;
    }
}

/// <summary>
/// Reads standard MIDI files back into the domain's <see cref="NoteEvent"/>s, the
/// read-back half of R7.2. Concrete Infrastructure type (not a port); Steps 8 and 9
/// construct it directly. Inverse of <see cref="DryWetMidiWriter"/>.
/// </summary>
public static class MidiFileReader
{
    /// <param name="flattenPedal">When true (default), fold sustain-pedal (CC64) spans into note
    /// durations so a pedalled MIDI synthesizes correctly through the pedal-less note→synth path. Pass
    /// false for notation, which wants the raw key-press durations (the pedal belongs as a mark, not a
    /// longer note).</param>
    public static MidiReadResult Read(Stream source, SampleRate rate, bool flattenPedal = true)
    {
        var midi = MidiFile.Read(source);
        double bpm = ReadTempoBpm(midi);
        int ppqn = ((TicksPerQuarterNoteTimeDivision)midi.TimeDivision!).TicksPerQuarterNote;

        var events = new List<NoteEvent>();
        foreach (var note in midi.GetNotes().OrderBy(n => n.Time))
        {
            long onsetSamples = TicksToSamples(note.Time, ppqn, bpm, rate);
            long durationSamples = TicksToSamples(note.Length, ppqn, bpm, rate);
            events.Add(new NoteEvent(
                new Pitch((int)note.NoteNumber),
                new SamplePosition(onsetSamples, rate),
                new SampleDuration(durationSamples, rate),
                (int)note.Velocity));
        }

        // Read the sustain-pedal (CC64) changes (value ≥ 64 = down). They are exposed on the result for
        // notation (pedal marks) and, when flattening, folded into note durations so a pedalled MIDI
        // synthesizes correctly through the pedal-less note→synth path (our own writer emits no CC64).
        var pedalChanges = midi.GetTimedEvents()
            .Where(te => te.Event is ControlChangeEvent cc && (byte)cc.ControlNumber == 64)
            .OrderBy(te => te.Time)
            .Select(te => new SustainPedal.Change(
                TicksToSamples(te.Time, ppqn, bpm, rate),
                (byte)((ControlChangeEvent)te.Event).ControlValue >= 64))
            .ToList();

        IReadOnlyList<NoteEvent> notes = flattenPedal ? SustainPedal.Flatten(events, pedalChanges) : events;
        return new MidiReadResult(notes, new Tempo(bpm), pedalChanges);
    }

    public static MidiReadResult ReadFile(string path, SampleRate rate, bool flattenPedal = true)
    {
        using var fs = File.OpenRead(path);
        return Read(fs, rate, flattenPedal);
    }

    // samples = ticks · 60 · rate.Hz / (PPQN · BPM) — inverse of the writer's map.
    private static long TicksToSamples(long ticks, int ppqn, double bpm, SampleRate rate)
    {
        double samples = (double)ticks * 60.0 * rate.Hz / (ppqn * bpm);
        return (long)Math.Round(samples, MidpointRounding.ToEven);
    }

    private static double ReadTempoBpm(MidiFile midi)
    {
        var setTempo = midi.GetTrackChunks()
            .SelectMany(c => c.Events)
            .OfType<SetTempoEvent>()
            .FirstOrDefault();
        long microsecondsPerQuarter = setTempo?.MicrosecondsPerQuarterNote ?? 500_000L;   // MIDI default = 120 BPM
        return 60_000_000.0 / microsecondsPerQuarter;
    }
}
