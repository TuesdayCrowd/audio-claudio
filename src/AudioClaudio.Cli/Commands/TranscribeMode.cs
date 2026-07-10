namespace AudioClaudio.Cli.Commands;

/// <summary>Which engine <c>transcribe</c> runs.</summary>
public enum TranscribeMode
{
    /// <summary>The monophonic YIN pipeline — one note at a time, closed-loop-proven. Opt in with <c>--mono</c>.</summary>
    Monophonic,

    /// <summary>The polyphonic Basic Pitch engine — chords and two hands. The default as of v0.2.0.</summary>
    Polyphonic,
}

/// <summary>
/// Resolves the <c>transcribe</c> engine from the command line. Polyphony is the default; <c>--mono</c>
/// opts back into the monophonic pipeline and always wins (it is the explicit opt-out), while
/// <c>--poly</c> names the default explicitly.
/// </summary>
public static class TranscribeModeResolver
{
    public static TranscribeMode Resolve(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return System.Array.IndexOf(args, "--mono") >= 0 ? TranscribeMode.Monophonic : TranscribeMode.Polyphonic;
    }
}
