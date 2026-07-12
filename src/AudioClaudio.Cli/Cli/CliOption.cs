namespace AudioClaudio.Cli.Cli;

/// <summary>
/// One declared option (`--name`) of a <see cref="CliCommand"/>: its value kind,
/// its help text, and whether it is required. A command's options are the single
/// source of truth for both parsing/validation and generated help (S5.1).
/// </summary>
public sealed class CliOption
{
    public string Name { get; }
    public OptionKind Kind { get; }
    public string Description { get; }
    public bool Required { get; }
    public string? DefaultValue { get; }
    public IReadOnlyList<string> EnumValues { get; }

    public CliOption(
        string name,
        OptionKind kind,
        string description,
        bool required = false,
        string? defaultValue = null,
        IReadOnlyList<string>? enumValues = null)
    {
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"option name must start with '--', got '{name}'", nameof(name));
        if (kind == OptionKind.Enum && (enumValues is null || enumValues.Count == 0))
            throw new ArgumentException("an Enum-kind option requires at least one enum value", nameof(enumValues));

        Name = name;
        Kind = kind;
        Description = description;
        Required = required;
        DefaultValue = defaultValue;
        EnumValues = enumValues ?? Array.Empty<string>();
    }

    /// <summary>The option name without its leading "--", used as the lookup key in <see cref="ParsedArgs"/>.</summary>
    public string Key => Name[2..];

    /// <summary>The expected-value wording used in error sentences and help ("a number", "one of: a, b").</summary>
    public string KindDescription => Kind switch
    {
        OptionKind.Flag => "a flag",
        OptionKind.String => "a value",
        OptionKind.Int => "a whole number",
        OptionKind.Double => "a number",
        OptionKind.Path => "a file path",
        OptionKind.Enum => $"one of: {string.Join(", ", EnumValues)}",
        _ => "a value",
    };
}
