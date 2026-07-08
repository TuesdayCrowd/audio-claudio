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
            TranscribeCommand.Run(args[1], tempo, outDir);
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
            // claudio listen --tempo N [--out-dir .] [--view] [--record]
            // The composition root — the ONLY place adapters are constructed (Section 7) and
            // Ctrl+C is wired. The mic is just one more IAudioSource; the live print streams from
            // pipeline.StreamNotes; the accurate files come from pipeline.Transcribe on stop.
            int tempo = int.Parse(
                TryReadOption(args, "--tempo")
                    ?? throw new ArgumentException("listen requires --tempo <bpm>"),
                System.Globalization.CultureInfo.InvariantCulture);
            string outDir = TryReadOption(args, "--out-dir") ?? ".";
            // Session archiving: the out-dir root always holds the LATEST run's files at stable paths;
            // each session is copied into a start-timestamped subfolder on stop (folder = when recording
            // STARTED). Reading the wall clock here is a composition-root concern (the domain stays clock-free).
            string sessionTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            Directory.CreateDirectory(outDir);
            var cleared = SessionOutputArchive.CleanLatest(outDir);
            if (cleared.Count > 0)
                Console.WriteLine($"Cleared {cleared.Count} previous output file(s) from {outDir}.");
            bool view = Array.IndexOf(args, "--view") >= 0;
            bool record = Array.IndexOf(args, "--record") >= 0;
            const int SampleRateHz = 44100, FrameSize = 1024, Hop = 256;

            var settings = TranscriptionSettings.ForTempo(tempo) with { FrameSize = FrameSize, Hop = Hop };
            var pipeline = new TranscriptionPipeline(settings, new Radix2Fft()); // Domain FFT (Step 3 Option A)

            using var micSource = new PortAudioAudioSource(SampleRateHz, FrameSize, Hop, channels: 1);
            var midiWriter = new DryWetMidiWriter(); // implements INoteEventWriter + IScoreWriter
            var session = new LiveTranscriptionSession(pipeline.StreamNotes, pipeline.Transcribe);

            // --view (live-notation view, Phase-2 §8 item 3): a local HTTP server + browser tab
            // rendering the growing score. Both stay null when --view is absent, so plain
            // `listen` behavior (R10.3) is completely unchanged.
            using var server = view
                ? new LiveNotationServer(Path.Combine(AppContext.BaseDirectory, "wwwroot"))
                : null;
            var projector = view
                ? new LiveScoreProjector(new QuantizationGrid(new SampleRate(SampleRateHz), new Tempo(tempo),
                                                               TimeSignature.FourFour, Subdivision.Sixteenth))
                : null;

            Action<NoteEvent>? onLiveNote = null;
            Action<Score>? onFinalScore = null;
            if (server is not null && projector is not null)
            {
                bool viewStarted;
                try
                {
                    server.Start();
                    viewStarted = true;
                }
                catch (Exception ex)
                {
                    // The view is OPTIONAL. On any server-start failure (e.g. the documented
                    // ephemeral-port bind race, or a locked-down environment), degrade to plain
                    // `listen` — transcription + the raw.mid/score.mid/score.musicxml trio — rather
                    // than abort. The hooks stay null, so nothing is ever wired to a server that
                    // never started.
                    Console.Error.WriteLine(
                        $"Live notation view unavailable ({ex.Message}); continuing without it.");
                    viewStarted = false;
                }

                if (viewStarted)
                {
                    Console.WriteLine($"Live notation view: {server.BaseUrl}");
                    TryOpenBrowser(server.BaseUrl);

                    LiveNotationServer liveServer = server;
                    LiveScoreProjector liveProjector = projector;
                    onLiveNote = n => liveServer.PublishScore(liveProjector.Add(n));
                    onFinalScore = s => liveServer.PublishScore(s);
                }
            }

            // R10.3: listen now emits score.musicxml on stop, alongside raw.mid/score.mid.
            var listen = new ListenCommand(session, midiWriter, midiWriter, Console.WriteLine,
                                            musicXmlWriter: new MusicXmlScoreWriter(),
                                            onLiveNote: onLiveNote, onFinalScore: onFinalScore);

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; micSource.Stop(); cts.Cancel(); };
            micSource.Start();
            var result = listen.Run(micSource, tempo, outDir, cts.Token);

            // --record (opt-in): also write the real captured audio and the transcription
            // synthesized back, so the two can be loaded side by side (e.g. in Audacity) and
            // compared. `synthesizer.Value` forces the Lazy here, so plain `listen` still never
            // touches a SoundFont (Step 9's lazy-construction guarantee, R8.1).
            if (record)
            {
                if (result.CapturedFrames.Count > 0)
                {
                    string inputPath = Path.Combine(outDir, "input.wav");
                    WavFileWriter.Write(inputPath, Framing.ReconstructMono(result.CapturedFrames), result.CapturedFrames[0].Rate);
                    Console.WriteLine($"Wrote {inputPath}.");
                }

                string recreationPath = Path.Combine(outDir, "recreation.wav");
                RenderCommand.RenderToWav(result.Events, synthesizer.Value, rate, recreationPath);
                Console.WriteLine($"Wrote {recreationPath}.");
            }

            if (onFinalScore is not null)
                Thread.Sleep(TimeSpan.FromSeconds(1)); // view was wired: let the final SSE push reach the browser

            string archiveDir = SessionOutputArchive.Archive(outDir, sessionTimestamp);
            Console.WriteLine($"Archived this session to {archiveDir}.");
            return 0;
        }
    default:
        return Usage();
}

static int Usage()
{
    Console.Error.WriteLine("usage: claudio <transcribe|listen|render|play> ...");
    Console.Error.WriteLine("  transcribe <in.wav> --tempo <bpm> [--out-dir <dir>]   -> raw.mid, score.mid, score.musicxml");
    Console.Error.WriteLine("  listen --tempo <bpm> [--out-dir <dir>] [--view] [--record]  -> live; raw.mid, score.mid, score.musicxml on Ctrl+C; --view opens a browser sheet-music view; --record also writes input.wav + recreation.wav");
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
