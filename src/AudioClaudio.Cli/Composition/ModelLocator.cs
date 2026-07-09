using System;
using System.IO;

namespace AudioClaudio.Cli.Composition;

/// <summary>
/// Resolves the Basic Pitch ONNX model path: an explicit --model wins; otherwise walk up from the
/// executable for the repo's committed fixture (works when run via `dotnet run` from the repo).
/// Mirrors <see cref="SoundFontLocator"/>; a shipped exe outside the repo SHALL pass --model.
/// </summary>
public static class ModelLocator
{
    public static string Resolve(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "fixtures", "models", "basic-pitch-nmp.onnx");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Basic Pitch model not found; pass --model <path> to basic-pitch-nmp.onnx.");
    }
}
