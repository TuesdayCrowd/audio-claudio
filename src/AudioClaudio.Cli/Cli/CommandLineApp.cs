using System.Globalization;

namespace AudioClaudio.Cli.Cli;

/// <summary>
/// The composition root for the hand-rolled CLI kernel: commands register
/// themselves once (name, arguments, options, handler) via <see cref="Register"/>;
/// <see cref="Run"/> parses, validates, and dispatches against exactly that
/// declaration — the same one <see cref="HelpRenderer"/> reads — so help can never
/// disagree with what the parser accepts (S5.1).
/// </summary>
public sealed class CommandLineApp
{
    /// <summary>Exit code for any usage error: unknown command/flag, a bad value, a missing argument.</summary>
    public const int UsageErrorExitCode = 64;

    private const string NoColorFlag = "--no-color";

    private readonly List<CliCommand> _commands = new();
    private readonly Dictionary<string, Func<ParsedArgs, TextWriter, TextWriter, int>> _handlers =
        new(StringComparer.Ordinal);

    public string ToolName { get; }
    public string ToolSummary { get; }
    public string Version { get; }

    public IReadOnlyList<CliCommand> Commands => _commands;

    public CommandLineApp(string toolName, string toolSummary, string version)
    {
        ToolName = toolName;
        ToolSummary = toolSummary;
        Version = version;
    }

    /// <summary>Declares one command and the handler that runs when it is dispatched.</summary>
    public CommandLineApp Register(CliCommand command, Func<ParsedArgs, TextWriter, TextWriter, int> handler)
    {
        _commands.Add(command);
        _handlers[command.Name] = handler;
        return this;
    }

    /// <summary>
    /// Parses <paramref name="args"/>, validates them against the matched command's
    /// declaration, and dispatches to its handler. Never throws on user input — every
    /// failure is a stderr sentence plus <see cref="UsageErrorExitCode"/>.
    /// </summary>
    public int Run(string[] args, TextWriter stdout, TextWriter stderr, AnsiStyler? styler = null)
    {
        var noColorFlag = args.Contains(NoColorFlag);
        var filtered = args.Where(a => a != NoColorFlag).ToArray();
        styler ??= AnsiStyler.FromEnvironment(stdout, noColorFlag);

        if (filtered.Length == 0 || filtered[0] is "--help" or "-h")
        {
            stdout.Write(HelpRenderer.RenderTopLevel(ToolName, ToolSummary, _commands, styler));
            return 0;
        }

        if (filtered[0] == "--version")
        {
            stdout.Write($"{ToolName} {Version}\n");
            return 0;
        }

        var commandName = filtered[0];
        var command = _commands.Find(c => c.Name == commandName);
        if (command is null)
        {
            stderr.Write($"{styler.Error("error")}: {UnknownToken("command", commandName, _commands.Select(c => c.Name))}\n");
            return UsageErrorExitCode;
        }

        var rest = filtered[1..];
        if (rest.Length > 0 && rest[0] is "--help" or "-h")
        {
            stdout.Write(HelpRenderer.RenderCommand(ToolName, command, styler));
            return 0;
        }

        var (parsed, error) = Validate(command, rest);
        if (error is not null)
        {
            stderr.Write($"{styler.Error("error")}: {error}\n");
            return UsageErrorExitCode;
        }

        return _handlers[command.Name](parsed!, stdout, stderr);
    }

    /// <summary>
    /// Parses and kind-validates one command's tokens against its declaration.
    /// Exposed directly so validation can be unit-tested without a handler or the
    /// rest of <see cref="Run"/>.
    /// </summary>
    public (ParsedArgs? Args, string? Error) Validate(CliCommand command, string[] tokens)
    {
        var options = new Dictionary<string, object>();
        var positionals = new List<string>();

        var i = 0;
        while (i < tokens.Length)
        {
            var token = tokens[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(token);
                i++;
                continue;
            }

            var option = command.Options.FirstOrDefault(o => o.Name == token);
            if (option is null)
                return (null, UnknownToken("option", token, command.Options.Select(o => o.Name)));

            if (option.Kind == OptionKind.Flag)
            {
                options[option.Key] = true;
                i++;
                continue;
            }

            if (i + 1 >= tokens.Length)
                return (null, $"{option.Name} expects a value.");

            var (value, kindError) = Convert(option, tokens[i + 1]);
            if (kindError is not null)
                return (null, kindError);

            options[option.Key] = value!;
            i += 2;
        }

        // Required positionals are assumed contiguous at the front of a command's declaration.
        var requiredArgumentCount = command.Arguments.Count(a => a.Required);
        if (positionals.Count < requiredArgumentCount)
            return (null, $"missing required argument {command.Arguments[positionals.Count].UsageToken}.");

        if (positionals.Count > command.Arguments.Count)
            return (null, $"unexpected argument '{positionals[command.Arguments.Count]}'.");

        foreach (var option in command.Options)
        {
            if (options.ContainsKey(option.Key))
                continue;

            if (option.Required)
                return (null, $"{option.Name} is required.");

            if (option.DefaultValue is not null)
            {
                var (value, kindError) = Convert(option, option.DefaultValue);
                if (kindError is not null)
                    throw new InvalidOperationException(
                        $"{option.Name}'s declared default '{option.DefaultValue}' fails its own kind check: {kindError}");
                options[option.Key] = value!;
            }
        }

        var arguments = new Dictionary<string, string>();
        for (var a = 0; a < positionals.Count; a++)
            arguments[command.Arguments[a].Name] = positionals[a];

        return (new ParsedArgs(arguments, options), null);
    }

    private static (object? Value, string? Error) Convert(CliOption option, string raw)
    {
        switch (option.Kind)
        {
            case OptionKind.String:
            case OptionKind.Path:
                return (raw, null);

            case OptionKind.Int:
                return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                    ? (i, null)
                    : (null, $"{option.Name} expects {option.KindDescription}, got '{raw}'.");

            case OptionKind.Double:
                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? (d, null)
                    : (null, $"{option.Name} expects {option.KindDescription}, got '{raw}'.");

            case OptionKind.Enum:
                var match = option.EnumValues.FirstOrDefault(
                    v => string.Equals(v, raw, StringComparison.OrdinalIgnoreCase));
                return match is not null
                    ? (match, null)
                    : (null, $"{option.Name} expects {option.KindDescription}, got '{raw}'.");

            default:
                throw new InvalidOperationException($"unhandled option kind {option.Kind}");
        }
    }

    private static string UnknownToken(string kind, string token, IEnumerable<string> candidates)
    {
        var suggestion = Suggest.NearestMatch(token, candidates);
        return suggestion is null
            ? $"unknown {kind} '{token}'."
            : $"unknown {kind} '{token}'. Did you mean '{suggestion}'?";
    }
}
