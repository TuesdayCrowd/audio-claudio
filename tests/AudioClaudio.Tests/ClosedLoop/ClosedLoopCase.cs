using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>A grid-exact monophonic performance: the input to one closed-loop trial.</summary>
public sealed record ClosedLoopCase(
    SampleRate Rate,
    int TempoBpm,
    TimeSignature TimeSignature,
    Subdivision Subdivision,
    IReadOnlyList<NoteEvent> Events)
{
    /// <summary>Stable short id derived from the case content; names quarantine artifacts.</summary>
    public string Id()
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Describe()));
        return System.Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant(); // 12 hex chars
    }

    public string Describe() =>
        $"bpm={TempoBpm};rate={Rate.Hz};notes=" +
        string.Join(",", Events.Select(e => $"{e.Pitch.MidiNumber}@{e.Onset.Samples}+{e.Duration.Samples}"));

    public override string ToString() => Describe();
}
