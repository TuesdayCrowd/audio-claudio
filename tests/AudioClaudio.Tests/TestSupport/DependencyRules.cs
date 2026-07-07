namespace AudioClaudio.Tests.TestSupport;

/// <summary>
/// Classifies referenced-assembly names against the §3 dependency rule for the
/// Domain layer: Domain may reference nothing but the BCL — no sibling project,
/// no audio/MIDI/DSP library.
/// </summary>
public static class DependencyRules
{
    /// <summary>Assemblies the Domain layer must never reference.</summary>
    public static readonly string[] Forbidden =
    [
        "AudioClaudio.Application",
        "AudioClaudio.Infrastructure",
        "AudioClaudio.Cli",
        "Melanchall.DryWetMidi",
        "MeltySynth",
        "PortAudioSharp",
        "PortAudioSharp2",
        "NWaves",
        "NAudio",
        "Microsoft.ML.OnnxRuntime",
    ];

    public static bool IsForbidden(string assemblyName)
    {
        var isSiblingProject =
            assemblyName.StartsWith("AudioClaudio.", StringComparison.Ordinal)
            && !assemblyName.Equals("AudioClaudio.Domain", StringComparison.Ordinal);

        var isDenylisted = Forbidden.Any(
            f => string.Equals(f, assemblyName, StringComparison.OrdinalIgnoreCase));

        return isSiblingProject || isDenylisted;
    }
}
