using System.Globalization;

namespace AudioClaudio.Cli.Commands;

/// <summary>
/// Deterministic, culture-invariant formatting for CLI report output. A percentage renders as
/// e.g. <c>"79.6%"</c> — dot decimal, no space before the sign — on every machine, never the
/// current culture's comma-decimal or <c>"%"</c>-with-space form. The <c>:P1</c> standard format
/// is culture-sensitive (invariant/many locales render <c>"100.0 %"</c> with a space; en-US renders
/// <c>"100.0%"</c>), which made the <c>evaluate</c>/<c>evaluate-audio</c> output — and any test over
/// it — differ between a US dev machine and a Linux CI runner. Formatting invariantly keeps output
/// identical everywhere (CLAUDE.md §4 determinism).
/// </summary>
internal static class CliFormat
{
    /// <summary>Formats a 0..1 fraction as a fixed-1-decimal percentage, e.g. 0.796 → "79.6%".</summary>
    public static string Percent(double fraction) =>
        (fraction * 100.0).ToString("F1", CultureInfo.InvariantCulture) + "%";
}
