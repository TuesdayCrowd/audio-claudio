namespace AudioClaudio.Cli.Cli;

/// <summary>
/// The value kind an option's argument must satisfy. Drives both parse-time
/// validation (S5.3) and the "expected kind" wording in error sentences and
/// generated help.
/// </summary>
public enum OptionKind
{
    /// <summary>A boolean switch; the option itself is the value (no token follows it).</summary>
    Flag,

    /// <summary>Any non-empty string, taken verbatim.</summary>
    String,

    /// <summary>A whole number, parsed invariantly.</summary>
    Int,

    /// <summary>A real number, parsed invariantly.</summary>
    Double,

    /// <summary>A filesystem path, taken verbatim — existence is a handler's concern, not the kernel's.</summary>
    Path,

    /// <summary>One of a fixed, named set of choices (<see cref="CliOption.EnumValues"/>).</summary>
    Enum,
}
