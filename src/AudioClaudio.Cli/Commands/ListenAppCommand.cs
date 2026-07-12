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
        bool skipSilence = p.Flag("skip-silence");
        bool record = p.Flag("record") || skipSilence; // --skip-silence implies --record
        bool noteNames = p.Flag("note-names");

        // Polyphonic is the DEFAULT `listen` engine (mirrors `transcribe`'s poly-default/--mono
        // pattern); --mono opts into the proven monophonic path below, entirely unchanged -- nothing
        // here touches TranscriptionPipeline/LiveTranscriptionSession, and nothing below runs unless
        // --mono is set, so the mono path is completely unaffected by this branch's existence.
        if (!p.Flag("mono"))
        {
            return RunPolyphonic(outDir, tempoBpm, view, record, skipSilence, p.Path("soundfont"), stdout, stderr, logBuffer);
        }

        var rate = new SampleRate(SampleRateHz);

        // SoundFont/synth construction is LAZY (Step 9 precedent): a plain listen session that
        // never uses --record never touches a synthesizer, so it must run with no .sf2 present.
        var synthesizer = new Lazy<MeltySynthSynthesizer>(
            () => new MeltySynthSynthesizer(SoundFontLocator.Resolve(p.Path("soundfont"))));

        var settings = TranscriptionSettings.ForTempo(tempoBpm) with { FrameSize = FrameSize, Hop = Hop, EstimateTempo = estimateTempo };
        var pipeline = new TranscriptionPipeline(settings, new Radix2Fft()); // Domain FFT (Step 3 Option A)
        var midiWriter = new DryWetMidiWriter(); // implements INoteEventWriter + IScoreWriter
        var session = new LiveTranscriptionSession(pipeline.StreamNotes, pipeline.Transcribe);
        // The live preview's grid always uses the fallback/declared tempoBpm (never the estimate --
        // the estimate is only known after the batch pass on stop); the SAVED score.mid/score.musicxml
        // come from result.Score, which IS estimated when estimateTempo is set (via `settings` above).
        var grid = new QuantizationGrid(rate, new Tempo(tempoBpm), TimeSignature.FourFour, Subdivision.Sixteenth);
        var musicXml = new MusicXmlScoreWriter(noteNames);

        // One recording's outputs: the out-dir root holds the LATEST files at stable paths; on stop,
        // write --record's WAVs (optionally silence-collapsed) then archive that whole set into
        // <out-dir>/<timestamp>/ (timestamp = when the recording started).
        void FinalizeRecording(LiveSessionResult result, string timestamp, bool doRecord, bool doSkipSilence)
        {
            if (doRecord)
            {
                float[] inputPcm = result.CapturedFrames.Count > 0
                    ? Framing.ReconstructMono(result.CapturedFrames)
                    : Array.Empty<float>();
                IReadOnlyList<NoteEvent> recreationNotes = result.Events;

                if (doSkipSilence)
                {
                    var maxSilence = new SampleDuration(rate.Hz / 2, rate); // collapse pauses > 500 ms
                    var collapsed = SilenceCollapser.Collapse(result.Events, inputPcm, rate, maxSilence);
                    inputPcm = collapsed.Audio;
                    recreationNotes = collapsed.Notes;
                }

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
                RecordOptions pendingOptions = default;

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
                    var projector = new LiveScoreProjector(grid, estimateTempo);
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
                    // Runs until mic.Stop() ends the frames — triggered by the Stop button or Ctrl+C.
                    var result = listenCmd.Run(levelSource, (int)Math.Round(tempoBpm), outDir, CancellationToken.None);
                    lock (gate) { currentMic = null; }
                    mic.Dispose();

                    FinalizeRecording(result, timestamp, opts.Record, opts.SkipSilence);
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
                FinalizeRecording(result, timestamp, record, skipSilence);
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
        string outDir, double tempoBpm, bool view, bool record, bool skipSilence, string? soundfontPath,
        TextWriter stdout, TextWriter stderr, StringBuilder logBuffer) =>
        view
            ? RunPolyphonicView(outDir, tempoBpm, record, skipSilence, soundfontPath, stdout, stderr, logBuffer)
            : RunPolyphonicHeadless(outDir, tempoBpm, record, skipSilence, soundfontPath, stdout, logBuffer);

    // SINGLE-TAKE browser loop: the mic starts recording the moment this runs, waits for the browser
    // Start button per take (mirroring the mono --view loop), captures until Stop (or Ctrl+C), saves
    // score.mid/score.musicxml, then idles for the next Start. Ctrl+C exits.
    private static int RunPolyphonicView(
        string outDir, double tempoBpm, bool record, bool skipSilence, string? soundfontPath,
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
        // fires (capturing that take's per-recording Record/Skip-silence options), capture until the
        // Stop button (or Ctrl+C) stops the mic, save, then wait for the next Start.
        var startSignal = new SemaphoreSlim(0);
        var gate = new object();
        PortAudioAudioSource? currentMic = null;
        bool exiting = false;
        RecordOptions pendingOptions = default;

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
        using var view = new LivePolyphonicView(server, outDir, tempoBpm, stdout.WriteLine);

        while (true)
        {
            startSignal.Wait();
            lock (gate) { if (exiting) break; }

            RecordOptions opts;
            lock (gate) { opts = pendingOptions; }

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            logBuffer.Clear();
            server.PublishClear(); // blank the staff for the new take

            var mic = new PortAudioAudioSource(SampleRateHz, FrameSize, Hop, channels: 1);
            lock (gate) { currentMic = mic; }
            var levelSource = new LevelTeeingAudioSource(mic,
                rms => server.PublishLevel(rms, mic.DeviceName ?? "Unknown microphone"));
            mic.Start();
            // Drains until the Stop button or Ctrl+C ends the mic frames, then writes
            // raw.mid/score.mid/score.musicxml and returns the take's raw events + captured frames.
            LivePolyphonicResult result = view.Run(levelSource, exitCts.Token);
            lock (gate) { currentMic = null; }
            mic.Dispose();

            FinalizePolyphonicRecording(outDir, rate, synthesizer, stdout, logBuffer, result, timestamp, opts.Record, opts.SkipSilence);
            Thread.Sleep(TimeSpan.FromSeconds(1)); // let the final SSE push reach the browser
            lock (gate) { if (exiting) break; }
        }

        return 0;
    }

    // Headless single take: no server, no browser, no periodic re-transcribe loop -- just drain the
    // mic from launch to Ctrl+C, then one final transcribe + raw.mid/score.mid/score.musicxml write,
    // finalized (--record/--skip-silence/archive) exactly like the --view loop's per-take path, driven
    // here by the top-level flags instead of per-take browser options.
    // LivePolyphonicView skips its whole periodic-publish machinery when constructed with a null
    // server (see its Run() doc), so this path never pays for repeated whole-buffer inference.
    private static int RunPolyphonicHeadless(
        string outDir, double tempoBpm, bool record, bool skipSilence, string? soundfontPath,
        TextWriter stdout, StringBuilder logBuffer)
    {
        var rate = new SampleRate(SampleRateHz);
        var synthesizer = new Lazy<MeltySynthSynthesizer>(
            () => new MeltySynthSynthesizer(SoundFontLocator.Resolve(soundfontPath)));

        using var mic = new PortAudioAudioSource(SampleRateHz, FrameSize, Hop, channels: 1);
        using var view = new LivePolyphonicView(server: null, outDir, tempoBpm, stdout.WriteLine);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; mic.Stop(); cts.Cancel(); };

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        logBuffer.Clear();
        mic.Start();
        // Drains until Ctrl+C stops the mic, then writes raw.mid/score.mid/score.musicxml.
        LivePolyphonicResult result = view.Run(mic, cts.Token);

        FinalizePolyphonicRecording(outDir, rate, synthesizer, stdout, logBuffer, result, timestamp, record, skipSilence);
        return 0;
    }

    // Mirrors the mono path's local FinalizeRecording: when --record is on, writes input.wav (the
    // captured mic audio) + recreation.wav (the take's poly notes re-synthesized), optionally
    // silence-collapsed; ALWAYS archives the take's output files into <outDir>/<timestamp>/ + writes
    // log.txt. The one wrinkle versus mono: LivePolyphonicResult.RawEvents live at the poly engine's
    // OWN sample rate (BasicPitchModel.SampleRateHz -- BasicPitchTranscriber resamples internally),
    // not the mic's -- so they are rescaled to `rate` (the mic rate) first via
    // LivePolyphonicView.RescaleNotes. That lets SilenceCollapser/RenderCommand/WavFileWriter -- which
    // all require notes and audio to share ONE declared rate (the Domain's mixed-sample-rate guard,
    // CLAUDE.md §4 non-negotiable 3) -- be reused completely unchanged, exactly as the mono path uses them.
    private static void FinalizePolyphonicRecording(
        string outDir, SampleRate rate, Lazy<MeltySynthSynthesizer> synthesizer,
        TextWriter stdout, StringBuilder logBuffer,
        LivePolyphonicResult result, string timestamp, bool doRecord, bool doSkipSilence)
    {
        if (doRecord)
        {
            float[] inputPcm = result.CapturedFrames.Count > 0
                ? Framing.ReconstructMono(result.CapturedFrames)
                : Array.Empty<float>();
            IReadOnlyList<NoteEvent> recreationNotes = LivePolyphonicView.RescaleNotes(result.RawEvents, rate);

            if (doSkipSilence)
            {
                var maxSilence = new SampleDuration(rate.Hz / 2, rate); // collapse pauses > 500 ms
                var collapsed = SilenceCollapser.Collapse(recreationNotes, inputPcm, rate, maxSilence);
                inputPcm = collapsed.Audio;
                recreationNotes = collapsed.Notes;
            }

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
