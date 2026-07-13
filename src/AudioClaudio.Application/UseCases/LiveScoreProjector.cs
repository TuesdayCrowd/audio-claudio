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
    private readonly bool _estimateTempo;
    private readonly List<NoteEvent> _events = new();

    public LiveScoreProjector(QuantizationGrid grid) : this(grid, estimateTempo: false) { }

    /// <summary>
    /// When <paramref name="estimateTempo"/> is true (v2 Stage 5), each update re-estimates the tempo
    /// from the notes so far — the median inter-onset interval, <see cref="TempoEstimator"/> — and
    /// re-quantizes at THAT tempo instead of the grid's fixed fallback. The live preview then
    /// converges to the final batch score's estimated tempo rather than sitting at the fallback and
    /// visibly "jumping" when the recording stops. It uses the SAME estimator and the SAME fallback
    /// (the grid's tempo) that <see cref="TranscriptionPipeline"/>'s batch pass uses, so the live and
    /// final tempos agree. Below the estimator's 3-note floor the fallback is returned unchanged, so
    /// the first couple of notes don't cause the staff to re-flow. When false (an explicit tempo was
    /// declared), the grid's fixed tempo is kept — identical to the original behavior.
    /// </summary>
    public LiveScoreProjector(QuantizationGrid grid, bool estimateTempo)
    {
        _grid = grid;
        _estimateTempo = estimateTempo;
    }

    /// <summary>The accumulated performance so far, in the order notes were added.</summary>
    public IReadOnlyList<NoteEvent> Events => _events;

    /// <summary>Append one note and return the Score quantized over every note added so far.</summary>
    public Score Add(NoteEvent note)
    {
        _events.Add(note);
        // QuantizationGrid's properties are get-only (no init accessor), so rebuild via its ctor
        // rather than a `with` expression.
        QuantizationGrid grid = _estimateTempo
            ? new QuantizationGrid(
                _grid.SampleRate, TempoEstimator.Estimate(_events, _grid.Tempo),
                _grid.TimeSignature, _grid.Subdivision)
            : _grid;
        return Quantizer.Quantize(_events, grid);
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
