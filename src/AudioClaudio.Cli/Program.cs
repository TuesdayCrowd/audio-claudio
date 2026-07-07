using AudioClaudio.Cli.Commands;
using AudioClaudio.Cli.Composition;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi;
using AudioClaudio.Infrastructure.Synthesis;

var rate = new SampleRate(44100);

if (args.Length == 0)
    return Usage();

string? soundFontOption = TryReadOption(args, "--soundfont");
string soundFontPath = SoundFontLocator.Resolve(soundFontOption);
var synthesizer = new MeltySynthSynthesizer(soundFontPath);

switch (args[0])
{
    case "render" when args.Length >= 3:
        {
            // Step 7 reader: load the committed/source MIDI into domain NoteEvents.
            var notes = MidiFileReader.ReadFile(args[1], rate).Events;
            RenderCommand.RenderToWav(notes, synthesizer, rate, args[2]);
            return 0;
        }
    case "play" when args.Length >= 2:
        {
            var notes = MidiFileReader.ReadFile(args[1], rate).Events;
            PlayCommand.Play(notes, synthesizer, rate);
            return 0;
        }
    default:
        return Usage();
}

static int Usage()
{
    Console.Error.WriteLine("usage: claudio <render|play> <in.mid> [<out.wav>] [--soundfont <path>]");
    return 1;
}

static string? TryReadOption(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}
