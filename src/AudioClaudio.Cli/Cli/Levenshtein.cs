namespace AudioClaudio.Cli.Cli;

/// <summary>
/// Edit distance between two strings (single-character insert/delete/substitute),
/// the metric behind "did you mean…" suggestions (S5.2).
/// </summary>
public static class Levenshtein
{
    public static int Distance(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var lenA = a.Length;
        var lenB = b.Length;
        var previous = new int[lenB + 1];
        var current = new int[lenB + 1];

        for (var j = 0; j <= lenB; j++)
            previous[j] = j;

        for (var i = 1; i <= lenA; i++)
        {
            current[0] = i;
            for (var j = 1; j <= lenB; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[lenB];
    }
}
