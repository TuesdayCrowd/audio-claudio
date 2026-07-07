using AudioClaudio.Domain;

namespace AudioClaudio.Application.Ports;

/// <summary>
/// Writes a quantized <see cref="Score"/> to a destination. Format-agnostic:
/// the MIDI writer implements it here; the MusicXML writer will implement it in Step 11.
/// </summary>
public interface IScoreWriter
{
    void Write(Score score, Stream destination);
}
