using System;
using System.Collections.Generic;
using System.Threading;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;

namespace AudioClaudio.Application.UseCases;

/// <summary>The result of a live session: the accurate raw performance and its quantized score.</summary>
public sealed record LiveSessionResult(IReadOnlyList<NoteEvent> Events, Score Score)
{
    /// <summary>The raw analysis frames captured during the live pass, in order — the exact audio
    /// the transcriber heard, reassemblable via <see cref="AudioClaudio.Domain.Framing.ReconstructMono"/>
    /// (used by `listen --record` to write input.wav). Empty unless the session captured audio.</summary>
    public IReadOnlyList<Frame> CapturedFrames { get; init; } = Array.Empty<Frame>();
}

/// <summary>
/// Orchestrates a live transcription with two clearly separated jobs (Step 10, R10.3):
/// <list type="bullet">
/// <item><b>Live print (incremental):</b> pull notes from the injected <c>streamNotes</c> feed —
/// production binds it to <c>TranscriptionPipeline.StreamNotes</c>, the genuinely lazy causal
/// detector — and report each one via <c>onNote</c> as it is detected, at low latency.</item>
/// <item><b>Accurate files (batch, on stop):</b> the frames that flow through the live pass are
/// tee'd into a buffer; when the source ends (Ctrl+C stops the device), the injected batch
/// <c>transcribe</c> — production binds it to <c>TranscriptionPipeline.Transcribe</c> — runs over
/// exactly those buffered frames to produce the accurate raw events + quantized <see cref="Score"/>
/// that the output files are written from.</item>
/// </list>
/// The live device's <c>Frames</c> is enumerated EXACTLY ONCE (a live channel drains only once):
/// the recorder replays its captured frames to the batch pass. No I/O, no device, no clock reads;
/// all transcription lives in the injected pipeline (R10.4).
/// </summary>
public sealed class LiveTranscriptionSession
{
    private readonly Func<IAudioSource, IEnumerable<NoteEvent>> _streamNotes;
    private readonly Func<IAudioSource, TranscriptionResult> _transcribe;

    public LiveTranscriptionSession(
        Func<IAudioSource, IEnumerable<NoteEvent>> streamNotes,
        Func<IAudioSource, TranscriptionResult> transcribe)
    {
        _streamNotes = streamNotes;
        _transcribe = transcribe;
    }

    public LiveSessionResult Run(IAudioSource source, Action<NoteEvent> onNote, CancellationToken ct = default)
    {
        var recorder = new FrameRecordingAudioSource(source);

        // Live print: drive the incremental feed to natural completion (the source ending IS the
        // stop signal — Ctrl+C completes the capture channel). Iterating to completion also drains
        // the recorder, so every frame is captured for the batch pass below. `ct` is a secondary
        // guard for a still-flowing source.
        foreach (NoteEvent note in _streamNotes(recorder))
        {
            onNote(note);
            if (ct.IsCancellationRequested)
            {
                break;
            }
        }

        // Accurate files: batch-transcribe exactly the frames the live pass consumed.
        TranscriptionResult batch = _transcribe(recorder.ToBufferedSource());
        return new LiveSessionResult(batch.RawEvents, batch.Score) { CapturedFrames = recorder.CapturedFrames };
    }

    /// <summary>
    /// Tees an inner <see cref="IAudioSource"/>: yields each frame to the live consumer while
    /// capturing it, so the same frames can be replayed to the batch pass without enumerating the
    /// (once-only) live source twice.
    /// </summary>
    private sealed class FrameRecordingAudioSource : IAudioSource
    {
        private readonly IAudioSource _inner;
        private readonly List<Frame> _captured = new();

        public FrameRecordingAudioSource(IAudioSource inner) => _inner = inner;

        /// <summary>The frames captured so far, in the order they were yielded.</summary>
        public IReadOnlyList<Frame> CapturedFrames => _captured;

        public IEnumerable<Frame> Frames
        {
            get
            {
                foreach (Frame frame in _inner.Frames)
                {
                    _captured.Add(frame);
                    yield return frame;
                }
            }
        }

        /// <summary>An <see cref="IAudioSource"/> that replays the frames captured so far.</summary>
        public IAudioSource ToBufferedSource() => new BufferedAudioSource(_captured);
    }

    /// <summary>Replays a fixed, already-captured frame list (the batch pass reads it once).</summary>
    private sealed class BufferedAudioSource : IAudioSource
    {
        private readonly IReadOnlyList<Frame> _frames;

        public BufferedAudioSource(IReadOnlyList<Frame> frames) => _frames = frames;

        public IEnumerable<Frame> Frames => _frames;
    }
}
