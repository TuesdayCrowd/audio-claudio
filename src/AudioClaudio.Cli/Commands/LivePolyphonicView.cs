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
/// A minimal END-TO-END PROTOTYPE of near-real-time polyphonic live capture (`listen --view --poly`):
/// while the mic runs, a background loop re-transcribes the WHOLE captured buffer every ~1.64 s with
/// the existing batch <see cref="BasicPitchTranscriber"/> and publishes the resulting grand-staff
/// MusicXML to the live view. This is a prototype to judge feel/latency, not a production design --
/// re-transcribing the whole growing buffer on every tick does NOT scale (inference is roughly
/// 9 ms per ~2 s model window, so cost grows with session length; fine for a short demo, wrong for a
/// long one). Production polyphonic live capture needs genuinely incremental inference; that is
/// explicitly NOT attempted here (see CLAUDE.md's live-capture precedent for the monophonic path,
/// Step 10 -- this class is the polyphonic analogue of that step's "adapter only" scope, except the
/// whole-buffer re-transcribe below is the one deliberately non-scaling shortcut).
///
/// Threading: the CALLING thread drains <c>source.Frames</c> (a blocking pull -- see
/// <see cref="FrameAccumulator"/>'s doc and <c>CaptureFrameStream</c>) into a <see cref="FrameAccumulator"/>;
/// a separate background <see cref="Task"/> wakes every <see cref="TranscribeInterval"/>, takes a
/// point-in-time <see cref="FrameAccumulator.Snapshot"/> (never holding the accumulator's lock during
/// inference), reconstructs mono, wraps it in a throwaway in-memory <see cref="IAudioSource"/>, and
/// calls the (once-constructed, reused) transcriber. When the mic stops -- Ctrl+C or the Stop button,
/// whichever the caller wires up -- the drain's <c>foreach</c> ends, the background loop is cancelled,
/// and one FINAL transcribe writes <c>score.mid</c>/<c>score.musicxml</c> to the out-dir.
///
/// <see cref="Run"/> performs ONE take (drain → periodic transcribe → final save) and clears its
/// accumulator at the start, so the caller can reuse a single instance (the ONNX model loads once)
/// across successive takes. <c>ListenAppCommand</c>'s <c>--poly</c> loop drives the browser Start/Stop
/// buttons around it -- idle until Start, capture until Stop (or Ctrl+C), save, repeat -- mirroring the
/// mono <c>--view</c> loop. Per-take WAV recording/archiving (the mono path's --record/skip-silence/
/// archive machinery) is intentionally out of scope for this prototype.
/// </summary>
public sealed class LivePolyphonicView : IDisposable
{
    // Same 1024/256 mic framing as the rest of `listen` (ListenAppCommand's private constants).
    private const int FrameSize = 1024;
    private const int Hop = 256;

    // ~1.64 s: the task's stated re-transcribe cadence for this prototype.
    private static readonly TimeSpan TranscribeInterval = TimeSpan.FromMilliseconds(1640);

    private readonly LiveNotationServer _server;
    private readonly string _outDir;
    private readonly Tempo _tempo;
    private readonly Action<string> _print;
    private readonly BasicPitchTranscriber _transcriber;
    private readonly FrameAccumulator _accumulator = new();

    public LivePolyphonicView(LiveNotationServer server, string outDir, double tempoBpm, Action<string> print)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _outDir = outDir ?? throw new ArgumentNullException(nameof(outDir));
        _tempo = new Tempo(tempoBpm);
        _print = print ?? throw new ArgumentNullException(nameof(print));
        // Constructed ONCE and reused for every tick's inference (never re-loaded per tick).
        _transcriber = new BasicPitchTranscriber(ModelLocator.Resolve(null));
    }

    /// <summary>
    /// Drains <paramref name="source"/> on the CALLING thread until its frames end (the mic stopped),
    /// running the periodic background re-transcribe loop alongside, then performs one final
    /// transcribe and writes <c>score.mid</c>/<c>score.musicxml</c>. Returns once everything is
    /// written. <paramref name="ct"/> is observed by the background loop only (for prompt shutdown on
    /// Ctrl+C) -- the loop is ALSO unconditionally cancelled once draining ends, regardless of
    /// <paramref name="ct"/>'s state, so it can never keep running after this method returns.
    /// </summary>
    public void Run(IAudioSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Reset so one LivePolyphonicView (model loaded once) can serve successive Start/Stop takes.
        _accumulator.Clear();
        _print("Recording (polyphonic prototype, re-transcribing ~every 1.6s). Press Stop in the browser (or Ctrl+C) to save.");

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task backgroundLoop = Task.Run(() => TranscribeLoopAsync(loopCts.Token));

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
            loopCts.Cancel();
            try
            {
                backgroundLoop.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // expected shutdown path
            }
        }

        FinalTranscribeAndWrite();
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
            _server.PublishScoreXml(xml);
            _print($"live poly: republished ({raw.Count} notes, {grandStaff.Measures.Count} bars, {snapshot.Count} frames buffered).");
        }
        catch (Exception ex)
        {
            // A tick failing (e.g. a transient ONNX hiccup on a very short/silent buffer) must not
            // kill the whole prototype loop -- it just tries again next tick, same defensive idiom
            // as ListenCommand's live-view hooks (SafeInvokeHook).
            _print($"live poly: transcribe tick failed ({ex.Message}); continuing.");
        }
    }

    private void FinalTranscribeAndWrite()
    {
        IReadOnlyList<Frame> snapshot = _accumulator.Snapshot();
        GrandStaffScore? grandStaff;
        try
        {
            grandStaff = BuildGrandStaff(snapshot, out _);
        }
        catch (Exception ex)
        {
            _print($"live poly: final transcribe failed ({ex.Message}); nothing written.");
            return;
        }

        if (grandStaff is null)
        {
            _print("live poly: no audio captured; nothing written.");
            return;
        }

        string xml = new GrandStaffMusicXmlWriter().WriteToString(grandStaff);
        _server.PublishScoreXml(xml);

        IReadOnlyList<NoteEvent> quantized = GrandStaffFlattener.ToNoteEvents(grandStaff, MakeGrid());
        string scorePath = Path.Combine(_outDir, "score.mid");
        string xmlPath = Path.Combine(_outDir, "score.musicxml");
        var writer = new DryWetMidiWriter();
        using (var scoreFile = File.Create(scorePath))
        {
            writer.Write(quantized, _tempo, scoreFile);
        }

        File.WriteAllText(xmlPath, xml);
        _print($"Wrote {scorePath} and {xmlPath} ({quantized.Count} quantized notes, {grandStaff.Measures.Count} bars).");
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
