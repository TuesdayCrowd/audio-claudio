using System.Collections.Generic;
using AudioClaudio.Domain;

namespace AudioClaudio.Application.UseCases;

/// <summary>
/// Projects an incremental note stream onto a growing, approximate live <see cref="Score"/>:
/// each new note is appended to the performance so far and the WHOLE performance is
/// re-quantized (<see cref="Quantizer.Quantize"/> is pure and cheap enough at session-length
/// note counts -- see the live-notation design doc's "Full re-render per update" decision).
/// Pure and I/O-free: Domain types only, no clock, no device, no HTTP -- the same discipline
/// as <see cref="TranscriptionPipeline"/> and <see cref="LiveTranscriptionSession"/>.
///
/// Deliberately push-shaped (<see cref="Add"/>), not a pull over <c>IAudioSource</c>: a live
/// capture channel drains exactly once (see <see cref="LiveTranscriptionSession"/>'s remarks),
/// and that one drain is already owned by <see cref="LiveTranscriptionSession.Run"/>'s single
/// <c>onNote</c> callback. The Cli composition root feeds this projector from THAT callback
/// (see <c>ListenCommand</c>'s <c>onLiveNote</c> hook, Task 5) rather than re-enumerating the
/// source.
/// </summary>
public sealed class LiveScoreProjector
{
    private readonly QuantizationGrid _grid;
    private readonly List<NoteEvent> _events = new();

    public LiveScoreProjector(QuantizationGrid grid) => _grid = grid;

    /// <summary>The accumulated performance so far, in the order notes were added.</summary>
    public IReadOnlyList<NoteEvent> Events => _events;

    /// <summary>Append one note and return the Score quantized over every note added so far.</summary>
    public Score Add(NoteEvent note)
    {
        _events.Add(note);
        return Quantizer.Quantize(_events, _grid);
    }

    /// <summary>
    /// Pull-shaped convenience over an already-materialized (or fake, for tests) note sequence:
    /// one growing Score per note, via repeated <see cref="Add"/>. NOT used directly against a
    /// live <c>IAudioSource</c> -- see the remarks above.
    /// </summary>
    public IEnumerable<Score> LiveScores(IEnumerable<NoteEvent> notes)
    {
        foreach (NoteEvent note in notes)
        {
            yield return Add(note);
        }
    }
}
