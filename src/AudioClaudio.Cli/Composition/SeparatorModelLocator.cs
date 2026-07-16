using System;
using System.IO;

namespace AudioClaudio.Cli.Composition;

/// <summary>
/// Resolves the committed Spleeter source-separation model directory (the one holding the per-stem
/// ONNX exports, e.g. <c>piano.onnx</c>), by walking up from the executable for the repo fixture.
/// Mirrors <see cref="TranskunModelLocator"/>; a shipped exe outside the repo passes the directory
/// explicitly.
/// </summary>
public static class SeparatorModelLocator
{
    public static string Resolve(string? explicitDir = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDir))
        {
            return explicitDir;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "fixtures", "models", "spleeter");
            if (File.Exists(Path.Combine(candidate, "piano.onnx")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Spleeter model not found; expected fixtures/models/spleeter/piano.onnx under the repo, or pass --model <dir>.");
    }
}
