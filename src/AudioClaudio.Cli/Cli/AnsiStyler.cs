namespace AudioClaudio.Cli.Cli;

/// <summary>
/// Applies tasteful ANSI color to headings, error keywords, and offending tokens —
/// and nothing else. Disabled (styling becomes a no-op) on a non-interactive stream,
/// when NO_COLOR is set, or when the caller passes --no-color (S5.6). The three
/// inputs are constructor parameters, not sensed internally, so every combination
/// is directly unit-testable.
/// </summary>
public sealed class AnsiStyler
{
    private const string Reset = "\u001b[0m";

    public bool Enabled { get; }

    public AnsiStyler(bool interactiveTerminal, bool noColorEnvSet, bool noColorFlag)
    {
        Enabled = interactiveTerminal && !noColorEnvSet && !noColorFlag;
    }

    /// <summary>Reads the ambient environment (console redirection, NO_COLOR) for real usage.</summary>
    public static AnsiStyler FromEnvironment(TextWriter output, bool noColorFlag)
    {
        var isConsoleOut = ReferenceEquals(output, Console.Out);
        var isConsoleError = ReferenceEquals(output, Console.Error);
        var interactiveTerminal = isConsoleOut
            ? !Console.IsOutputRedirected
            : isConsoleError && !Console.IsErrorRedirected;

        return new AnsiStyler(
            interactiveTerminal,
            noColorEnvSet: !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")),
            noColorFlag: noColorFlag);
    }

    public string Heading(string text) => Style(text, "\u001b[1;36m");
    public string Error(string text) => Style(text, "\u001b[1;31m");
    public string Token(string text) => Style(text, "\u001b[33m");

    private string Style(string text, string code) => Enabled ? $"{code}{text}{Reset}" : text;
}
