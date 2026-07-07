using AudioClaudio.Domain;

namespace AudioClaudio.Application.Ports;

/// <summary>
/// Writes a raw, unquantized performance (a list of <see cref="NoteEvent"/> in
/// integer sample time) to a standard MIDI file. The declared <see cref="Tempo"/>
/// supplies the tempo map that maps sample time to MIDI ticks.
///
/// Deliberate 6th Application port beyond the constitution's five illustrative
/// ports (IAudioSource/ITranscriber/ISynthesizer/IScoreWriter/IClock), justified
/// by R7.1 (write BOTH a Score and a raw NoteEvent list). Recorded in CONTRACTS.md §7.
/// </summary>
public interface INoteEventWriter
{
    void Write(IReadOnlyList<NoteEvent> events, Tempo tempo, Stream destination);
}
