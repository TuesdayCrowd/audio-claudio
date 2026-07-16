using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using AudioClaudio.Application;
using AudioClaudio.Application.UseCases;
using AudioClaudio.Cli.Cli;
using AudioClaudio.Cli.Composition;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Infrastructure.LiveView;
using AudioClaudio.Infrastructure.Midi;
using AudioClaudio.Infrastructure.MusicXml;
using AudioClaudio.Infrastructure.Synthesis;

namespace AudioClaudio.Cli.Commands;

/// <summary>
/// The `listen` kernel handler's body (v2 Stage 5 Task 21), extracted out of the
/// <c>AppBuilder.Build</c> registration lambda for readability. This is the composition root
/// for live capture (Section 7 / R10.4): the mic (<see cref="PortAudioAudioSource"/>), the
/// optional live-notation server (<see cref="LiveNotationServer"/>, <c>--view</c>), and the
/// Start/Stop recording loop are constructed here and only here.
///
/// POLYPHONIC (<see cref="LivePolyphonicView"/>) is the default engine, mirroring `transcribe`'s
/// poly-default/`--mono` pattern -- both with `--view` (browser Start/Stop loop) and headless
/// (single take, launch to Ctrl+C). `--mono` opts into the original monophonic path (faithfully
/// ported from the pre-Stage-5 <c>Program.cs</c> `case "listen"` block onto the
/// <see cref="ParsedArgs"/> / stdout/stderr handler signature -- behavior there is unchanged,
/// only the argument-reading mechanism is).
///
/// Opening a real microphone device cannot be exercised in CI (no audio device is available,
/// same as <see cref="PortAudioAudioSource.Start"/> itself) -- this class's device-touching
/// paths are manual-acceptance only (CLAUDE.md Step 10 precedent, formalized in the Stage 5
/// manual-acceptance checklist). Automated coverage stops at "the handler is registered with
/// its full option surface" (see <c>ListenHandlerRegistrationTests</c>).
/// </summary>
internal static class ListenAppCommand
{
    private const int SampleRateHz = 44100;
    private const int FrameSize = 1024;
    private const int Hop = 256;

    public static int Run(ParsedArgs p, TextWriter stdout, TextWriter stderr, StringBuilder logBuffer)
    {
        double? tempoArg = p.Double("tempo");
        bool estimateTempo = tempoArg is null;
        double tempoBpm = tempoArg ?? 120.0;
        string outDir = p.Path("out-dir") ?? "out";
        Directory.CreateDirectory(outDir);
        bool view = p.Flag("view");
        bool record = p.Flag("record");
        bool noteNames = p.Flag("note-names");

        // The session-level default (the CLI flag): headless takes always use it; a --view take
        // uses its own per-take browser value instead (RecordOptions.TimeSignature) -- see the
        // mono --view loop and RunPolyphonicView below. Invalid input is rejected the same way
        // --key is (AppBuilder's transcribe/notate handlers): one "error:" line to stderr, exit 1.
        if (!TimeSignature.TryParse(p.String("time-signature"), out TimeSignature timeSignature, out string? timeSignatureError))
        {
            stderr.WriteLine($"error: {timeSignatureError}");
            return 1;
        }

        // Polyphonic is the DEFAULT `listen` engine (mirrors `transcribe`'s poly-default/--mono
        // pattern); --mono opts into the proven monophonic path below, entirely unchanged -- nothing
        // here touches TranscriptionPipeline/LiveTranscriptionSession, and nothing below runs unless
        // --mono is set, so the mono path is completely unaffected by this branch's existence.
        if (!p.Flag("mono"))
        {
            bool separate = p.Flag("separate");
            bool includeVocals = p.Flag("include-vocals");
            return RunPolyphonic(
                outDir, tempoBpm, view, record, noteNames, timeSignature, p.Path("soundfont"),
                separate, includeVocals, stdout, stderr, logBuffer);
        }

        var rate = new SampleRate(SampleRateHz);

        // SoundFont/synth construction is LAZY (Step 9 precedent): a plain listen session that
        // never uses --record never touches a synthesizer, so it must run with no .sf2 present.
        var synthesizer = new Lazy<MeltySynthSynthesizer>(
            () => new MeltySynthSynthesizer(SoundFontLocator.Resolve(p.Path("soundfont"))));

        // TimeSignature = timeSignature: the session-level default (the CLI flag) so a headless take
        // (no --view, or the view failed to start) saves with the declared signature. A --view take
        // re-quantizes with its OWN per-take browser value instead (see the loop below), so this
        // default is a fallback for that path, not its final word.
        var settings = TranscriptionSettings.ForTempo(tempoBpm) with
        {
            FrameSize = FrameSize,
            Hop = Hop,
            EstimateTempo = estimateTempo,
            TimeSignature = timeSignature,
        };
        var pipeline = new TranscriptionPipeline(settings, new Radix2Fft()); // Domain FFT (Step 3 Option A)
        var midiWriter = new DryWetMidiWriter(); // implements INoteEventWriter + IScoreWriter
        var session = new LiveTranscriptionSession(pipeline.StreamNotes, pipeline.Transcribe);
        var musicXml = new MusicXmlScoreWriter(noteNames);

        // One recording's outputs: the out-dir root holds the LATEST files at stable paths; on stop,
        // write --record's WAVs then archive that whole set into <out-dir>/<timestamp>/ (timestamp =
        // when the recording started).
        void FinalizeRecording(LiveSessionResult result, string timestamp, bool doRecord)
        {
            if (doRecord)
            {
                float[] inputPcm = result.CapturedFrames.Count > 0
                    ? Framing.ReconstructMono(result.CapturedFrames)
                    : Array.Empty<float>();
                IReadOnlyList<NoteEvent> recreationNotes = result.Events;

                if (inputPcm.Length > 0)
                {
                    string inputPath = Path.Combine(outDir, "input.wav");
                    WavFileWriter.Write(inputPath, inputPcm, rate);
                    stdout.WriteLine($"Wrote {inputPath}.");
                }

                string recreationPath = Path.Combine(outDir, "recreation.wav");
                RenderCommand.RenderToWav(recreationNotes, synthesizer.Value, rate, recreationPath);
                stdout.WriteLine($"Wrote {recreationPath}.");
            }

            string archiveDir = SessionOutputArchive.Archive(outDir, timestamp);
            stdout.WriteLine($"Archived to {archiveDir}.");

            if (estimateTempo)
                stdout.WriteLine($"Estimated tempo: {result.Score.Tempo.BeatsPerMinute:F0} BPM.");

            // Written LAST so the log captures every line above, including the estimated tempo.
            string log = logBuffer.ToString();
            File.WriteAllText(Path.Combine(archiveDir, "log.txt"), log);
            File.WriteAllText(Path.Combine(outDir, "log.txt"), log);
        }

        LiveNotationServer? server = null;
        if (view)
        {
            server = new LiveNotationServer(Path.Combine(AppContext.BaseDirectory, "wwwroot"),
                                            scoreToMusicXml: musicXml.WriteToString, outDirPath: outDir);
            try
            {
                server.Start();
            }
            catch (Exception ex)
            {
                // The view is OPTIONAL. On a server-start failure, degrade to a plain single
                // recording (launch -> Ctrl+C) rather than abort.
                stderr.WriteLine($"Live notation view unavailable ({ex.Message}); continuing without it.");
                server.Dispose();
                server = null;
            }
        }

        try
        {
            if (server is not null)
            {
                stdout.WriteLine($"Live notation view: {server.BaseUrl}");
                stdout.WriteLine("Press Start in the browser to record, Stop to save. Ctrl+C exits (saving a recording in progress).");
                TryOpenBrowser(server.BaseUrl);

                var startSignal = new SemaphoreSlim(0);
                var gate = new object();
                PortAudioAudioSource? currentMic = null;
                bool exiting = false;
                RecordOptions pendingOptions = RecordOptions.Default;

                server.StartRequested = opts => { lock (gate) { pendingOptions = opts; } startSignal.Release(); };
                server.StopRequested = () => { lock (gate) { currentMic?.Stop(); } };
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    lock (gate) { exiting = true; currentMic?.Stop(); }
                    startSignal.Release(); // wake the loop if it is idle-waiting for Start
                };

                while (true)
                {
                    startSignal.Wait();
                    lock (gate) { if (exiting) break; }

                    RecordOptions opts;
                    lock (gate) { opts = pendingOptions; }

                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                    logBuffer.Clear();
                    server.PublishClear(); // blank the staff for the new recording
                    var cleared = SessionOutputArchive.CleanLatest(outDir);
                    if (cleared.Count > 0)
                        stdout.WriteLine($"Cleared {cleared.Count} previous output file(s) from {outDir}.");

                    // fresh accumulation per recording; estimateTempo makes the live preview track an
                    // evolving tempo estimate so it converges to the final score instead of jumping on stop.
                    // The grid is built fresh per take from THIS take's browser Time selector (opts.TimeSignature),
                    // not the session-level flag default -- mirroring how opts.NoteNames/opts.Title are per-take.
                    var perTakeGrid = new QuantizationGrid(rate, new Tempo(tempoBpm), opts.TimeSignature, Subdivision.Sixteenth);
                    var projector = new LiveScoreProjector(perTakeGrid, estimateTempo);
                    LiveNotationServer liveServer = server;
                    var recordingWriter = new MusicXmlScoreWriter(opts.NoteNames, opts.Title);
                    server.ScoreToMusicXml = recordingWriter.WriteToString;
                    var listenCmd = new ListenCommand(session, midiWriter, midiWriter, stdout.WriteLine,
                                                    musicXmlWriter: recordingWriter,
                                                    onLiveNote: n => liveServer.PublishScore(projector.Add(n)),
                                                    onFinalScore: s => liveServer.PublishScore(s));

                    var mic = new PortAudioAudioSource(SampleRateHz, FrameSize, Hop, channels: 1);
                    lock (gate) { currentMic = mic; }
                    var levelSource = new LevelTeeingAudioSource(mic,
                        rms => liveServer.PublishLevel(rms, mic.DeviceName ?? "Unknown microphone"));
                    stdout.WriteLine($"Recording {timestamp}...");
                    mic.Start();
                    // Runs until mic.Stop() ends the frames — triggered by the Stop button or Ctrl+C. The
                    // rate/TimeSignature overrides re-quantize the SAVED score onto this take's own
                    // time signature too (not just the live preview above), so the two never disagree.
                    var result = listenCmd.Run(levelSource, (int)Math.Round(tempoBpm), outDir, CancellationToken.None,
                        overrideSampleRate: rate, overrideTimeSignature: opts.TimeSignature);
                    lock (gate) { currentMic = null; }
                    mic.Dispose();

                    FinalizeRecording(result, timestamp, opts.Record);
                    server.PublishTakeReady(); // every take file is now written; safe for the browser to fetch
                    Thread.Sleep(TimeSpan.FromSeconds(1)); // let the final SSE push reach the browser

                    lock (gate) { if (exiting) break; }
                }
            }
            else
            {
                // Plain listen (no --view, or the view failed to start): one recording, launch to Ctrl+C.
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                var cleared = SessionOutputArchive.CleanLatest(outDir);
                if (cleared.Count > 0)
                    stdout.WriteLine($"Cleared {cleared.Count} previous output file(s) from {outDir}.");

                using var micSource = new PortAudioAudioSource(SampleRateHz, FrameSize, Hop, channels: 1);
                var listenCmd = new ListenCommand(session, midiWriter, midiWriter, stdout.WriteLine,
                                                musicXmlWriter: musicXml);
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; micSource.Stop(); cts.Cancel(); };
                logBuffer.Clear();
                micSource.Start();
                var result = listenCmd.Run(micSource, (int)Math.Round(tempoBpm), outDir, cts.Token);
                FinalizeRecording(result, timestamp, record);
            }
        }
        finally
        {
            server?.Dispose();
        }

        return 0;
    }

    // The polyphonic default's two shapes (see LivePolyphonicView's doc for the full design/threading
    // writeup and its non-scaling caveat): --view opens the browser Start/Stop loop; without --view
    // it's a single headless take from launch to Ctrl+C. Routes to whichever the caller asked for.
    private static int RunPolyphonic(
        string outDir, double tempoBpm, bool view, bool record, bool noteNames, TimeSignature timeSignature,
        string? soundfontPath, bool separate, bool includeVocals,
        TextWriter stdout, TextWriter stderr, StringBuilder logBuffer) =>
        view
            // --view ignores the top-level --time-signature flag in favor of each take's own browser
            // Time selector (RecordOptions.TimeSignature, defaulting to 4/4) -- exactly the existing
            // precedent for --note-names/--record, neither of which RunPolyphonicView receives either.
            ? RunPolyphonicView(outDir, tempoBpm, record, soundfontPath, separate, includeVocals, stdout, stderr, logBuffer)
            : RunPolyphonicHeadless(outDir, tempoBpm, record, noteNames, timeSignature, soundfontPath, separate, includeVocals, stdout, logBuffer);

    // SINGLE-TAKE browser loop: the mic starts recording the moment this runs, waits for the browser
    // Start button per take (mirroring the mono --view loop), captures until Stop (or Ctrl+C), saves
    // score.mid/score.musicxml, then idles for the next Start. Ctrl+C exits.
    private static int RunPolyphonicView(
        string outDir, double tempoBpm, bool record, string? soundfontPath, bool separate, bool includeVocals,
        TextWriter stdout, TextWriter stderr, StringBuilder logBuffer)
    {
        using var server = new LiveNotationServer(Path.Combine(AppContext.BaseDirectory, "wwwroot"), outDirPath: outDir);
        try
        {
            server.Start();
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"error: live notation view unavailable ({ex.Message}); aborting.");
            return 1;
        }

        stdout.WriteLine($"Live notation view: {server.BaseUrl}");
        stdout.WriteLine("Press Start in the browser to record, Stop to save. Ctrl+C exits (saving a take in progress).");
        TryOpenBrowser(server.BaseUrl);

        var rate = new SampleRate(SampleRateHz);
        // SoundFont/synth construction is LAZY (Step 9 precedent): a plain take that never uses
        // --record never touches a synthesizer, so it must run with no .sf2 present.
        var synthesizer = new Lazy<MeltySynthSynthesizer>(
            () => new MeltySynthSynthesizer(SoundFontLocator.Resolve(soundfontPath)));

        // Same browser Start/Stop loop shape as the mono --view path: stay idle until the Start button
        // fires (capturing that take's per-recording Record/Note-names options), capture until the
        // Stop button (or Ctrl+C) stops the mic, save, then wait for the next Start.
        var startSignal = new SemaphoreSlim(0);
        var gate = new object();
        PortAudioAudioSource? currentMic = null;
        bool exiting = false;
        RecordOptions pendingOptions = RecordOptions.Default;

        server.StartRequested = opts => { lock (gate) { pendingOptions = opts; } startSignal.Release(); };
        server.StopRequested = () => { lock (gate) { currentMic?.Stop(); } };
        using var exitCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            lock (gate) { exiting = true; currentMic?.Stop(); }
            exitCts.Cancel();
            startSignal.Release(); // wake the loop if it is idle-waiting for Start
        };

        // One view (the ONNX model loads once) reused across takes; Run() clears its accumulator each take.
        using var view = new LivePolyphonicView(server, outDir, tempoBpm, stdout.WriteLine, separate, includeVocals, soundfontPath);

        while (true)
        {
            startSignal.Wait();
            lock (gate) { if (exiting) break; }

            RecordOptions opts;
            lock (gate) { opts = pendingOptions; }

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            logBuffer.Clear();
            server.PublishClear(); // blank the staff for the new take
            var cleared = SessionOutputArchive.CleanLatest(outDir);
            if (cleared.Count > 0)
                stdout.WriteLine($"Cleared {cleared.Count} previous output file(s) from {outDir}.");

            var mic = new PortAudioAudioSource(SampleRateHz, FrameSize, Hop, channels: 1);
            lock (gate) { currentMic = mic; }
            var levelSource = new LevelTeeingAudioSource(mic,
                rms => server.PublishLevel(rms, mic.DeviceName ?? "Unknown microphone"));
            mic.Start();
            // Drains until the Stop button or Ctrl+C ends the mic frames, then writes
            // raw.mid/score.mid/score.musicxml and returns the take's raw events + captured frames.
            LivePolyphonicResult result = view.Run(levelSource, exitCts.Token, opts.NoteNames, opts.Title, opts.TimeSignature);
            lock (gate) { currentMic = null; }
            mic.Dispose();

            FinalizePolyphonicRecording(outDir, rate, synthesizer, stdout, logBuffer, result, timestamp, opts.Record, separate);
            server.PublishTakeReady(); // every take file is now written; safe for the browser to fetch
            Thread.Sleep(TimeSpan.FromSeconds(1)); // let the final SSE push reach the browser
            lock (gate) { if (exiting) break; }
        }

        return 0;
    }

    // Headless single take: no server, no browser, no periodic re-transcribe loop -- just drain the
    // mic from launch to Ctrl+C, then one final transcribe + raw.mid/score.mid/score.musicxml write,
    // finalized (--record/archive) exactly like the --view loop's per-take path, driven here by the
    // top-level flags instead of per-take browser options.
    // LivePolyphonicView skips its whole periodic-publish machinery when constructed with a null
    // server (see its Run() doc), so this path never pays for repeated whole-buffer inference.
    private static int RunPolyphonicHeadless(
        string outDir, double tempoBpm, bool record, bool noteNames, TimeSignature timeSignature,
        string? soundfontPath, bool separate, bool includeVocals, TextWriter stdout, StringBuilder logBuffer)
    {
        var rate = new SampleRate(SampleRateHz);
        var synthesizer = new Lazy<MeltySynthSynthesizer>(
            () => new MeltySynthSynthesizer(SoundFontLocator.Resolve(soundfontPath)));

        using var mic = new PortAudioAudioSource(SampleRateHz, FrameSize, Hop, channels: 1);
        using var view = new LivePolyphonicView(server: null, outDir, tempoBpm, stdout.WriteLine, separate, includeVocals, soundfontPath);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; mic.Stop(); cts.Cancel(); };

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        logBuffer.Clear();
        mic.Start();
        // Drains until Ctrl+C stops the mic, then writes raw.mid/score.mid/score.musicxml (or, with
        // --separate, multitrack.mid/score.mid/score.musicxml/recreation.wav + stem WAVs -- see
        // LivePolyphonicView's class doc).
        LivePolyphonicResult result = view.Run(mic, cts.Token, noteNames, title: null, timeSignature);

        FinalizePolyphonicRecording(outDir, rate, synthesizer, stdout, logBuffer, result, timestamp, record, separate);
        return 0;
    }

    // Mirrors the mono path's local FinalizeRecording: when --record is on, writes input.wav (the
    // captured mic audio) + recreation.wav (the take's poly notes re-synthesized); ALWAYS archives
    // the take's output files into <outDir>/<timestamp>/ + writes log.txt. The one wrinkle versus
    // mono: LivePolyphonicResult.RawEvents live at the poly engine's OWN sample rate
    // (BasicPitchModel.SampleRateHz -- BasicPitchTranscriber resamples internally), not the mic's --
    // so they are rescaled to `rate` (the mic rate) first via NoteEventRescaler.Rescale. That
    // lets RenderCommand/WavFileWriter -- which require notes and audio to share ONE declared rate
    // (the Domain's mixed-sample-rate guard, CLAUDE.md §4 non-negotiable 3) -- be reused completely
    // unchanged, exactly as the mono path uses them.
    //
    // `separate`: with `listen --separate`, LivePolyphonicView's own final save
    // (FinalPianizeAndWrite) ALREADY wrote recreation.wav via the full batch pianize pipeline (all
    // stems, always -- not gated behind --record) -- re-rendering it here from
    // result.RawEvents/NoteEventRescaler would be redundant work for no benefit (rendering is
    // deterministic, R8.2, so at best it reproduces the same bytes), so that step is skipped;
    // input.wav (raw captured mic audio) is still written when --record is set, exactly as usual.
    private static void FinalizePolyphonicRecording(
        string outDir, SampleRate rate, Lazy<MeltySynthSynthesizer> synthesizer,
        TextWriter stdout, StringBuilder logBuffer,
        LivePolyphonicResult result, string timestamp, bool doRecord, bool separate)
    {
        if (doRecord)
        {
            float[] inputPcm = result.CapturedFrames.Count > 0
                ? Framing.ReconstructMono(result.CapturedFrames)
                : Array.Empty<float>();

            if (inputPcm.Length > 0)
            {
                string inputPath = Path.Combine(outDir, "input.wav");
                WavFileWriter.Write(inputPath, inputPcm, rate);
                stdout.WriteLine($"Wrote {inputPath}.");
            }

            if (!separate)
            {
                IReadOnlyList<NoteEvent> recreationNotes = NoteEventRescaler.Rescale(result.RawEvents, rate);
                string recreationPath = Path.Combine(outDir, "recreation.wav");
                RenderCommand.RenderToWav(recreationNotes, synthesizer.Value, rate, recreationPath);
                stdout.WriteLine($"Wrote {recreationPath}.");
            }
        }

        string archiveDir = SessionOutputArchive.Archive(outDir, timestamp);
        stdout.WriteLine($"Archived to {archiveDir}.");

        // Written LAST so the log captures every line above.
        string log = logBuffer.ToString();
        File.WriteAllText(Path.Combine(archiveDir, "log.txt"), log);
        File.WriteAllText(Path.Combine(outDir, "log.txt"), log);
    }

    // Best-effort cross-platform default-browser open (Phase-2 §8 item 3). Never throws: the URL is
    // already printed above, so the user can always open it by hand if this fails. Re-created here
    // (rather than moved out of Program.cs, which the Stage 5 CLI migration leaves untouched) because
    // the pre-Stage-5 version is a `Program.cs` top-level-statement local function.
    private static void TryOpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("cmd", $"/c start \"\" \"{url}\"") { CreateNoWindow = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else if (OperatingSystem.IsLinux())
                Process.Start("xdg-open", url);
        }
        catch
        {
            // best effort only; the URL was already printed above for the user to open by hand.
        }
    }
}
