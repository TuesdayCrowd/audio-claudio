using System;
using System.IO;

namespace AudioClaudio.Cli.Composition;

/// <summary>
/// Resolves the SoundFont path: an explicit --soundfont wins; otherwise walk up from
/// the executable for the repo's fixtures (works when run via `dotnet run` from the repo).
/// A shipped exe outside the repo SHALL pass --soundfont.
/// </summary>
public static class SoundFontLocator
{
    public static string Resolve(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "fixtures", "soundfont", "GeneralUser-GS.sf2");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "SoundFont not found; pass --soundfont <path> to a .sf2 file.");
    }
}
