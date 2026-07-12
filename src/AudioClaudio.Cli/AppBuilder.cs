using System.Text;
using AudioClaudio.Cli.Cli;

namespace AudioClaudio.Cli;

/// <summary>
/// The composition root for the CLI-kernel migration (v2 Stage 5): builds the
/// <see cref="CommandLineApp"/> and registers all seven commands with their full option
/// surface. Handlers are wired one command at a time by later tasks (14–21); <see
/// cref="Program"/>'s top-level statements do nothing but call <see cref="Build"/>, wrap
/// <c>Run</c> in the top-level try/catch (Task 14), and return its exit code — every other
/// line of app behavior lives here so it is unit-testable in-process.
/// </summary>
public static class AppBuilder
{
    public static string Version { get; } =
        typeof(AppBuilder).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Builds one <see cref="AnsiStyler"/> for handler-printed messages (e.g. a styled
    /// "error:" prefix on a missing-file sentence), computed the same way
    /// <see cref="AnsiStyler.FromEnvironment"/> computes it internally for the kernel's own
    /// help/error rendering — so a handler's own color decisions never disagree with the
    /// kernel's (S5.6). Kept internal so Tasks 15+ can reuse it without recomputing.
    /// </summary>
    internal static AnsiStyler ConsoleStyler(bool noColor) =>
        new(
            interactiveTerminal: !Console.IsOutputRedirected,
            noColorEnvSet: !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")),
            noColorFlag: noColor);

    public static CommandLineApp Build(StringBuilder logBuffer, bool noColor)
    {
        ArgumentNullException.ThrowIfNull(logBuffer);
        var styler = ConsoleStyler(noColor);
        var app = new CommandLineApp("claudio", "a real-time piano transcriber", Version);

        var transcribe = new CliCommand("transcribe", "Transcribe a WAV recording to MIDI + MusicXML.")
            .WithArgument(new CliArgument("input.wav", "the recording to transcribe"))
            .WithOption(new CliOption("--tempo", OptionKind.Double, "declared tempo in BPM (auto-estimated if omitted)"))
            .WithOption(new CliOption("--out-dir", OptionKind.Path, "directory to write raw.mid/score.mid/score.musicxml", defaultValue: "."))
            .WithOption(new CliOption("--note-names", OptionKind.Flag, "print a scientific-pitch-name lyric under each note"))
            .WithOption(new CliOption("--mono", OptionKind.Flag, "use the monophonic YIN pipeline instead of the polyphonic default"))
            .WithOption(new CliOption("--model", OptionKind.String, "explicit model path, or 'transkun' for the Transkun engine"))
            .WithOption(new CliOption("--key", OptionKind.Int, "override the auto-detected key signature (sharps +, flats -)"))
            .WithOption(new CliOption("--onset-threshold", OptionKind.Double, "polyphonic onset activation threshold"))
            .WithOption(new CliOption("--frame-threshold", OptionKind.Double, "polyphonic sustained-frame activation threshold"))
            .WithOption(new CliOption("--min-note-len", OptionKind.Int, "polyphonic flicker floor in frames"))
            .WithOption(new CliOption("--legato", OptionKind.Flag, "(--mono) opt into legato note recovery"))
            .WithOption(new CliOption("--coarse-rhythm", OptionKind.Flag, "(--mono) floor note values at an eighth"))
            .WithOption(new CliOption("--triplets", OptionKind.Flag, "engrave eighth-note triplets"))
            .WithExample("claudio transcribe song.wav --out-dir out");
        app.Register(transcribe, (p, stdout, stderr) => throw new NotImplementedException("wired in Task 20"));

        var notate = new CliCommand("notate", "Engrave an existing MIDI file as a grand-staff score.")
            .WithArgument(new CliArgument("input.mid", "the MIDI file to notate"))
            .WithOption(new CliOption("--out-dir", OptionKind.Path, "directory to write score.mid/score.musicxml", defaultValue: "."))
            .WithOption(new CliOption("--tempo", OptionKind.Double, "declared tempo in BPM (auto-estimated if omitted)"))
            .WithOption(new CliOption("--key", OptionKind.Int, "override the auto-detected key signature"))
            .WithOption(new CliOption("--note-names", OptionKind.Flag, "print a scientific-pitch-name lyric under each note"))
            .WithOption(new CliOption("--triplets", OptionKind.Flag, "engrave eighth-note triplets"))
            .WithExample("claudio notate performance.mid --out-dir out");
        app.Register(notate, (p, stdout, stderr) => throw new NotImplementedException("wired in Task 19"));

        var render = new CliCommand("render", "Render a MIDI file to a deterministic WAV.")
            .WithArgument(new CliArgument("input.mid", "the MIDI file to render"))
            .WithArgument(new CliArgument("output.wav", "the WAV file to write"))
            .WithOption(new CliOption("--soundfont", OptionKind.Path, "explicit SoundFont path (auto-discovered otherwise)"))
            .WithExample("claudio render song.mid song.wav");
        app.Register(render, (p, stdout, stderr) => throw new NotImplementedException("wired in Task 15"));

        var play = new CliCommand("play", "Play a MIDI file through the default audio device.")
            .WithArgument(new CliArgument("input.mid", "the MIDI file to play"))
            .WithOption(new CliOption("--soundfont", OptionKind.Path, "explicit SoundFont path (auto-discovered otherwise)"))
            .WithExample("claudio play song.mid");
        app.Register(play, (p, stdout, stderr) => throw new NotImplementedException("wired in Task 15"));

        var evaluate = new CliCommand("evaluate", "Score a candidate transcription against a reference note-set.")
            .WithArgument(new CliArgument("candidate.mid", "the transcription to evaluate"))
            .WithArgument(new CliArgument("reference.mid", "the ground-truth reference"))
            .WithOption(new CliOption("--onset-tolerance-ms", OptionKind.Double, "onset matching tolerance in ms (default 50)"))
            .WithOption(new CliOption("--align", OptionKind.Flag, "cancel a global tempo ratio before scoring"))
            .WithOption(new CliOption("--warp", OptionKind.Flag, "DTW-warp to also remove local rubato (wins over --align)"))
            .WithExample("claudio evaluate out/score.mid reference.mid --align");
        app.Register(evaluate, (p, stdout, stderr) => throw new NotImplementedException("wired in Task 16"));

        var evaluateAudio = new CliCommand("evaluate-audio", "Compare two WAVs by pitch-content (chroma) similarity.")
            .WithArgument(new CliArgument("original.wav", "the original recording"))
            .WithArgument(new CliArgument("reproduction.wav", "the re-synthesized recording"))
            .WithExample("claudio evaluate-audio input.wav recreation.wav");
        app.Register(evaluateAudio, (p, stdout, stderr) => throw new NotImplementedException("wired in Task 17"));

        var listen = new CliCommand("listen", "Transcribe live from the microphone.")
            .WithOption(new CliOption("--tempo", OptionKind.Double, "declared tempo in BPM (auto-estimated if omitted)"))
            .WithOption(new CliOption("--out-dir", OptionKind.Path, "directory to write raw.mid/score.mid/score.musicxml", defaultValue: "."))
            .WithOption(new CliOption("--view", OptionKind.Flag, "open a live sheet-music browser view"))
            .WithOption(new CliOption("--record", OptionKind.Flag, "also write input.wav + recreation.wav"))
            .WithOption(new CliOption("--skip-silence", OptionKind.Flag, "collapse pauses > 500ms (implies --record)"))
            .WithOption(new CliOption("--note-names", OptionKind.Flag, "print a scientific-pitch-name lyric under each note"))
            .WithOption(new CliOption("--soundfont", OptionKind.Path, "explicit SoundFont for the --record recreation (auto-discovered otherwise)"))
            .WithExample("claudio listen --view --record");
        app.Register(listen, (p, stdout, stderr) => throw new NotImplementedException("wired in Task 21"));

        return app;
    }
}
