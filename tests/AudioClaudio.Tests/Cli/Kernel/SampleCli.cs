using AudioClaudio.Cli.Cli;

namespace AudioClaudio.Tests.Cli.Kernel;

/// <summary>
/// A tiny two-command "claudio" declaration shared by the CLI kernel tests in this
/// folder, so each task's test asserts only the behavior it targets.
/// </summary>
internal static class SampleCli
{
    public const string ToolName = "claudio";
    public const string ToolSummary = "a real-time piano transcriber";
    public const string Version = "0.2.1";

    public static readonly CliCommand Transcribe =
        new CliCommand("transcribe", "Transcribe an audio file to notation.")
            .WithArgument(new CliArgument("in.wav", "The audio file to transcribe."))
            .WithOption(new CliOption("--tempo", OptionKind.Double, "Declared tempo in BPM."))
            .WithOption(new CliOption("--key", OptionKind.Int, "Key signature as a fifths count.", required: true))
            .WithOption(new CliOption(
                "--model", OptionKind.String, "Which transcription engine to use.", defaultValue: "basicpitch"));

    public static readonly CliCommand Render =
        new CliCommand("render", "Render a MIDI file to a WAV file.")
            .WithArgument(new CliArgument("in.mid", "The MIDI file to render."))
            .WithArgument(new CliArgument("out.wav", "Where to write the rendered audio."))
            .WithOption(new CliOption("--soundfont", OptionKind.Path, "SoundFont (.sf2) to render with."))
            .WithExample("claudio render score.mid out.wav");

    /// <summary>Builds a fresh app with both commands registered against recording handlers, so a
    /// test can assert both the dispatch outcome and exactly what the handler received.</summary>
    public static (CommandLineApp App, List<(string Command, ParsedArgs Args)> Invocations) BuildApp(
        Func<ParsedArgs, TextWriter, TextWriter, int>? transcribeHandler = null,
        Func<ParsedArgs, TextWriter, TextWriter, int>? renderHandler = null)
    {
        var invocations = new List<(string, ParsedArgs)>();

        var app = new CommandLineApp(ToolName, ToolSummary, Version)
            .Register(Transcribe, (parsed, stdout, stderr) =>
            {
                invocations.Add(("transcribe", parsed));
                return transcribeHandler?.Invoke(parsed, stdout, stderr) ?? 0;
            })
            .Register(Render, (parsed, stdout, stderr) =>
            {
                invocations.Add(("render", parsed));
                return renderHandler?.Invoke(parsed, stdout, stderr) ?? 0;
            });

        return (app, invocations);
    }
}
