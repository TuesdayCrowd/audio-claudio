using System.Text;
using AudioClaudio.Cli;
using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

/// <summary>
/// Pins the app-level error-path contract (S5.2/S5.3/S5.5) through the real, fully-wired
/// <see cref="AppBuilder"/> app — unknown command, unknown flag, malformed option value, and a
/// missing input file all read as a one-line sentence with the right exit code, never a stack
/// trace. If any assertion here goes red, the bug is in the kernel/handler, never in loosening
/// this test.
/// </summary>
public class ErrorPathTests
{
    private static (int Code, string Out, string Err) RunCaptured(string[] args)
    {
        var app = AppBuilder.Build(new StringBuilder(), noColor: true);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = app.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Unknown_command_suggests_the_nearest_match_and_exits_nonzero()
    {
        var (code, _, err) = RunCaptured(new[] { "trascribe", "song.wav" });

        Assert.Equal(CommandLineApp.UsageErrorExitCode, code);
        Assert.Contains("unknown command", err);
        Assert.Contains("transcribe", err);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Unknown_flag_suggests_the_nearest_match_and_exits_nonzero()
    {
        var (code, _, err) = RunCaptured(new[] { "transcribe", "song.wav", "--modl", "transkun" });

        Assert.Equal(CommandLineApp.UsageErrorExitCode, code);
        Assert.Contains("--model", err);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Malformed_tempo_names_the_flag_expected_kind_and_offending_token()
    {
        var (code, _, err) = RunCaptured(new[] { "transcribe", "song.wav", "--tempo", "abc" });

        Assert.Equal(CommandLineApp.UsageErrorExitCode, code);
        Assert.Contains("--tempo", err);
        Assert.Contains("abc", err);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Missing_input_file_reads_as_a_sentence_not_a_stack_trace()
    {
        var (code, _, err) = RunCaptured(new[] { "transcribe", "no-such-file.wav", "--mono" });

        Assert.Equal(1, code);
        Assert.Contains("input file 'no-such-file.wav' not found", err);
        Assert.DoesNotContain("at AudioClaudio", err);
    }
}
