using System.Text;

namespace AudioClaudio.Cli.Cli;

/// <summary>
/// Renders top-level and per-command help directly from a <see cref="CliCommand"/>'s
/// declaration — the same one <see cref="CommandLineApp"/> parses against — so help
/// can never disagree with what is actually accepted (S5.1). Lines are joined with a
/// literal "\n" (never <see cref="Environment.NewLine"/>) so golden fixtures are
/// byte-identical on every OS.
/// </summary>
public static class HelpRenderer
{
    public static string RenderTopLevel(
        string toolName, string toolSummary, IReadOnlyList<CliCommand> commands, AnsiStyler styler)
    {
        var sb = new StringBuilder();
        Line(sb, $"{toolName} — {toolSummary}");
        Line(sb, string.Empty);
        Line(sb, styler.Heading("Usage:"));
        Line(sb, $"  {toolName} <command> [options]");
        Line(sb, string.Empty);
        Line(sb, styler.Heading("Commands:"));

        var width = commands.Count == 0 ? 0 : commands.Max(c => c.Name.Length);
        foreach (var command in commands)
            Line(sb, $"  {command.Name.PadRight(width)}   {command.Summary}");

        Line(sb, string.Empty);
        Line(sb, $"Run '{toolName} <command> --help' for details on a command.");

        return sb.ToString();
    }

    public static string RenderCommand(string toolName, CliCommand command, AnsiStyler styler)
    {
        var sb = new StringBuilder();
        Line(sb, styler.Heading("Usage:"));

        var usage = new StringBuilder($"  {toolName} {command.Name}");
        foreach (var argument in command.Arguments)
            usage.Append(' ').Append(argument.UsageToken);
        if (command.Options.Count > 0)
            usage.Append(" [options]");
        Line(sb, usage.ToString());

        if (command.Arguments.Count > 0)
        {
            Line(sb, string.Empty);
            Line(sb, styler.Heading("Arguments:"));
            var width = command.Arguments.Max(a => a.UsageToken.Length);
            foreach (var argument in command.Arguments)
                Line(sb, $"  {argument.UsageToken.PadRight(width)}   {argument.Description}");
        }

        if (command.Options.Count > 0)
        {
            Line(sb, string.Empty);
            Line(sb, styler.Heading("Options:"));
            var labels = command.Options.Select(OptionLabel).ToList();
            var width = labels.Max(l => l.Length);
            for (var i = 0; i < command.Options.Count; i++)
            {
                var option = command.Options[i];
                var suffix = option.Required
                    ? " (required)"
                    : option.DefaultValue is null ? string.Empty : $" (default: {option.DefaultValue})";
                Line(sb, $"  {labels[i].PadRight(width)}   {option.Description}{suffix}");
            }
        }

        if (command.Example is not null)
        {
            Line(sb, string.Empty);
            Line(sb, styler.Heading("Example:"));
            Line(sb, $"  {command.Example}");
        }

        return sb.ToString();
    }

    private static string OptionLabel(CliOption option) => option.Kind switch
    {
        OptionKind.Flag => option.Name,
        OptionKind.Enum => $"{option.Name} <{string.Join("|", option.EnumValues)}>",
        _ => $"{option.Name} <{option.Kind.ToString().ToUpperInvariant()}>",
    };

    private static void Line(StringBuilder sb, string text) => sb.Append(text).Append('\n');
}
