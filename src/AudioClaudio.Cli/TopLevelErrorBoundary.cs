using AudioClaudio.Cli.Cli;

namespace AudioClaudio.Cli;

/// <summary>
/// S5.5: the single place an unhandled exception becomes a sentence instead of a raw
/// .NET stack trace. Wraps <see cref="CommandLineApp.Run"/> (or any other exit-code-
/// producing delegate) so no user-reachable path in the packaged binary ever prints a
/// stack trace unless the user explicitly asked for one via --debug.
/// </summary>
public static class TopLevelErrorBoundary
{
    public const int UnexpectedErrorExitCode = 1;

    public static int Run(Func<int> body, TextWriter stderr, AnsiStyler styler, bool debug)
    {
        try
        {
            return body();
        }
        catch (Exception ex)
        {
            stderr.Write(debug
                ? $"{styler.Error("error:")} unexpected error:\n{ex}\n"
                : $"{styler.Error("error:")} unexpected error: {ex.Message} (run with --debug for the stack trace)\n");
            return UnexpectedErrorExitCode;
        }
    }
}
