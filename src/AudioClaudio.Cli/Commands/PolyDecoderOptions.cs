using System.Globalization;
using AudioClaudio.Domain.Polyphony;

namespace AudioClaudio.Cli.Commands;

/// <summary>
/// Reads the polyphonic decoder-threshold flags for <c>transcribe --poly</c> into a
/// <see cref="NoteDecoderOptions"/>. Every flag defaults to Basic Pitch's stock value, so the
/// polyphonic path is unchanged unless a knob is turned (Stage 4b's honest-default rule):
/// <list type="bullet">
/// <item><c>--onset-threshold &lt;v&gt;</c> — minimum onset activation to start a note (default 0.5);
///   raise it to shed spurious note starts (precision up, recall down).</item>
/// <item><c>--frame-threshold &lt;v&gt;</c> — minimum sustained activation to keep a note (default 0.3).</item>
/// <item><c>--min-note-len &lt;frames&gt;</c> — the flicker floor in frames (default 11 ≈ 128 ms).</item>
/// </list>
/// </summary>
public static class PolyDecoderOptions
{
    public static NoteDecoderOptions FromArgs(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        NoteDecoderOptions d = NoteDecoderOptions.Default;
        return d with
        {
            OnsetThreshold = ReadDouble(args, "--onset-threshold", d.OnsetThreshold),
            FrameThreshold = ReadDouble(args, "--frame-threshold", d.FrameThreshold),
            MinNoteLenFrames = ReadInt(args, "--min-note-len", d.MinNoteLenFrames),
        };
    }

    /// <summary>Reads the same thresholds from a kernel-validated <see
    /// cref="AudioClaudio.Cli.Cli.ParsedArgs"/> (the CLI-kernel migration's replacement for the
    /// raw <c>string[]</c> overload above).</summary>
    public static NoteDecoderOptions FromArgs(AudioClaudio.Cli.Cli.ParsedArgs parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        var d = NoteDecoderOptions.Default;
        return d with
        {
            OnsetThreshold = parsed.Double("onset-threshold") ?? d.OnsetThreshold,
            FrameThreshold = parsed.Double("frame-threshold") ?? d.FrameThreshold,
            MinNoteLenFrames = parsed.Int("min-note-len") ?? d.MinNoteLenFrames,
        };
    }

    private static double ReadDouble(string[] args, string name, double fallback) =>
        ReadValue(args, name) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

    private static int ReadInt(string[] args, string name, int fallback) =>
        ReadValue(args, name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

    private static string? ReadValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
