using System.Globalization;

namespace AudioClaudio.Domain;

/// <summary>
/// A time signature such as 4/4. The MVP uses 4/4 only; the type is written a
/// little more generally (any positive numerator, any power-of-two denominator)
/// so the grid math has a single honest source for beats-per-measure.
/// </summary>
public readonly record struct TimeSignature
{
    /// <summary>Numerator — beats in one measure.</summary>
    public int BeatsPerMeasure { get; }

    /// <summary>Denominator — the note value that gets one beat (4 = quarter).</summary>
    public int BeatUnit { get; }

    public TimeSignature(int beatsPerMeasure, int beatUnit)
    {
        if (beatsPerMeasure <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(beatsPerMeasure), beatsPerMeasure, "Numerator (beats per measure) must be positive.");
        }

        if (beatUnit <= 0 || (beatUnit & (beatUnit - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(beatUnit), beatUnit, "Denominator (beat unit) must be a positive power of two.");
        }

        BeatsPerMeasure = beatsPerMeasure;
        BeatUnit = beatUnit;
    }

    /// <summary>The MVP time signature, 4/4.</summary>
    public static TimeSignature FourFour => new(4, 4);

    private const string FormatErrorMessage =
        "time signature must look like 4/4 or 6/8 (denominator a power of two)";

    /// <summary>
    /// Parses a "N/D" time signature (e.g. "3/4", "6/8") declared on the command line or from a
    /// web-view form field. Surrounding and interior whitespace around the numerator/denominator is
    /// tolerated ("4 / 4" parses the same as "4/4"). On success, <paramref name="result"/> is the
    /// parsed <see cref="TimeSignature"/> and <paramref name="error"/> is null; on failure,
    /// <paramref name="result"/> is <c>default</c> and <paramref name="error"/> is a friendly
    /// message suitable for printing directly to the user. The actual range/power-of-two validation
    /// is delegated to the constructor (never duplicated here) so the two never disagree.
    /// </summary>
    public static bool TryParse(string? text, out TimeSignature result, out string? error)
    {
        result = default;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = FormatErrorMessage;
            return false;
        }

        int slash = text.IndexOf('/');
        if (slash < 0)
        {
            error = FormatErrorMessage;
            return false;
        }

        string numeratorText = text[..slash].Trim();
        string denominatorText = text[(slash + 1)..].Trim();

        if (!int.TryParse(numeratorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int beatsPerMeasure) ||
            !int.TryParse(denominatorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int beatUnit))
        {
            error = FormatErrorMessage;
            return false;
        }

        try
        {
            result = new TimeSignature(beatsPerMeasure, beatUnit);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            error = FormatErrorMessage;
            return false;
        }
    }
}
