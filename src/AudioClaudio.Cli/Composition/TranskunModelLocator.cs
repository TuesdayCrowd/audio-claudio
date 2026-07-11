using System;
using System.IO;

namespace AudioClaudio.Cli.Composition;

/// <summary>
/// Resolves the committed Transkun model directory (the one holding <c>transkun.onnx</c> + the frozen
/// buffers), by walking up from the executable for the repo fixture. Mirrors <see cref="ModelLocator"/>;
/// a shipped exe outside the repo passes the directory explicitly.
/// </summary>
public static class TranskunModelLocator
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
            string candidate = Path.Combine(dir.FullName, "fixtures", "models", "transkun");
            if (File.Exists(Path.Combine(candidate, "transkun.onnx")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Transkun model not found; expected fixtures/models/transkun/transkun.onnx under the repo.");
    }
}
