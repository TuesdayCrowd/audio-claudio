using AudioClaudio.Domain;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using NoteEvent = AudioClaudio.Domain.NoteEvent;
using Tempo = AudioClaudio.Domain.Tempo;

namespace AudioClaudio.Infrastructure.Midi;

/// <summary>
/// Sample-to-MIDI-tick conversion and note emission shared by every MIDI writer in this namespace
/// (<see cref="DryWetMidiWriter"/> and <see cref="MultiTrackMidiWriter"/>) — factored out so both
/// writers use the exact same tick math rather than two copies drifting apart (R7.2).
/// </summary>
internal static class MidiTickMath
{
    /// <summary>
    /// Ticks per quarter note. 480 = 2^5·3·5 makes every MVP grid value (whole..sixteenth and their
    /// dotted forms) land on an integer tick, so score grid positions serialize losslessly (R7.2).
    /// Also the DAW-standard.
    /// </summary>
    public const short TicksPerQuarterNote = 480;

    /// <summary>ticksPerSecond = PPQN · BPM / 60  ⇒  ticks = samples/rate · ticksPerSecond.</summary>
    public static long SamplesToTicks(long samples, SampleRate rate, double bpm)
    {
        double ticks = (double)samples * TicksPerQuarterNote * bpm / (60.0 * rate.Hz);
        return (long)System.Math.Round(ticks, System.MidpointRounding.ToEven);
    }

    public static TimedEvent TempoEvent(Tempo tempo)
    {
        long microsecondsPerQuarter = (long)System.Math.Round(60_000_000.0 / tempo.BeatsPerMinute);
        return new TimedEvent(new SetTempoEvent(microsecondsPerQuarter), 0);
    }

    /// <summary>
    /// Converts one <see cref="NoteEvent"/> to a DryWetMIDI <see cref="Note"/> at the given tempo and
    /// channel. Length is floored at 1 tick — a sounding note must have positive length, and a
    /// sub-tick duration still clamps within the R7.2 tick-resolution bound.
    /// </summary>
    public static Note ToNote(NoteEvent e, double bpm, FourBitNumber channel)
    {
        long onset = SamplesToTicks(e.Onset.Samples, e.Onset.Rate, bpm);
        long length = SamplesToTicks(e.Duration.Samples, e.Duration.Rate, bpm);
        return NoteFromTicks(e.Pitch.MidiNumber, onset, length, e.Velocity, channel);
    }

    /// <summary>
    /// Builds a DryWetMIDI <see cref="Note"/> directly from already-computed tick values (used by the
    /// grid-flattened <see cref="Score"/> path, whose ticks come from <c>LengthTicks</c> arithmetic
    /// rather than a sample count). Length is floored at 1 tick, same rationale as <see cref="ToNote"/>.
    /// </summary>
    public static Note NoteFromTicks(int midiNumber, long onsetTicks, long lengthTicks, int velocity, FourBitNumber channel)
    {
        return new Note((SevenBitNumber)midiNumber, System.Math.Max(1, lengthTicks), onsetTicks)
        {
            Velocity = (SevenBitNumber)velocity,
            Channel = channel,
        };
    }
}
