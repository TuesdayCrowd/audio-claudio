namespace AudioClaudio.Cli.Cli;

/// <summary>One declared positional argument of a <see cref="CliCommand"/> (e.g. `&lt;in.wav&gt;`).</summary>
public sealed class CliArgument
{
    public string Name { get; }
    public string Description { get; }
    public bool Required { get; }

    public CliArgument(string name, string description, bool required = true)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("argument name must not be empty", nameof(name));

        Name = name;
        Description = description;
        Required = required;
    }

    /// <summary>The bracketed rendering used in usage lines and help (`&lt;in.wav&gt;` or `[in.wav]`).</summary>
    public string UsageToken => Required ? $"<{Name}>" : $"[{Name}]";
}
