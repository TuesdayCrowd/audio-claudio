namespace AudioClaudio.Cli.Cli;

/// <summary>
/// Finds the closest candidate to an unrecognized token, for "did you mean…"
/// error sentences (S5.2). Ties break on the earliest candidate in input order,
/// so the same inputs always produce the same suggestion (determinism, CLAUDE.md §4).
/// </summary>
public static class Suggest
{
    /// <summary>The maximum edit distance still considered a plausible typo.</summary>
    public const int DefaultMaxDistance = 2;

    public static string? NearestMatch(
        string input, IEnumerable<string> candidates, int maxDistance = DefaultMaxDistance)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(candidates);

        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var candidate in candidates)
        {
            var distance = Levenshtein.Distance(input, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best is not null && bestDistance <= maxDistance ? best : null;
    }
}
