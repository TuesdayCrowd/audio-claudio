using AudioClaudio.Domain;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using NoteEvent = AudioClaudio.Domain.NoteEvent;
using Tempo = AudioClaudio.Domain.Tempo;

namespace AudioClaudio.Infrastructure.Midi;

/// <summary>
/// DryWetMIDI adapter that writes a genuinely multi-track standard MIDI file — one
/// <see cref="TrackChunk"/> per instrument stem, each carrying its own track name, GM
/// program (on its own channel), and notes — so the file plays back like the original
/// band rather than collapsing every stem onto one track/channel. The Stage-3 sibling of
/// <see cref="DryWetMidiWriter"/>, which only ever writes a single track; the two share
/// the exact tick math and note construction via <see cref="MidiTickMath"/> (DRY, no
/// byte-level drift between them).
/// </summary>
public sealed class MultiTrackMidiWriter
{
    /// <summary>
    /// General MIDI channel 9 (0-indexed) is reserved for percussion; assigning tracks by
    /// index would collide with it once a 10th track appeared. The current pipeline caps
    /// at 4 tracks (piano/bass/other/vocals) so this is a documented guard, not yet a
    /// reachable case.
    /// </summary>
    private const int PercussionChannel = 9;

    /// <summary>
    /// Writes one track per entry in <paramref name="tracks"/>. Each track gets a
    /// <see cref="SequenceTrackNameEvent"/> and a <see cref="ProgramChangeEvent"/> at tick
    /// 0, then its notes, all on a channel assigned by track index (0, 1, 2, …, skipping 9
    /// = percussion). The global <see cref="SetTempoEvent"/> lives at tick 0 of the first
    /// track (the conductor track), matching <see cref="DryWetMidiWriter"/>'s convention.
    /// All notes across all tracks are assumed to share one <see cref="SampleRate"/> (the
    /// caller — Stage 2's rate reconciliation — guarantees this); each note's own
    /// <c>Onset.Rate</c> is used for its tick conversion regardless.
    /// </summary>
    public void Write(
        IReadOnlyList<(string Name, int GmProgram, IReadOnlyList<NoteEvent> Notes)> tracks,
        Tempo tempo,
        Stream destination)
    {
        var chunks = new List<TrackChunk>(tracks.Count);
        for (int i = 0; i < tracks.Count; i++)
        {
            var (name, gmProgram, notes) = tracks[i];
            var channel = (FourBitNumber)ChannelForTrack(i);

            var objects = new List<ITimedObject>();
            if (i == 0)
            {
                objects.Add(MidiTickMath.TempoEvent(tempo));
            }

            objects.Add(new TimedEvent(new SequenceTrackNameEvent(name), 0));
            objects.Add(new TimedEvent(new ProgramChangeEvent((SevenBitNumber)gmProgram) { Channel = channel }, 0));

            foreach (var note in notes)
            {
                objects.Add(MidiTickMath.ToNote(note, tempo.BeatsPerMinute, channel));
            }

            chunks.Add(objects.ToTrackChunk());
        }

        var midiFile = new MidiFile(chunks.ToArray())
        {
            TimeDivision = new TicksPerQuarterNoteTimeDivision(MidiTickMath.TicksPerQuarterNote),
        };

        // DryWetMIDI's default write format (MidiFileFormat.MultiTrack) auto-splits a file
        // that ends up with EXACTLY one physical TrackChunk into two (a meta-only "conductor"
        // chunk plus a channel-events chunk) — its own accommodation for Format 1 conventionally
        // having more than one track. That's harmless content-wise but would defeat a
        // single-instrument caller's expectation of one track = one chunk, so pin the format
        // explicitly: MultiTrack chunk counts (0 or 2+) are unaffected by this quirk and pass
        // through unchanged (verified empirically), so only the count-1 case needs SingleTrack.
        var format = chunks.Count == 1 ? MidiFileFormat.SingleTrack : MidiFileFormat.MultiTrack;
        midiFile.Write(destination, format);
    }

    // Channels 0..8 map directly to track indices 0..8; index 9 onward skips the reserved
    // percussion channel (9) by shifting up by one. Documented guard for future track counts.
    private static int ChannelForTrack(int index) => index < PercussionChannel ? index : index + 1;
}
