using System.Diagnostics;
using AudioClaudio.Application;
using AudioClaudio.Application.UseCases;
using AudioClaudio.Cli.Commands;
using AudioClaudio.Cli.Composition;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Evaluation;
using AudioClaudio.Domain.Polyphony;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Infrastructure.LiveView;
using AudioClaudio.Infrastructure.Midi;
using AudioClaudio.Infrastructure.MusicXml;
using AudioClaudio.Infrastructure.Synthesis;
using AudioClaudio.Infrastructure.Transcription;

var rate = new SampleRate(44100);

var logBuffer = new System.Text.StringBuilder();
Console.SetOut(new AudioClaudio.Cli.Composition.TeeTextWriter(Console.Out, logBuffer));

if (args.Length == 0)
    return Usage();

// SoundFont/synth construction is LAZY (Step 9): `transcribe` never touches a synthesizer, so it
// must run with no .sf2 present. Only `render`/`play` resolve and construct it, on first use.
string? soundFontOption = TryReadOption(args, "--soundfont");
var synthesizer = new Lazy<MeltySynthSynthesizer>(() => new MeltySynthSynthesizer(SoundFontLocator.Resolve(soundFontOption)));

switch (args[0])
{
    case "transcribe" when args.Length >= 2:
        {
            // claudio transcribe <in.wav> [--tempo N] [--out-dir .]
            double? tempo = TryReadOption(args, "--tempo") is { } t
                ? double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)
                : null;
            string outDir = TryReadOption(args, "--out-dir") ?? ".";
            bool noteNames = Array.IndexOf(args, "--note-names") >= 0;
            bool legato = Array.IndexOf(args, "--legato") >= 0; // v2 Stage 2: opt-in legato recovery (--mono path)
            bool coarseRhythm = Array.IndexOf(args, "--coarse-rhythm") >= 0; // v2 Stage 2: coarse-grid note-off (--mono path)
            // As of v0.2.0 the polyphonic Basic Pitch engine is the default; --mono opts back into the
            // monophonic YIN pipeline (the closed-loop-proven path).
            bool poly = TranscribeModeResolver.Resolve(args) == TranscribeMode.Polyphonic;
            if (poly)
            {
                // Polyphonic path (Basic Pitch). raw.mid is the honest many-note output; score.mid/
                // score.musicxml are quantized with the (still monophonic) quantizer for now.
                Directory.CreateDirectory(outDir);
                string modelPath = ModelLocator.Resolve(TryReadOption(args, "--model"));
                if (!TryReadKeyOverride(args, out int? keyOverride, out string? keyError)) // sharps +, flats −; overrides auto-detect
                {
                    Console.Error.WriteLine(keyError);
                    return 1;
                }

                using var polySource = WavAudioSource.FromFile(args[1], new FrameParameters(1024, 256));
                var decoderOptions = PolyDecoderOptions.FromArgs(args); // --onset-threshold/--frame-threshold/--min-note-len (Stage 4b)
                using var polyTx = new BasicPitchTranscriber(modelPath, decoderOptions, tempo: tempo is { } bpm ? new Tempo(bpm) : null);
                TranscriptionResult polyResult = polyTx.Transcribe(polySource);
                // Auto-detect the key signature from the notes (Krumhansl-Schmuckler) unless --key declares it.
                int key = keyOverride ?? KeyDetector.Detect(polyResult.RawEvents.Select(e => e.Pitch).ToList());
                var polyWriter = new DryWetMidiWriter();
                using (var raw = File.Create(Path.Combine(outDir, "raw.mid")))
                    polyWriter.Write(polyResult.RawEvents, polyResult.Score.Tempo, raw); // honest un-quantized polyphony

                // Stage 3: quantize into a polyphonic grand-staff score (chords + two staves), then
                // emit both score.musicxml (grand staff) and a flattened polyphonic score.mid.
                var polyRate = new SampleRate(BasicPitchModel.SampleRateHz);
                var polyGrid = new QuantizationGrid(polyRate, polyResult.Score.Tempo, TimeSignature.FourFour, Subdivision.Sixteenth);
                var chordWindow = new SampleDuration(polyRate.Hz / 20, polyRate); // ~50 ms: notes this close are one chord
                var grandStaff = PolyphonicQuantizer.Quantize(polyResult.RawEvents, polyGrid, chordWindow);
                using (var mx = File.Create(Path.Combine(outDir, "score.musicxml")))
                    new GrandStaffMusicXmlWriter(noteNames, fifths: key).Write(grandStaff, mx);
                var quantized = GrandStaffFlattener.ToNoteEvents(grandStaff, polyGrid);
                using (var score = File.Create(Path.Combine(outDir, "score.mid")))
                    polyWriter.Write(quantized, polyResult.Score.Tempo, score);
                Console.WriteLine($"Polyphonic transcription: {polyResult.RawEvents.Count} notes -> raw.mid; {quantized.Count} quantized -> score.mid + score.musicxml (grand staff, {grandStaff.Measures.Count} bars, key {key:+#;-#;0}{(keyOverride is null ? " detected" : " declared")})");
                File.WriteAllText(Path.Combine(outDir, "log.txt"), logBuffer.ToString());
                return 0;
            }

            TranscribeCommand.Run(args[1], tempo, outDir, noteNames, legato, coarseRhythm);
            File.WriteAllText(Path.Combine(outDir, "log.txt"), logBuffer.ToString());
            return 0;
        }
    case "notate" when args.Length >= 2:
        {
            // claudio notate <in.mid> [--out-dir .] [--tempo N] [--key F] [--note-names]
            // Engrave an existing MIDI as a grand-staff score (reads honor the sustain pedal). Lets a
            // richer source (e.g. a piano-specific transcriber's MIDI, with real durations + pedal) flow
            // through the same score-building the polyphonic transcribe path uses.
            string outDir = TryReadOption(args, "--out-dir") ?? ".";
            Directory.CreateDirectory(outDir);
            bool noteNames = Array.IndexOf(args, "--note-names") >= 0;
            if (!TryReadKeyOverride(args, out int? keyOverride, out string? keyError))
            {
                Console.Error.WriteLine(keyError);
                return 1;
            }

            var read = MidiFileReader.ReadFile(args[1], rate, flattenPedal: false); // notation wants raw key-press durations
            // Auto-detect the key signature from the notes (Krumhansl-Schmuckler) unless --key declares it.
            int key = keyOverride ?? KeyDetector.Detect(read.Events.Select(e => e.Pitch).ToList());
            // Auto-estimate the tempo from the note onsets when --tempo is omitted (median inter-onset
            // interval); the MIDI's own tempo is only the fallback when there is too little rhythm.
            string? tempoArg = TryReadOption(args, "--tempo");
            Tempo scoreTempo = tempoArg is null
                ? TempoEstimator.Estimate(read.Events, read.Tempo)
                : new Tempo(double.Parse(tempoArg, System.Globalization.CultureInfo.InvariantCulture));
            var grid = new QuantizationGrid(rate, scoreTempo, TimeSignature.FourFour, Subdivision.Sixteenth);
            var chordWindow = new SampleDuration(rate.Hz / 20, rate); // ~50 ms merge window
            var grandStaff = PolyphonicQuantizer.Quantize(read.Events, grid, chordWindow);
            var writer = new DryWetMidiWriter();
            // Sustain-pedal marks (CC64) become pedal lines in the score, positioned by grid tick.
            var pedalMarks = read.PedalChanges
                .Select(c => ((int)grid.SamplesToTick(c.Sample), c.Down))
                .ToList();
            using (var mx = File.Create(Path.Combine(outDir, "score.musicxml")))
                new GrandStaffMusicXmlWriter(noteNames, fifths: key).Write(grandStaff, mx, pedalMarks);
            var quantized = GrandStaffFlattener.ToNoteEvents(grandStaff, grid);
            using (var score = File.Create(Path.Combine(outDir, "score.mid")))
                writer.Write(quantized, scoreTempo, score);
            Console.WriteLine($"Notated {read.Events.Count} notes ({scoreTempo.BeatsPerMinute:F0} BPM{(tempoArg is null ? " estimated" : "")}) -> score.musicxml + score.mid (grand staff, {grandStaff.Measures.Count} bars, key {key:+#;-#;0}{(keyOverride is null ? " detected" : " declared")})");
            return 0;
        }
    case "render" when args.Length >= 3:
        {
            // Step 7 reader: load the committed/source MIDI into domain NoteEvents.
            var notes = MidiFileReader.ReadFile(args[1], rate).Events;
            RenderCommand.RenderToWav(notes, synthesizer.Value, rate, args[2]);
            return 0;
        }
    case "play" when args.Length >= 2:
        {
            var notes = MidiFileReader.ReadFile(args[1], rate).Events;
            PlayCommand.Play(notes, synthesizer.Value, rate);
            return 0;
        }
    case "evaluate" when args.Length >= 3:
        {
            // claudio evaluate <candidate.mid> <reference.mid> [--onset-tolerance-ms 50] [--align]
            // Note-level precision/recall/F1 of a transcription against a reference note-set.
            var candidate = MidiFileReader.ReadFile(args[1], rate).Events;
            var reference = MidiFileReader.ReadFile(args[2], rate).Events;
            double tolMs = TryReadOption(args, "--onset-tolerance-ms") is { } t
                ? double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)
                : 50.0;
            // Alignment cancels the performance-vs-score timing difference before scoring, so the F1
            // reflects pitch recovery rather than tempo drift. --align (Stage 4a) removes the single
            // gross tempo ratio; --warp (DTW) additionally removes local rubato. --warp wins if both
            // are passed. Alignment uses only timing, never pitch, so pitch recovery is judged cleanly.
            bool warp = Array.IndexOf(args, "--warp") >= 0;
            bool align = Array.IndexOf(args, "--align") >= 0;
            IReadOnlyList<NoteEvent> evalCandidate = candidate;
            if (warp)
            {
                evalCandidate = OnsetAlignment.DtwWarp(candidate, reference);
                Console.WriteLine("(candidate DTW-warped to the reference timeline — local rubato removed)");
            }
            else if (align)
            {
                evalCandidate = OnsetAlignment.GlobalScale(candidate, reference);
                Console.WriteLine("(candidate globally time-aligned to the reference span)");
            }

            EvaluateCommand.Run(evalCandidate, reference, new NoteMatchOptions(tolMs / 1000.0), Console.WriteLine);
            return 0;
        }
    case "evaluate-audio" when args.Length >= 3:
        {
            // claudio evaluate-audio <original.wav> <reproduction.wav>
            // Timbre-robust pitch-content similarity — "does the re-synthesis sound like the original?".
            // Compares chromagrams (per-frame pitch-class energy), so a real piano and a SoundFont render
            // of the transcription are comparable by NOTES, not by timbre. 1.0 = identical pitch content.
            const int FrameSize = 4096, Hop = 2048;
            using var audioA = WavAudioSource.FromFile(args[1], new FrameParameters(FrameSize, Hop));
            using var audioB = WavAudioSource.FromFile(args[2], new FrameParameters(FrameSize, Hop));
            var chromaA = Chromagram.FromFrames(audioA.Frames, FrameSize);
            var chromaB = Chromagram.FromFrames(audioB.Frames, FrameSize);
            double similarity = ChromaSimilarity.Compare(chromaA, chromaB);
            Console.WriteLine($"Chroma (pitch-content) similarity: {similarity:P1}  ({chromaA.Count} vs {chromaB.Count} frames)");
            return 0;
        }
    case "listen":
        {
            // claudio listen [--tempo N] [--out-dir .] [--view] [--record] [--skip-silence]
            // The composition root — the ONLY place adapters are constructed (Section 7) and Ctrl+C is
            // wired. With --view, recording is driven by Start/Stop buttons in the browser: multiple
            // recordings per run, each saved under its own start-timestamp; the mic opens on Start and
            // closes on Stop; Ctrl+C exits, auto-saving a recording still in progress. Without --view it
            // is a single recording from launch to Ctrl+C.
            string? tempoArg = TryReadOption(args, "--tempo");
            bool estimateTempo = tempoArg is null;
            double tempoBpm = tempoArg is null ? 120.0 : double.Parse(tempoArg, System.Globalization.CultureInfo.InvariantCulture);
            string outDir = TryReadOption(args, "--out-dir") ?? ".";
            Directory.CreateDirectory(outDir);
            bool view = Array.IndexOf(args, "--view") >= 0;
            bool skipSilence = Array.IndexOf(args, "--skip-silence") >= 0;
            bool record = Array.IndexOf(args, "--record") >= 0 || skipSilence; // --skip-silence implies --record
            bool noteNames = Array.IndexOf(args, "--note-names") >= 0;
            const int SampleRateHz = 44100, FrameSize = 1024, Hop = 256;

            var settings = TranscriptionSettings.ForTempo(tempoBpm) with { FrameSize = FrameSize, Hop = Hop, EstimateTempo = estimateTempo };
            var pipeline = new TranscriptionPipeline(settings, new Radix2Fft()); // Domain FFT (Step 3 Option A)
            var midiWriter = new DryWetMidiWriter(); // implements INoteEventWriter + IScoreWriter
            var session = new LiveTranscriptionSession(pipeline.StreamNotes, pipeline.Transcribe);
            // The live preview's grid always uses the fallback/declared tempoBpm (never the estimate --
            // the estimate is only known after the batch pass on stop); the SAVED score.mid/score.musicxml
            // come from result.Score, which IS estimated when estimateTempo is set (via `settings` above).
            var grid = new QuantizationGrid(new SampleRate(SampleRateHz), new Tempo(tempoBpm),
                                            TimeSignature.FourFour, Subdivision.Sixteenth);
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
                        Console.WriteLine($"Wrote {inputPath}.");
                    }

                    string recreationPath = Path.Combine(outDir, "recreation.wav");
                    RenderCommand.RenderToWav(recreationNotes, synthesizer.Value, rate, recreationPath);
                    Console.WriteLine($"Wrote {recreationPath}.");
                }

                string archiveDir = SessionOutputArchive.Archive(outDir, timestamp);
                Console.WriteLine($"Archived to {archiveDir}.");

                if (estimateTempo)
                    Console.WriteLine($"Estimated tempo: {result.Score.Tempo.BeatsPerMinute:F0} BPM.");

                // Written LAST so the log captures every line above, including the estimated tempo.
                string log = logBuffer.ToString();
                File.WriteAllText(Path.Combine(archiveDir, "log.txt"), log);
                File.WriteAllText(Path.Combine(outDir, "log.txt"), log);
            }

            LiveNotationServer? server = null;
            if (view)
            {
                server = new LiveNotationServer(Path.Combine(AppContext.BaseDirectory, "wwwroot"),
                                                scoreToMusicXml: musicXml.WriteToString);
                try
                {
                    server.Start();
                }
                catch (Exception ex)
                {
                    // The view is OPTIONAL. On a server-start failure, degrade to a plain single
                    // recording (launch -> Ctrl+C) rather than abort.
                    Console.Error.WriteLine($"Live notation view unavailable ({ex.Message}); continuing without it.");
                    server.Dispose();
                    server = null;
                }
            }

            try
            {
                if (server is not null)
                {
                    Console.WriteLine($"Live notation view: {server.BaseUrl}");
                    Console.WriteLine("Press Start in the browser to record, Stop to save. Ctrl+C exits (saving a recording in progress).");
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

                        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                        logBuffer.Clear();
                        server.PublishClear(); // blank the staff for the new recording
                        var cleared = SessionOutputArchive.CleanLatest(outDir);
                        if (cleared.Count > 0)
                            Console.WriteLine($"Cleared {cleared.Count} previous output file(s) from {outDir}.");

                        var projector = new LiveScoreProjector(grid); // fresh accumulation per recording
                        LiveNotationServer liveServer = server;
                        var recordingWriter = new MusicXmlScoreWriter(opts.NoteNames, opts.Title);
                        server.ScoreToMusicXml = recordingWriter.WriteToString;
                        var listen = new ListenCommand(session, midiWriter, midiWriter, Console.WriteLine,
                                                        musicXmlWriter: recordingWriter,
                                                        onLiveNote: n => liveServer.PublishScore(projector.Add(n)),
                                                        onFinalScore: s => liveServer.PublishScore(s));

                        var mic = new PortAudioAudioSource(SampleRateHz, FrameSize, Hop, channels: 1);
                        lock (gate) { currentMic = mic; }
                        Console.WriteLine($"Recording {timestamp}...");
                        mic.Start();
                        // Runs until mic.Stop() ends the frames — triggered by the Stop button or Ctrl+C.
                        var result = listen.Run(mic, (int)Math.Round(tempoBpm), outDir, CancellationToken.None);
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
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                    var cleared = SessionOutputArchive.CleanLatest(outDir);
                    if (cleared.Count > 0)
                        Console.WriteLine($"Cleared {cleared.Count} previous output file(s) from {outDir}.");

                    using var micSource = new PortAudioAudioSource(SampleRateHz, FrameSize, Hop, channels: 1);
                    var listen = new ListenCommand(session, midiWriter, midiWriter, Console.WriteLine,
                                                    musicXmlWriter: musicXml);
                    using var cts = new CancellationTokenSource();
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; micSource.Stop(); cts.Cancel(); };
                    logBuffer.Clear();
                    micSource.Start();
                    var result = listen.Run(micSource, (int)Math.Round(tempoBpm), outDir, cts.Token);
                    FinalizeRecording(result, timestamp, record, skipSilence);
                }
            }
            finally
            {
                server?.Dispose();
            }
            return 0;
        }
    default:
        return Usage();
}

static int Usage()
{
    Console.Error.WriteLine("usage: claudio <transcribe|listen|render|play> ...");
    Console.Error.WriteLine("  transcribe <in.wav> [--tempo <bpm>] [--out-dir <dir>] [--note-names] [--mono] [--model <path>] [--key <fifths>] [--onset-threshold <v>] [--frame-threshold <v>] [--min-note-len <frames>]   -> raw.mid, score.mid, score.musicxml; POLYPHONIC (Basic Pitch, grand staff) by default (closed-loop-proven: note-level F1 >= 0.75 at 50ms onset tolerance, seed-4242 corpus); --mono uses the monophonic YIN pipeline (exact-recovery closed loop; auto-estimates tempo when --tempo is omitted); --key overrides the auto-detected key signature (sharps +, flats -, e.g. -4 = A-flat major) that drives enharmonic spelling; the three thresholds tune note density; --legato (with --mono) opts into legato note recovery (a wobble-vs-legato trade-off, off by default); --coarse-rhythm (with --mono) floors note values at an eighth for cleaner rhythm from uneven playing");
    Console.Error.WriteLine("  listen [--tempo <bpm>] [--out-dir <dir>] [--view] [--record] [--skip-silence] [--note-names]  -> live; raw.mid, score.mid, score.musicxml on Ctrl+C; omit --tempo to auto-estimate it from your playing; --view opens a browser sheet-music view with Start/Stop recording buttons (multiple takes, each saved under its own timestamp); --record also writes input.wav + recreation.wav; --skip-silence: continuous playback — drop pauses >500ms from input.wav + recreation.wav (implies --record); --note-names prints each note's name (e.g. C4) beneath it");
    Console.Error.WriteLine("  render|play <in.mid> [<out.wav>] [--soundfont <path>]   (both honor the CC64 sustain pedal)");
    Console.Error.WriteLine("  notate <in.mid> [--out-dir <dir>] [--tempo <bpm>] [--key <fifths>] [--note-names]   -> engrave a MIDI as a grand-staff score.musicxml + score.mid (tempo auto-estimated from the onsets unless --tempo; key auto-detected unless --key)");
    Console.Error.WriteLine("  evaluate <candidate.mid> <reference.mid> [--onset-tolerance-ms <ms>] [--align|--warp]  -> note-level precision/recall/F1 vs a reference; --align cancels the global tempo difference, --warp (DTW) also removes local rubato");
    Console.Error.WriteLine("  evaluate-audio <original.wav> <reproduction.wav>  -> timbre-robust pitch-content (chroma) similarity: does the re-synthesis sound like the original? 1.0 = identical notes over time");
    return 1;
}

static string? TryReadOption(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

// Parse an optional --key override, validating it to a real key signature (fifths −7..+7). Absent → the
// key is auto-detected. Fails fast with a clean message rather than letting a nonsensical value crash the
// speller or silently emit a garbage <fifths> (v2 Stage 3b review finding).
static bool TryReadKeyOverride(string[] args, out int? fifths, out string? error)
{
    fifths = null;
    error = null;
    if (TryReadOption(args, "--key") is not { } raw)
        return true;

    if (!AudioClaudio.Cli.Commands.KeyOption.TryParse(raw, out int value, out error))
        return false;

    fifths = value;
    return true;
}

// Best-effort cross-platform default-browser open (Phase-2 §8 item 3). Never throws: the URL is
// already printed above, so the user can always open it by hand if this fails.
static void TryOpenBrowser(string url)
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
