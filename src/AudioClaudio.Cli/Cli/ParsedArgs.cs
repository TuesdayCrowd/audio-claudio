namespace AudioClaudio.Cli.Cli;

/// <summary>
/// The result of successfully parsing and validating one command's arguments and
/// options: positionals by name, and options already converted to their declared
/// <see cref="OptionKind"/> — a handler never re-parses a raw token.
/// </summary>
public sealed class ParsedArgs
{
    private readonly IReadOnlyDictionary<string, string> _arguments;
    private readonly IReadOnlyDictionary<string, object> _options;

    public ParsedArgs(
        IReadOnlyDictionary<string, string> arguments,
        IReadOnlyDictionary<string, object> options)
    {
        _arguments = arguments;
        _options = options;
    }

    public string Argument(string name) => _arguments[name];

    public string? ArgumentOrNull(string name) =>
        _arguments.TryGetValue(name, out var value) ? value : null;

    public bool Flag(string name) =>
        _options.TryGetValue(name, out var value) && value is bool flag && flag;

    public string? String(string name) =>
        _options.TryGetValue(name, out var value) ? (string)value : null;

    public int? Int(string name) =>
        _options.TryGetValue(name, out var value) ? (int)value : null;

    public double? Double(string name) =>
        _options.TryGetValue(name, out var value) ? (double)value : null;

    public string? Path(string name) =>
        _options.TryGetValue(name, out var value) ? (string)value : null;

    public string? Enum(string name) =>
        _options.TryGetValue(name, out var value) ? (string)value : null;
}
