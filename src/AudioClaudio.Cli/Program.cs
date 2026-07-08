using AudioClaudio.Application;
using AudioClaudio.Application.UseCases;
using AudioClaudio.Cli.Commands;
using AudioClaudio.Cli.Composition;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Infrastructure.Audio;
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
            // claudio listen --tempo N [--out-dir .]
            // The composition root — the ONLY place adapters are constructed (Section 7) and
            // Ctrl+C is wired. The mic is just one more IAudioSource; the live print streams from
            // pipeline.StreamNotes; the accurate files come from pipeline.Transcribe on stop.
            int tempo = int.Parse(
                TryReadOption(args, "--tempo")
                    ?? throw new ArgumentException("listen requires --tempo <bpm>"),
                System.Globalization.CultureInfo.InvariantCulture);
            string outDir = TryReadOption(args, "--out-dir") ?? ".";
            const int SampleRateHz = 44100, FrameSize = 1024, Hop = 256;

            var settings = TranscriptionSettings.ForTempo(tempo) with { FrameSize = FrameSize, Hop = Hop };
            var pipeline = new TranscriptionPipeline(settings, new Radix2Fft()); // Domain FFT (Step 3 Option A)

            using var micSource = new PortAudioAudioSource(SampleRateHz, FrameSize, Hop, channels: 1);
            var midiWriter = new DryWetMidiWriter(); // implements INoteEventWriter + IScoreWriter
            var session = new LiveTranscriptionSession(pipeline.StreamNotes, pipeline.Transcribe);
            // R10.3: listen now emits score.musicxml on stop, alongside raw.mid/score.mid.
            var listen = new ListenCommand(session, midiWriter, midiWriter, Console.WriteLine,
                                            musicXmlWriter: new MusicXmlScoreWriter());

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; micSource.Stop(); cts.Cancel(); };
            micSource.Start();
            listen.Run(micSource, tempo, outDir, cts.Token);
            return 0;
        }
    default:
        return Usage();
}

static int Usage()
{
    Console.Error.WriteLine("usage: claudio <transcribe|listen|render|play> ...");
    Console.Error.WriteLine("  transcribe <in.wav> --tempo <bpm> [--out-dir <dir>]   -> raw.mid, score.mid, score.musicxml");
    Console.Error.WriteLine("  listen --tempo <bpm> [--out-dir <dir>]                -> live; raw.mid, score.mid, score.musicxml on Ctrl+C");
    Console.Error.WriteLine("  render|play <in.mid> [<out.wav>] [--soundfont <path>]");
    return 1;
}

static string? TryReadOption(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}
