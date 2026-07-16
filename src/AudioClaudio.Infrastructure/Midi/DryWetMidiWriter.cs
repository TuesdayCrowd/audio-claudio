using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using NoteEvent = AudioClaudio.Domain.NoteEvent;
using Tempo = AudioClaudio.Domain.Tempo;

namespace AudioClaudio.Infrastructure.Midi;

/// <summary>
/// DryWetMIDI adapter that serializes both a quantized <see cref="Score"/> and a
/// raw <see cref="NoteEvent"/> performance to standard MIDI files.
/// </summary>
public sealed class DryWetMidiWriter : INoteEventWriter, IScoreWriter
{
    /// <summary>
    /// Ticks per quarter note. 480 = 2^5·3·5 makes every MVP grid value
    /// (whole..sixteenth and their dotted forms) land on an integer tick, so
    /// score grid positions serialize losslessly (R7.2). Also the DAW-standard.
    /// Mirrors <see cref="MidiTickMath.TicksPerQuarterNote"/> (kept public here since
    /// existing callers/tests reference it via this type).
    /// </summary>
    public const short TicksPerQuarterNote = MidiTickMath.TicksPerQuarterNote;

    public void Write(IReadOnlyList<NoteEvent> events, Tempo tempo, Stream destination)
    {
        var header = new List<ITimedObject> { MidiTickMath.TempoEvent(tempo) };
        var notes = new List<TimedNote>(events.Count);
        foreach (var e in events)
        {
            long onset = MidiTickMath.SamplesToTicks(e.Onset.Samples, e.Onset.Rate, tempo.BeatsPerMinute);
            long length = MidiTickMath.SamplesToTicks(e.Duration.Samples, e.Duration.Rate, tempo.BeatsPerMinute);

            // A sounding note must have positive length; a sub-tick duration
            // clamps to one tick, still within the R7.2 tick-resolution bound.
            notes.Add(new TimedNote(e.Pitch.MidiNumber, onset, Math.Max(1, length), e.Velocity));
        }

        WriteTrack(header, notes, destination);
    }

    public void Write(Score score, Stream destination)
    {
        var header = new List<ITimedObject>
        {
            MidiTickMath.TempoEvent(score.Tempo),
            new TimedEvent(
                new TimeSignatureEvent(
                    (byte)score.TimeSignature.BeatsPerMeasure,
                    (byte)score.TimeSignature.BeatUnit),
                0),
        };

        WriteTrack(header, FlattenScore(score), destination);
    }

    // ---- shared helpers -------------------------------------------------

    // Walks measures on the grid; ElementKind.Note emits, ElementKind.Rest only
    // advances the cursor. LengthTicks is in GRID ticks (a quarter note is
    // score.Subdivision.TicksPerQuarter() of them); convert to MIDI ticks by
    // multiplying by PPQN / gridTicksPerQuarter — exact because 480 is a multiple
    // of every MVP grid resolution.
    private static IReadOnlyList<TimedNote> FlattenScore(Score score)
    {
        int gridTicksPerQuarter = score.Subdivision.TicksPerQuarter();
        long ticksPerMeasure =
            score.TimeSignature.BeatsPerMeasure * (4L * TicksPerQuarterNote) / score.TimeSignature.BeatUnit;

        var notes = new List<TimedNote>();
        for (int m = 0; m < score.Measures.Count; m++)
        {
            long cursor = m * ticksPerMeasure;
            foreach (var element in score.Measures[m].Elements)
            {
                long length = (long)element.LengthTicks * TicksPerQuarterNote / gridTicksPerQuarter;
                if (element.Kind == ElementKind.Note)
                {
                    // Tied segments (TiedToNext) are emitted as adjacent notes; MIDI
                    // tie-merging for playback is a documented later refinement.
                    notes.Add(new TimedNote(element.Pitch!.Value.MidiNumber, cursor, length, element.Velocity));
                }

                cursor += length;
            }
        }

        return notes;
    }

    private static void WriteTrack(
        IReadOnlyList<ITimedObject> headerEvents,
        IReadOnlyList<TimedNote> notes,
        Stream destination)
    {
        var objects = new List<ITimedObject>(headerEvents);
        foreach (var n in notes)
        {
            objects.Add(MidiTickMath.NoteFromTicks(n.MidiNumber, n.TickOnset, n.TickLength, n.Velocity, (FourBitNumber)0));
        }

        var midiFile = new MidiFile(objects.ToTrackChunk())
        {
            TimeDivision = new TicksPerQuarterNoteTimeDivision(TicksPerQuarterNote),
        };
        midiFile.Write(destination);
    }

    private readonly record struct TimedNote(int MidiNumber, long TickOnset, long TickLength, int Velocity);
}
