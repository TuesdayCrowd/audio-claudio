using System.Linq;
using AudioClaudio.Cli.Cli;
using AudioClaudio.Cli.Commands;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class ParsedArgsAdaptersTests
{
    private static ParsedArgs Capture(CliCommand cmd, string[] args)
    {
        ParsedArgs? captured = null;
        var app = new CommandLineApp("test-tool", "x", "0.0.0");
        app.Register(cmd, (p, stdout, stderr) => { captured = p; return 0; });
        // CommandLineApp.Run dispatches on args[0] as the command name (see
        // CommandLineAppDispatchTests); the command's own arguments/options follow it.
        var fullArgs = new[] { cmd.Name }.Concat(args).ToArray();
        int code = app.Run(fullArgs, new System.IO.StringWriter(), new System.IO.StringWriter());
        Assert.Equal(0, code);
        return captured!;
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void TranscribeModeResolver_resolves_mono_from_ParsedArgs()
    {
        var cmd = new CliCommand("transcribe", "x")
            .WithArgument(new CliArgument("input.wav", "x"))
            .WithOption(new CliOption("--mono", OptionKind.Flag, "x"));

        var parsed = Capture(cmd, new[] { "song.wav", "--mono" });

        Assert.Equal(TranscribeMode.Monophonic, TranscribeModeResolver.Resolve(parsed));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void TranscribeModeResolver_defaults_to_polyphonic_from_ParsedArgs()
    {
        var cmd = new CliCommand("transcribe", "x")
            .WithArgument(new CliArgument("input.wav", "x"))
            .WithOption(new CliOption("--mono", OptionKind.Flag, "x"));

        var parsed = Capture(cmd, new[] { "song.wav" });

        Assert.Equal(TranscribeMode.Polyphonic, TranscribeModeResolver.Resolve(parsed));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void PolyDecoderOptions_reads_thresholds_from_ParsedArgs()
    {
        var cmd = new CliCommand("transcribe", "x")
            .WithArgument(new CliArgument("input.wav", "x"))
            .WithOption(new CliOption("--onset-threshold", OptionKind.Double, "x"))
            .WithOption(new CliOption("--frame-threshold", OptionKind.Double, "x"))
            .WithOption(new CliOption("--min-note-len", OptionKind.Int, "x"));

        var parsed = Capture(cmd, new[] { "song.wav", "--onset-threshold", "0.7", "--min-note-len", "5" });
        var o = PolyDecoderOptions.FromArgs(parsed);

        Assert.Equal(0.7, o.OnsetThreshold);
        Assert.Equal(5, o.MinNoteLenFrames);
        Assert.Equal(AudioClaudio.Domain.Polyphony.NoteDecoderOptions.Default.FrameThreshold, o.FrameThreshold);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void KeyOption_Validate_rejects_an_out_of_range_fifths()
    {
        Assert.True(KeyOption.Validate(null, out string? noError));
        Assert.Null(noError);

        Assert.False(KeyOption.Validate(8, out string? error));
        Assert.Contains("--key", error);
    }
}
