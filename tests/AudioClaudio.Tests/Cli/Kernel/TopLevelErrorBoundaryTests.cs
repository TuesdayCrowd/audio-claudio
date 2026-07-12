using AudioClaudio.Cli;
using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class TopLevelErrorBoundaryTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void An_unexpected_exception_prints_a_friendly_sentence_not_a_stack_trace()
    {
        var stderr = new StringWriter();
        var styler = new AnsiStyler(interactiveTerminal: false, noColorEnvSet: false, noColorFlag: false);

        int code = TopLevelErrorBoundary.Run(
            () => throw new InvalidOperationException("the ONNX session failed to load"),
            stderr, styler, debug: false);

        Assert.Equal(1, code);
        Assert.Equal(
            "error: unexpected error: the ONNX session failed to load (run with --debug for the stack trace)\n",
            stderr.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Debug_flag_prints_the_full_stack_trace_instead()
    {
        var stderr = new StringWriter();
        var styler = new AnsiStyler(interactiveTerminal: false, noColorEnvSet: false, noColorFlag: false);

        int code = TopLevelErrorBoundary.Run(
            () => throw new InvalidOperationException("boom"), stderr, styler, debug: true);

        Assert.Equal(1, code);
        Assert.Contains("InvalidOperationException", stderr.ToString());
        Assert.Contains("boom", stderr.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void A_clean_run_passes_the_inner_exit_code_through_unchanged()
    {
        var stderr = new StringWriter();
        var styler = new AnsiStyler(interactiveTerminal: false, noColorEnvSet: false, noColorFlag: false);

        int code = TopLevelErrorBoundary.Run(() => 42, stderr, styler, debug: false);

        Assert.Equal(42, code);
        Assert.Equal(string.Empty, stderr.ToString());
    }
}
