namespace AudioClaudio.Cli.Cli;

/// <summary>
/// One declared command (`claudio transcribe`): its positionals, its options, and
/// a worked example. Built with a small fluent API so a command declares itself
/// once (S5.1) — <see cref="CommandLineApp"/> parses and <see cref="HelpRenderer"/>
/// renders help from exactly this declaration, nothing duplicated.
/// </summary>
public sealed class CliCommand
{
    private readonly List<CliArgument> _arguments = new();
    private readonly List<CliOption> _options = new();

    public string Name { get; }
    public string Summary { get; }
    public string? Example { get; private set; }

    public IReadOnlyList<CliArgument> Arguments => _arguments;
    public IReadOnlyList<CliOption> Options => _options;

    public CliCommand(string name, string summary)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("command name must not be empty", nameof(name));

        Name = name;
        Summary = summary;
    }

    public CliCommand WithArgument(CliArgument argument)
    {
        _arguments.Add(argument);
        return this;
    }

    public CliCommand WithOption(CliOption option)
    {
        _options.Add(option);
        return this;
    }

    public CliCommand WithExample(string example)
    {
        Example = example;
        return this;
    }
}
