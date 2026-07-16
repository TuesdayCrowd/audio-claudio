using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AudioClaudio.Application;
using AudioClaudio.Application.Ports;
using AudioClaudio.Application.UseCases;
using AudioClaudio.Cli.Composition;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Polyphony;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Infrastructure.LiveView;
using AudioClaudio.Infrastructure.Midi;
using AudioClaudio.Infrastructure.MusicXml;
using AudioClaudio.Infrastructure.Separation;
using AudioClaudio.Infrastructure.Transcription;

namespace AudioClaudio.Cli.Commands;

/// <summary>
/// One take's outcome from <see cref="LivePolyphonicView.Run"/>: <paramref name="RawEvents"/> is the
/// honest, un-quantized polyphonic note list from the take's FINAL transcribe -- at the poly engine's
/// OWN sample rate (<see cref="AudioClaudio.Infrastructure.Transcription.BasicPitchModel.SampleRateHz"/>,
/// not the mic's, since <c>BasicPitchTranscriber</c> resamples internally) -- and
/// <paramref name="CapturedFrames"/> is the raw mic audio (at the mic's declared rate). Together they
/// are everything <c>ListenAppCommand</c> needs to mirror the monophonic path's
/// --record/archive machinery for the polyphonic engine.
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
/// --record/archive machinery on top -- this class itself stays scoped to
/// capture + transcribe + raw.mid/score.mid/score.musicxml.
///
/// <para><b><c>listen --separate</c> (DECISIONS.md "Multi-instrument -&gt; piano" / "Live-separated
/// `listen --separate` prototype"):</b> when constructed with <c>separate: true</c>, this class
/// additionally routes every buffer through <see cref="SpleeterSourceSeparator"/> + per-stem
/// transcription (<see cref="MultiStemRouting"/>/<see cref="MultiStemTranscriber"/>) before building
/// a grand staff -- the SAME "all notes on piano" pipeline as <c>claudio pianize</c>, not the plain
/// single-engine Basic Pitch path above. This is STRICTLY heavier per tick (5 U-Nets + up to 4
/// transcribers vs. 1), and <c>SeparationLiveSpike</c> measured that re-processing the WHOLE captured
/// buffer every tick COSTS MORE AND DEGRADES as a take grows (4.6 s/8.3 s/13.8 s total at 5 s/15 s/30 s
/// of buffered audio -- all already over the 1.64 s tick budget, and getting worse, not staying flat).
/// So the separated TICK (<see cref="TranscribeAndPublish"/> via <see cref="BuildSeparatedGrandStaff"/>)
/// re-processes only the LAST <see cref="SeparatedWindowSeconds"/> of the buffer each time -- bounded,
/// not degrading, but still a laggy multi-second-refresh PROTOTYPE, never real-time (see
/// <see cref="SeparatedWindowSeconds"/>'s doc for the exact measurement behind the window choice). The
/// separated FINAL save (<see cref="FinalPianizeAndWrite"/>), by contrast, is full quality: on Stop it
/// runs the WHOLE take through <see cref="PianizeCommand.PianizeSource"/> (the same batch pipeline
/// `claudio pianize` uses), writing <c>multitrack.mid</c> + the stem WAVs + <c>score.mid</c>/
/// <c>score.musicxml</c> + <c>recreation.wav</c> -- deliberately NOT <c>raw.mid</c> (there is no single
/// "raw" engine in separated mode; <c>multitrack.mid</c> IS the faithful per-instrument raw view,
/// exactly as for <c>pianize</c>). The non-separated path above is completely untouched by any of
/// this -- <c>separate: false</c> (the default) reproduces the exact prior behavior byte-for-byte.
///
/// Manual-acceptance note (mirrors the non-separated path): only the real microphone device is
/// untestable in CI. Both the bounded-window tick build and the full <see cref="Run"/> drain-to-final
/// path are ordinary methods over an <see cref="IAudioSource"/> and are exercised in automated tests
/// against a committed fixture WAV (never a live device) -- see
/// <c>tests/AudioClaudio.Tests/Cli/LivePolyphonicViewSeparatedTests.cs</c>.</para>
/// </summary>
public sealed class LivePolyphonicView : IDisposable
{
    // Same 1024/256 mic framing as the rest of `listen` (ListenAppCommand's private constants).
    private const int FrameSize = 1024;
    private const int Hop = 256;

    // ~1.64 s: the stated re-transcribe cadence when a live view is attached.
    private static readonly TimeSpan TranscribeInterval = TimeSpan.FromMilliseconds(1640);

    // The separated tick's bounded window (see the class doc): only the LAST this-many seconds of
    // the captured buffer are re-separated + re-transcribed each tick, so per-tick cost stays roughly
    // CONSTANT as a take grows instead of degrading like the whole-buffer approach SeparationLiveSpike
    // measured. Chosen from a direct measurement on the dev machine (Apple M3 Max) of candidate
    // windows through the real separate+multi-stem-transcribe pipeline (golden Spleeter fixture,
    // tiled to length): 6 s ~= 4.8 s/tick, 7 s ~= 5.0 s/tick, 8 s ~= 6.3 s/tick. 7 s lands
    // comfortably inside SeparationLiveSpike's own "4-6 s per tick" ballpark (its 5 s point measured
    // 4.6 s) with a little headroom before 8 s's jump past 6 s. Combined with the ~1.64 s
    // TranscribeInterval wait between ticks, the resulting steady-state refresh is roughly
    // 1.64 + 5.0 ~= 6.6 s -- a laggy prototype refresh, by design (see class doc), never real-time.
    private const int SeparatedWindowSeconds = 7;

    // The common rate every separated stem's transcription is reconciled to (also the mic's own
    // declared rate, so the bounded-window slice below needs no resample) -- mirrors
    // PianizeCommand.OutputRate.
    private static readonly SampleRate SeparatedRate = new(44100);

    private readonly LiveNotationServer? _server;
    private readonly string _outDir;
    private readonly Tempo _tempo;
    private readonly Action<string> _print;
    private readonly bool _separate;
    private readonly bool _includeVocals;
    private readonly string? _soundfontPath;

    // Non-separated path (the default): one BasicPitchTranscriber, constructed once, reused per tick.
    private readonly BasicPitchTranscriber? _transcriber;

    // Separated path (`separate: true`): the separator + stem routing table are constructed ONCE per
    // session and reused across every live-view TICK (BuildSeparatedGrandStaff) -- never re-loaded per
    // tick. The Stop-triggered final save (FinalPianizeAndWrite) does NOT reuse these: it runs the whole
    // take through the batch PianizeCommand.PianizeSource pipeline, which constructs (and disposes) its
    // own separator + transcribers. Reusing these already-warm instances on Stop is a possible latency
    // optimization, deliberately deferred (the final save is already inherently multi-second).
    private readonly SpleeterSourceSeparator? _separator;
    private readonly MultiStemTranscriber? _multiStemTranscriber;
    private readonly IDisposable? _stemTranscribers; // disposes the routing table's transcribers

    private readonly FrameAccumulator _accumulator = new();

    // Set fresh at the top of each take's Run() -- the per-take --note-names choice (mirrors the
    // mono path's per-take RecordOptions.NoteNames) -- and read by both GrandStaffMusicXmlWriter
    // sites below (the periodic live-publish tick and the final write), so a take's live view and
    // its saved score.musicxml always agree on whether note-name lyrics are on.
    private bool _noteNames;

    // Set fresh at the top of each take's Run() -- the per-take Title (the browser's Title field,
    // mirroring the mono path's RecordOptions.Title). Read by both GrandStaffMusicXmlWriter sites as
    // the score's work-title; null for a headless take (no browser Title input).
    private string? _title;

    // Set fresh at the top of each take's Run() -- the per-take time signature (the browser's Time
    // selector, mirroring the mono path's RecordOptions.TimeSignature; headless takes get the
    // top-level `--time-signature` flag instead). Read by MakeGrid(), which is the ONE grid
    // construction site this class uses for both the periodic live-publish tick and the final
    // quantized write, so a take's live view and its saved score.mid/score.musicxml always agree.
    private TimeSignature _timeSignature = TimeSignature.FourFour;

    /// <param name="server">
    /// The live-notation server to periodically republish to, or <c>null</c> for a headless take
    /// (no browser view) -- when null, <see cref="Run"/> never starts the periodic re-transcribe loop.
    /// </param>
    /// <param name="separate">When true, routes every buffer through source separation + per-stem
    /// transcription (the `pianize` pipeline) instead of the single BasicPitchTranscriber path -- see
    /// the class doc. Default false reproduces the prior (pre-`--separate`) behavior exactly.</param>
    /// <param name="includeVocals">With <paramref name="separate"/>, folds the vocal stem into the
    /// merged piano score/recreation (it is always transcribed into the final multitrack.mid
    /// regardless -- mirrors <c>pianize --include-vocals</c>). Ignored when <paramref name="separate"/>
    /// is false.</param>
    /// <param name="soundfontPath">With <paramref name="separate"/>, the explicit SoundFont for the
    /// final save's <c>recreation.wav</c> (auto-discovered when null) -- threaded to
    /// <see cref="PianizeCommand.PianizeSource"/>. Ignored when <paramref name="separate"/> is false
    /// (the non-separated final save's own --record recreation path resolves its SoundFont
    /// separately, in <c>ListenAppCommand</c>).</param>
    public LivePolyphonicView(
        LiveNotationServer? server, string outDir, double tempoBpm, Action<string> print,
        bool separate = false, bool includeVocals = false, string? soundfontPath = null)
    {
        _server = server;
        _outDir = outDir ?? throw new ArgumentNullException(nameof(outDir));
        _tempo = new Tempo(tempoBpm);
        _print = print ?? throw new ArgumentNullException(nameof(print));
        _separate = separate;
        _includeVocals = includeVocals;
        _soundfontPath = soundfontPath;

        // Constructed ONCE and reused for every tick's inference (never re-loaded per tick) --
        // whichever path this session actually uses; the other stays entirely uninitialized (null),
        // so a non-separated session never pays Spleeter's model-load cost, and vice versa.
        if (_separate)
        {
            _separator = new SpleeterSourceSeparator(SeparatorModelLocator.Resolve(null), new Radix2Fft());
            (IReadOnlyList<StemRoute> routing, IDisposable transcribers) = MultiStemRouting.Build();
            _stemTranscribers = transcribers;
            _multiStemTranscriber = new MultiStemTranscriber(routing, SeparatedRate);
        }
        else
        {
            _transcriber = new BasicPitchTranscriber(ModelLocator.Resolve(null));
        }
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
    /// <param name="noteNames">This take's --note-names choice: when true, both the periodic live
    /// republish and the final score.musicxml carry a scientific-pitch-name lyric under each note
    /// (mirrors the mono path's per-take <c>RecordOptions.NoteNames</c>).</param>
    /// <param name="timeSignature">This take's time signature (mirrors the mono path's per-take
    /// <c>RecordOptions.TimeSignature</c>). Null (the default) falls back to <see
    /// cref="TimeSignature.FourFour"/> -- callers should never pass <c>default(TimeSignature)</c>
    /// (0/0), which is not a valid signature.</param>
    public LivePolyphonicResult Run(
        IAudioSource source, CancellationToken ct, bool noteNames = false, string? title = null,
        TimeSignature? timeSignature = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Reset so one LivePolyphonicView (model loaded once) can serve successive Start/Stop takes.
        _accumulator.Clear();
        _noteNames = noteNames;
        _title = title;
        _timeSignature = timeSignature ?? TimeSignature.FourFour;
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
            IReadOnlyList<NoteEvent> raw;
            GrandStaffScore? grandStaff = _separate
                ? BuildSeparatedGrandStaff(snapshot, out raw)
                : BuildGrandStaff(snapshot, out raw);
            if (grandStaff is null)
            {
                return;
            }

            string xml = new GrandStaffMusicXmlWriter(_noteNames, _title).WriteToString(grandStaff);
            _server?.PublishScoreXml(xml);
            _print($"live poly{(_separate ? " (separate)" : "")}: republished ({raw.Count} notes, {grandStaff.Measures.Count} bars, {snapshot.Count} frames buffered).");
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

        // Separated mode's final save is a completely different (and full-quality) artifact set --
        // see FinalPianizeAndWrite's doc and the class doc's "capture-then-pianize on Stop" section.
        if (_separate)
        {
            return FinalPianizeAndWrite(snapshot);
        }

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

        string xml = new GrandStaffMusicXmlWriter(_noteNames, _title).WriteToString(grandStaff);
        _server?.PublishScoreXml(xml);

        IReadOnlyList<NoteEvent> quantized = GrandStaffFlattener.ToNoteEvents(grandStaff, MakeGrid(new SampleRate(BasicPitchModel.SampleRateHz)));
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

    /// <summary>
    /// Separated mode's final save (`listen --separate`, on Stop): "capture-then-pianize" -- the
    /// WHOLE captured take (not the tick's bounded window) is wrapped in a <see cref="PcmAudioSource"/>
    /// and run through the exact same batch pipeline as <c>claudio pianize &lt;file.wav&gt;</c> via
    /// <see cref="PianizeCommand.PianizeSource"/>: full-quality separation + per-stem transcription
    /// over the entire recording. Writes the 5 stem WAVs, <c>multitrack.mid</c>,
    /// <c>score.mid</c>/<c>score.musicxml</c>, and <c>recreation.wav</c> directly to <c>_outDir</c> --
    /// deliberately no <c>raw.mid</c> (see class doc). Declares this take's tempo (never estimates it,
    /// matching the rest of this class) and lets <see cref="PianizeCommand"/> auto-detect the key
    /// (matching plain `pianize`'s own default). NOTE: <see cref="PianizeCommand"/> always quantizes
    /// in 4/4 -- this take's own <c>_timeSignature</c> (e.g. a browser Time selector choice) is NOT
    /// honored here, a known, accepted limitation inherited from `pianize` itself (not fixed by this
    /// change; see DECISIONS.md).
    /// </summary>
    private LivePolyphonicResult FinalPianizeAndWrite(IReadOnlyList<Frame> snapshot)
    {
        if (snapshot.Count == 0)
        {
            _print("live poly (separate): no audio captured; nothing written.");
            return new LivePolyphonicResult(Array.Empty<NoteEvent>(), snapshot);
        }

        float[] mono = Framing.ReconstructMono(snapshot);
        SampleRate rate = snapshot[0].Rate;
        // Any frame size/hop works here -- SpleeterSourceSeparator reconstructs the whole buffer
        // internally regardless (mirrors PianizeCommand's own InputFrameParameters).
        var source = new PcmAudioSource(mono, rate, new FrameParameters(4096, 4096));

        PianizeCommand.Result result;
        try
        {
            result = PianizeCommand.PianizeSource(
                source,
                _outDir,
                separatorModelDir: null,
                tempoBpm: _tempo.BeatsPerMinute,
                keyFifths: null,
                includeVocals: _includeVocals,
                includeNoteNames: _noteNames,
                triplets: false,
                soundfontPath: _soundfontPath);
        }
        catch (Exception ex)
        {
            _print($"live poly (separate): final pianize failed ({ex.Message}); nothing written.");
            return new LivePolyphonicResult(Array.Empty<NoteEvent>(), snapshot);
        }

        // Publish exactly the file just written (rather than re-serializing the score a second time),
        // so the live view and the saved score.musicxml can never disagree.
        string xmlPath = Path.Combine(_outDir, "score.musicxml");
        if (File.Exists(xmlPath))
        {
            _server?.PublishScoreXml(File.ReadAllText(xmlPath));
        }

        _print($"Wrote multitrack.mid, score.mid, score.musicxml, and recreation.wav to {_outDir} " +
               $"({result.MergedNotes.Count} merged notes, {result.GrandStaff.Measures.Count} bars).");

        return new LivePolyphonicResult(result.MergedNotes, snapshot);
    }

    // Reconstructs mono from the snapshotted frames, wraps it in a throwaway in-memory IAudioSource
    // (mirroring how the WAV adapter frames a mono buffer), runs the batch transcriber, and quantizes
    // into a grand-staff score. Null when the snapshot is empty (nothing captured yet). Non-separated
    // path only (_transcriber is null when _separate).
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
        TranscriptionResult result = _transcriber!.Transcribe(inMemorySource);
        rawEvents = result.RawEvents;

        var polyRate = new SampleRate(BasicPitchModel.SampleRateHz);
        var chordWindow = new SampleDuration(polyRate.Hz / 20, polyRate); // ~50 ms: matches AppBuilder's recipe
        return PolyphonicQuantizer.Quantize(result.RawEvents, MakeGrid(polyRate), chordWindow);
    }

    /// <summary>
    /// The separated tick's bounded-window build (`listen --separate` only): reconstructs mono from
    /// the snapshotted frames, takes only the LAST <see cref="SeparatedWindowSeconds"/> of it (see the
    /// class doc for why -- unbounded whole-buffer re-processing measurably degrades as a take grows),
    /// separates that window into stems, transcribes every routed stem
    /// (<see cref="_multiStemTranscriber"/>), merges the included stems' notes (vocals excluded unless
    /// <see cref="_includeVocals"/> -- drums are never routed at all, see <see cref="MultiStemRouting"/>),
    /// and quantizes into a grand-staff score at <see cref="SeparatedRate"/>. Null when the snapshot is
    /// empty. <c>internal</c> (not <c>private</c>) so tests can exercise this bounded-window tick logic
    /// directly against a fixture buffer, rather than racing the real background timer loop (a
    /// file-backed <see cref="IAudioSource"/> drains far faster than real time, so the timer would
    /// rarely if ever fire within a test) -- see <c>AudioClaudio.Cli.csproj</c>'s
    /// <c>InternalsVisibleTo</c>.
    /// </summary>
    internal GrandStaffScore? BuildSeparatedGrandStaff(IReadOnlyList<Frame> frames, out IReadOnlyList<NoteEvent> mergedNotes)
    {
        mergedNotes = Array.Empty<NoteEvent>();
        if (frames.Count == 0)
        {
            return null;
        }

        float[] mono = Framing.ReconstructMono(frames);
        SampleRate rate = frames[0].Rate;

        int windowSamples = SeparatedWindowSeconds * rate.Hz;
        float[] windowed = mono.Length > windowSamples ? mono[^windowSamples..] : mono;

        // Any frame size/hop works here -- SpleeterSourceSeparator reconstructs the whole buffer
        // internally regardless (mirrors PianizeCommand's own InputFrameParameters).
        var windowSource = new PcmAudioSource(windowed, rate, new FrameParameters(4096, 4096));
        IReadOnlyList<SeparatedStem> stems = _separator!.Separate(windowSource);
        IReadOnlyList<StemTranscription> transcriptions = _multiStemTranscriber!.Transcribe(stems);

        mergedNotes = transcriptions
            .Where(t => _includeVocals || t.StemName != "vocals")
            .SelectMany(t => t.Notes)
            .ToList();

        var chordWindow = new SampleDuration(SeparatedRate.Hz / 20, SeparatedRate); // ~50 ms, matches PianizeCommand
        return PolyphonicQuantizer.Quantize(mergedNotes, MakeGrid(SeparatedRate), chordWindow);
    }

    private QuantizationGrid MakeGrid(SampleRate rate) =>
        new(rate, _tempo, _timeSignature, Subdivision.Sixteenth);

    public void Dispose()
    {
        _transcriber?.Dispose();
        _separator?.Dispose();
        _stemTranscribers?.Dispose();
    }

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
