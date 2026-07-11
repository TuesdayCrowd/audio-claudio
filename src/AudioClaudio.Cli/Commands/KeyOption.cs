using System.Globalization;

namespace AudioClaudio.Cli.Commands;

/// <summary>
/// Parses and validates the <c>--key</c> override into a key signature in fifths. Extracted for
/// testability (v2 Stage 3b review): a nonsensical or out-of-range value must fail cleanly with a
/// message, never crash the enharmonic speller (<c>Math.Abs(int.MinValue)</c>) or silently emit a
/// garbage <c>&lt;fifths&gt;</c>. The valid range is the twelve standard key signatures, −7 (C♭ major)
/// through +7 (C♯ major).
/// </summary>
public static class KeyOption
{
    public const int MinFifths = -7;
    public const int MaxFifths = 7;

    /// <summary>Parse a raw <c>--key</c> value. Returns false with <paramref name="error"/> set when the
    /// value is not an integer in [−7, +7].</summary>
    public static bool TryParse(string? raw, out int fifths, out string? error)
    {
        error = null;
        if (!int.TryParse(raw, NumberStyles.Integer | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out fifths)
            || fifths is < MinFifths or > MaxFifths)
        {
            fifths = 0;
            error = $"--key must be an integer from {MinFifths} (C-flat major) to +{MaxFifths} (C-sharp major); got '{raw}'.";
            return false;
        }

        return true;
    }
}
