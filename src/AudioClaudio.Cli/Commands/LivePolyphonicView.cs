using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AudioClaudio.Application;
using AudioClaudio.Application.Ports;
using AudioClaudio.Cli.Composition;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Polyphony;
using AudioClaudio.Infrastructure.LiveView;
using AudioClaudio.Infrastructure.Midi;
using AudioClaudio.Infrastructure.MusicXml;
using AudioClaudio.Infrastructure.Transcription;

namespace AudioClaudio.Cli.Commands;

/// <summary>
/// One take's outcome from <see cref="LivePolyphonicView.Run"/>: <paramref name="RawEvents"/> is the
/// honest, un-quantized polyphonic note list from the take's FINAL transcribe -- at the poly engine's
/// OWN sample rate (<see cref="AudioClaudio.Infrastructure.Transcription.BasicPitchModel.SampleRateHz"/>,
/// not the mic's, since <c>BasicPitchTranscriber</c> resamples internally) -- and
/// <paramref name="CapturedFrames"/> is the raw mic audio (at the mic's declared rate). Together they
/// are everything <c>ListenAppCommand</c> needs to mirror the monophonic path's
/// --record/--skip-silence/archive machinery for the polyphonic engine.
/// </summary>
public sealed record LivePolyphonicResult(IReadOnlyList<NoteEvent> RawEvents, IReadOnlyList<Frame> CapturedFrames);

/// <summary>
/// The polyphonic live-capture engine behind `listen` (the DEFAULT engine; `--mono` opts into the
/// separate monophonic path instead): while the mic runs, a background loop re-transcribes the WHOLE
/// captured buffer every ~1.64 s with the existing batch <see cref="BasicPitchTranscriber"/> and
/// publishes the resulting grand-staff MusicXML to the live view -- but ONLY when a
/// <see cref="LiveNotationServer"/> is actually attached (<c>listen --view</c>). Given a null server
/// (headless `listen`, no <c>--view</c>), <see cref="Run"/> skips the periodic loop entirely -- no
/// background task, no wasted inference -- and just drains the mic to one final transcribe on stop.
///
/// The periodic-republish design does NOT scale (inference is roughly 9 ms per ~2 s model window, so
/// cost grows with session length; fine for a short take, wrong for a long one). Production polyphonic
/// live capture needs genuinely incremental inference; that is explicitly NOT attempted here (see
/// CLAUDE.md's live-capture precedent for the monophonic path, Step 10 -- this class is the polyphonic
/// analogue of that step's "adapter only" scope, except the whole-buffer re-transcribe below is the one
/// deliberately non-scaling shortcut, kept because it is good enough for a live demo take).
///
/// Threading: the CALLING thread drains <c>source.Frames</c> (a blocking pull -- see
/// <see cref="FrameAccumulator"/>'s doc and <c>CaptureFrameStream</c>) into a <see cref="FrameAccumulator"/>;
/// when a server is attached, a separate background <see cref="Task"/> wakes every
/// <see cref="TranscribeInterval"/>, takes a point-in-time <see cref="FrameAccumulator.Snapshot"/>
/// (never holding the accumulator's lock during inference), reconstructs mono, wraps it in a throwaway
/// in-memory <see cref="IAudioSource"/>, and calls the (once-constructed, reused) transcriber. When the
/// mic stops -- Ctrl+C or the Stop button, whichever the caller wires up -- the drain's <c>foreach</c>
/// ends, the background loop (if any) is cancelled, and one FINAL transcribe writes
/// <c>score.mid</c>/<c>score.musicxml</c> to the out-dir.
///
/// <see cref="Run"/> performs ONE take (drain → periodic transcribe → final save) and clears its
/// accumulator at the start, so the caller can reuse a single instance (the ONNX model loads once)
/// across successive takes. <c>ListenAppCommand</c>'s <c>--view</c> loop drives the browser Start/Stop
/// buttons around it -- idle until Start, capture until Stop (or Ctrl+C), save, repeat -- mirroring the
/// mono <c>--view</c> loop; the headless (no <c>--view</c>) path just runs one take from launch to
/// Ctrl+C with a null server. <see cref="Run"/> RETURNS the take's raw poly events and captured frames
/// (see <see cref="LivePolyphonicResult"/>) precisely so the caller can layer the mono path's
/// --record/--skip-silence/archive machinery on top -- this class itself stays scoped to
/// capture + transcribe + raw.mid/score.mid/score.musicxml.
/// </summary>
public sealed class LivePolyphonicView : IDisposable
{
    // Same 1024/256 mic framing as the rest of `listen` (ListenAppCommand's private constants).
    private const int FrameSize = 1024;
    private const int Hop = 256;

    // ~1.64 s: the stated re-transcribe cadence when a live view is attached.
    private static readonly TimeSpan TranscribeInterval = TimeSpan.FromMilliseconds(1640);

    private readonly LiveNotationServer? _server;
    private readonly string _outDir;
    private readonly Tempo _tempo;
    private readonly Action<string> _print;
    private readonly BasicPitchTranscriber _transcriber;
    private readonly FrameAccumulator _accumulator = new();

    /// <param name="server">
    /// The live-notation server to periodically republish to, or <c>null</c> for a headless take
    /// (no browser view) -- when null, <see cref="Run"/> never starts the periodic re-transcribe loop.
    /// </param>
    public LivePolyphonicView(LiveNotationServer? server, string outDir, double tempoBpm, Action<string> print)
    {
        _server = server;
        _outDir = outDir ?? throw new ArgumentNullException(nameof(outDir));
        _tempo = new Tempo(tempoBpm);
        _print = print ?? throw new ArgumentNullException(nameof(print));
        // Constructed ONCE and reused for every tick's inference (never re-loaded per tick).
        _transcriber = new BasicPitchTranscriber(ModelLocator.Resolve(null));
    }

    /// <summary>
    /// Drains <paramref name="source"/> on the CALLING thread until its frames end (the mic stopped),
    /// running the periodic background re-transcribe loop alongside IF a server is attached, then
    /// performs one final transcribe and writes <c>score.mid</c>/<c>score.musicxml</c>. Returns once
    /// everything is written. <paramref name="ct"/> is observed by the background loop only (for
    /// prompt shutdown on Ctrl+C) -- the loop is ALSO unconditionally cancelled once draining ends,
    /// regardless of <paramref name="ct"/>'s state, so it can never keep running after this method
    /// returns. Returns the take's raw poly events and captured frames (<see cref="LivePolyphonicResult"/>).
    /// </summary>
    public LivePolyphonicResult Run(IAudioSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Reset so one LivePolyphonicView (model loaded once) can serve successive Start/Stop takes.
        _accumulator.Clear();
        _print(_server is not null
            ? "Recording (polyphonic, re-transcribing ~every 1.6s). Press Stop in the browser (or Ctrl+C) to save."
            : "Recording (polyphonic). Press Ctrl+C to stop and save.");

        // No server -> no periodic loop at all: nothing to publish to, so no point paying for the
        // repeated whole-buffer inference (see the class doc's "no wasted inference" guarantee).
        CancellationTokenSource? loopCts = _server is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        Task? backgroundLoop = loopCts is not null
            ? Task.Run(() => TranscribeLoopAsync(loopCts.Token))
            : null;

        try
        {
            foreach (Frame frame in source.Frames)
            {
                _accumulator.Add(frame);
            }
        }
        finally
        {
            // Stop the periodic loop the instant frames stop, however that happened -- never rely
            // solely on the caller's `ct` (it may not be the thing that ended the drain).
            if (loopCts is not null)
            {
                loopCts.Cancel();
                try
                {
                    backgroundLoop!.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    // expected shutdown path
                }
                finally
                {
                    loopCts.Dispose();
                }
            }
        }

        return FinalTranscribeAndWrite();
    }

    private async Task TranscribeLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TranscribeInterval, ct).ConfigureAwait(false);
                TranscribeAndPublish();
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown -- the mic stopped or Ctrl+C fired.
        }
    }

    private void TranscribeAndPublish()
    {
        IReadOnlyList<Frame> snapshot = _accumulator.Snapshot();
        try
        {
            GrandStaffScore? grandStaff = BuildGrandStaff(snapshot, out IReadOnlyList<NoteEvent> raw);
            if (grandStaff is null)
            {
                return;
            }

            string xml = new GrandStaffMusicXmlWriter().WriteToString(grandStaff);
            _server?.PublishScoreXml(xml);
            _print($"live poly: republished ({raw.Count} notes, {grandStaff.Measures.Count} bars, {snapshot.Count} frames buffered).");
        }
        catch (Exception ex)
        {
            // A tick failing (e.g. a transient ONNX hiccup on a very short/silent buffer) must not
            // kill the whole loop -- it just tries again next tick, same defensive idiom
            // as ListenCommand's live-view hooks (SafeInvokeHook).
            _print($"live poly: transcribe tick failed ({ex.Message}); continuing.");
        }
    }

    private LivePolyphonicResult FinalTranscribeAndWrite()
    {
        IReadOnlyList<Frame> snapshot = _accumulator.Snapshot();
        GrandStaffScore? grandStaff;
        IReadOnlyList<NoteEvent> rawEvents;
        try
        {
            grandStaff = BuildGrandStaff(snapshot, out rawEvents);
        }
        catch (Exception ex)
        {
            _print($"live poly: final transcribe failed ({ex.Message}); nothing written.");
            return new LivePolyphonicResult(Array.Empty<NoteEvent>(), snapshot);
        }

        if (grandStaff is null)
        {
            _print("live poly: no audio captured; nothing written.");
            return new LivePolyphonicResult(rawEvents, snapshot);
        }

        string xml = new GrandStaffMusicXmlWriter().WriteToString(grandStaff);
        _server?.PublishScoreXml(xml);

        IReadOnlyList<NoteEvent> quantized = GrandStaffFlattener.ToNoteEvents(grandStaff, MakeGrid());
        string rawPath = Path.Combine(_outDir, "raw.mid");
        string scorePath = Path.Combine(_outDir, "score.mid");
        string xmlPath = Path.Combine(_outDir, "score.musicxml");
        var writer = new DryWetMidiWriter();
        using (var rawFile = File.Create(rawPath))
        {
            writer.Write(rawEvents, _tempo, rawFile);
        }

        using (var scoreFile = File.Create(scorePath))
        {
            writer.Write(quantized, _tempo, scoreFile);
        }

        File.WriteAllText(xmlPath, xml);
        _print($"Wrote {rawPath}, {scorePath}, and {xmlPath} ({quantized.Count} quantized notes, {grandStaff.Measures.Count} bars).");

        return new LivePolyphonicResult(rawEvents, snapshot);
    }

    // Reconstructs mono from the snapshotted frames, wraps it in a throwaway in-memory IAudioSource
    // (mirroring how the WAV adapter frames a mono buffer), runs the batch transcriber, and quantizes
    // into a grand-staff score. Null when the snapshot is empty (nothing captured yet).
    private GrandStaffScore? BuildGrandStaff(IReadOnlyList<Frame> frames, out IReadOnlyList<NoteEvent> rawEvents)
    {
        rawEvents = Array.Empty<NoteEvent>();
        if (frames.Count == 0)
        {
            return null;
        }

        float[] mono = Framing.ReconstructMono(frames);
        SampleRate rate = frames[0].Rate;
        var inMemorySource = new InMemoryFrameAudioSource(mono, rate, new FrameParameters(FrameSize, Hop));
        TranscriptionResult result = _transcriber.Transcribe(inMemorySource);
        rawEvents = result.RawEvents;

        var polyRate = new SampleRate(BasicPitchModel.SampleRateHz);
        var chordWindow = new SampleDuration(polyRate.Hz / 20, polyRate); // ~50 ms: matches AppBuilder's recipe
        return PolyphonicQuantizer.Quantize(result.RawEvents, MakeGrid(), chordWindow);
    }

    private QuantizationGrid MakeGrid() =>
        new(new SampleRate(BasicPitchModel.SampleRateHz), _tempo, TimeSignature.FourFour, Subdivision.Sixteenth);

    /// <summary>
    /// Converts a note list from its own declared <see cref="SampleRate"/> into <paramref name="targetRate"/>
    /// by scaling onset/duration sample counts by the exact rate ratio -- e.g. 2x between the mic's 44100 Hz
    /// and this engine's internal <see cref="BasicPitchModel.SampleRateHz"/> (22050 Hz). Pure; never coerces
    /// across rates silently (the Domain's mixed-sample-rate non-negotiable, CLAUDE.md §4) -- this performs
    /// the explicit, exact conversion that <see cref="BasicPitchTranscriber"/>'s internal resampling makes
    /// necessary before <see cref="LivePolyphonicResult.RawEvents"/> can be handed to rate-sensitive helpers
    /// (e.g. <c>SilenceCollapser</c>, <c>ISynthesizer.Render</c>) that require notes and audio to share ONE
    /// declared rate. A no-op (returns <paramref name="notes"/> unchanged) when the list is empty.
    /// </summary>
    public static IReadOnlyList<NoteEvent> RescaleNotes(IReadOnlyList<NoteEvent> notes, SampleRate targetRate)
    {
        ArgumentNullException.ThrowIfNull(notes);
        if (notes.Count == 0)
        {
            return notes;
        }

        var rescaled = new List<NoteEvent>(notes.Count);
        foreach (NoteEvent n in notes)
        {
            double ratio = (double)targetRate.Hz / n.Onset.Rate.Hz;
            long onset = (long)Math.Round(n.Onset.Samples * ratio);
            long duration = Math.Max(1, (long)Math.Round(n.Duration.Samples * ratio));
            rescaled.Add(new NoteEvent(
                n.Pitch, new SamplePosition(onset, targetRate), new SampleDuration(duration, targetRate), n.Velocity));
        }

        return rescaled;
    }

    public void Dispose() => _transcriber.Dispose();

    // Trivial in-memory IAudioSource: frames a mono buffer via the Domain splitter. A fresh instance
    // is thrown away after each tick's transcribe -- it exists only to hand BasicPitchTranscriber a
    // clean IAudioSource without touching the live mic's own single-reader frame channel.
    private sealed class InMemoryFrameAudioSource : IAudioSource
    {
        private readonly IReadOnlyList<Frame> _frames;

        public IEnumerable<Frame> Frames => _frames;

        public InMemoryFrameAudioSource(float[] samples, SampleRate rate, FrameParameters parameters)
        {
            _frames = Framing.Split(samples, rate, parameters);
        }
    }
}
