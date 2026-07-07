using AudioClaudio.Cli.Commands;
using AudioClaudio.Cli.Composition;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi;
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
    default:
        return Usage();
}

static int Usage()
{
    Console.Error.WriteLine("usage: claudio <transcribe|render|play> ...");
    Console.Error.WriteLine("  transcribe <in.wav> --tempo <bpm> [--out-dir <dir>]   -> raw.mid, score.mid");
    Console.Error.WriteLine("  render|play <in.mid> [<out.wav>] [--soundfont <path>]");
    return 1;
}

static string? TryReadOption(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}
