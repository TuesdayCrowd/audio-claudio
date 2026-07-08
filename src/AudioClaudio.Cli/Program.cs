using System.Diagnostics;
using AudioClaudio.Application;
using AudioClaudio.Application.UseCases;
using AudioClaudio.Cli.Commands;
using AudioClaudio.Cli.Composition;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Infrastructure.LiveView;
using AudioClaudio.Infrastructure.Midi;
using AudioClaudio.Infrastructure.MusicXml;
using AudioClaudio.Infrastructure.Synthesis;

var rate = new SampleRate(44100);

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
            // claudio transcribe <in.wav> --tempo N [--out-dir .]
            double tempo = double.Parse(
                TryReadOption(args, "--tempo")
                    ?? throw new ArgumentException("transcribe requires --tempo <bpm>"),
                System.Globalization.CultureInfo.InvariantCulture);
            string outDir = TryReadOption(args, "--out-dir") ?? ".";
            bool noteNames = Array.IndexOf(args, "--note-names") >= 0;
            TranscribeCommand.Run(args[1], tempo, outDir, noteNames);
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
    case "listen":
        {
            // claudio listen --tempo N [--out-dir .] [--view] [--record] [--skip-silence]
            // The composition root — the ONLY place adapters are constructed (Section 7) and Ctrl+C is
            // wired. With --view, recording is driven by Start/Stop buttons in the browser: multiple
            // recordings per run, each saved under its own start-timestamp; the mic opens on Start and
            // closes on Stop; Ctrl+C exits, auto-saving a recording still in progress. Without --view it
            // is a single recording from launch to Ctrl+C.
            int tempo = int.Parse(
                TryReadOption(args, "--tempo")
                    ?? throw new ArgumentException("listen requires --tempo <bpm>"),
                System.Globalization.CultureInfo.InvariantCulture);
            string outDir = TryReadOption(args, "--out-dir") ?? ".";
            Directory.CreateDirectory(outDir);
            bool view = Array.IndexOf(args, "--view") >= 0;
            bool skipSilence = Array.IndexOf(args, "--skip-silence") >= 0;
            bool record = Array.IndexOf(args, "--record") >= 0 || skipSilence; // --skip-silence implies --record
            bool noteNames = Array.IndexOf(args, "--note-names") >= 0;
            const int SampleRateHz = 44100, FrameSize = 1024, Hop = 256;

            var settings = TranscriptionSettings.ForTempo(tempo) with { FrameSize = FrameSize, Hop = Hop };
            var pipeline = new TranscriptionPipeline(settings, new Radix2Fft()); // Domain FFT (Step 3 Option A)
            var midiWriter = new DryWetMidiWriter(); // implements INoteEventWriter + IScoreWriter
            var session = new LiveTranscriptionSession(pipeline.StreamNotes, pipeline.Transcribe);
            var grid = new QuantizationGrid(new SampleRate(SampleRateHz), new Tempo(tempo),
                                            TimeSignature.FourFour, Subdivision.Sixteenth);
            var musicXml = new MusicXmlScoreWriter(noteNames);

            // One recording's outputs: the out-dir root holds the LATEST files at stable paths; on stop,
            // write --record's WAVs (optionally silence-collapsed) then archive that whole set into
            // <out-dir>/<timestamp>/ (timestamp = when the recording started).
            void FinalizeRecording(LiveSessionResult result, string timestamp)
            {
                if (record)
                {
                    float[] inputPcm = result.CapturedFrames.Count > 0
                        ? Framing.ReconstructMono(result.CapturedFrames)
                        : Array.Empty<float>();
                    IReadOnlyList<NoteEvent> recreationNotes = result.Events;

                    if (skipSilence)
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

                    server.StartRequested = () => startSignal.Release();
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

                        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                        server.PublishClear(); // blank the staff for the new recording
                        var cleared = SessionOutputArchive.CleanLatest(outDir);
                        if (cleared.Count > 0)
                            Console.WriteLine($"Cleared {cleared.Count} previous output file(s) from {outDir}.");

                        var projector = new LiveScoreProjector(grid); // fresh accumulation per recording
                        LiveNotationServer liveServer = server;
                        var listen = new ListenCommand(session, midiWriter, midiWriter, Console.WriteLine,
                                                        musicXmlWriter: musicXml,
                                                        onLiveNote: n => liveServer.PublishScore(projector.Add(n)),
                                                        onFinalScore: s => liveServer.PublishScore(s));

                        var mic = new PortAudioAudioSource(SampleRateHz, FrameSize, Hop, channels: 1);
                        lock (gate) { currentMic = mic; }
                        Console.WriteLine($"Recording {timestamp}...");
                        mic.Start();
                        // Runs until mic.Stop() ends the frames — triggered by the Stop button or Ctrl+C.
                        var result = listen.Run(mic, tempo, outDir, CancellationToken.None);
                        lock (gate) { currentMic = null; }
                        mic.Dispose();

                        FinalizeRecording(result, timestamp);
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
                    micSource.Start();
                    var result = listen.Run(micSource, tempo, outDir, cts.Token);
                    FinalizeRecording(result, timestamp);
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
    Console.Error.WriteLine("  transcribe <in.wav> --tempo <bpm> [--out-dir <dir>] [--note-names]   -> raw.mid, score.mid, score.musicxml");
    Console.Error.WriteLine("  listen --tempo <bpm> [--out-dir <dir>] [--view] [--record] [--skip-silence] [--note-names]  -> live; raw.mid, score.mid, score.musicxml on Ctrl+C; --view opens a browser sheet-music view with Start/Stop recording buttons (multiple takes, each saved under its own timestamp); --record also writes input.wav + recreation.wav; --skip-silence: continuous playback — drop pauses >500ms from input.wav + recreation.wav (implies --record); --note-names prints each note's name (e.g. C4) beneath it");
    Console.Error.WriteLine("  render|play <in.mid> [<out.wav>] [--soundfont <path>]");
    return 1;
}

static string? TryReadOption(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
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
