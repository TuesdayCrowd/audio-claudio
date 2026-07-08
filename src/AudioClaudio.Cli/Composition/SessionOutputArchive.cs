using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AudioClaudio.Cli.Composition;

/// <summary>
/// Manages `listen`'s output scheme: the out-dir root always holds the LATEST session's files
/// (raw.mid, score.mid, score.musicxml, and --record's input.wav/recreation.wav) at stable paths,
/// and each session is additionally archived into a start-timestamped
/// <c>&lt;outDir&gt;/&lt;yyyyMMdd_HHmm&gt;/</c> subfolder. <see cref="CleanLatest"/> runs on start
/// (top-level only, so prior archive subfolders survive); <see cref="Archive"/> runs on stop,
/// after every file has been written.
/// </summary>
public static class SessionOutputArchive
{
    private static readonly string[] OutputPatterns = { "*.mid", "*.musicxml", "*.wav" };

    /// <summary>Delete the previous run's output files from the out-dir ROOT (top-level only, so
    /// existing timestamped archive subfolders are preserved), leaving the directory ready to hold
    /// just the next session's "latest" files. Returns the paths deleted.</summary>
    public static IReadOnlyList<string> CleanLatest(string outDir)
    {
        if (!Directory.Exists(outDir)) return Array.Empty<string>();
        var deleted = new List<string>();
        foreach (string pattern in OutputPatterns)
            foreach (string file in Directory.EnumerateFiles(outDir, pattern, SearchOption.TopDirectoryOnly).ToList())
            {
                File.Delete(file);
                deleted.Add(file);
            }
        return deleted;
    }

    /// <summary>Copy the current "latest" output files (out-dir root, top-level only) into
    /// <c>&lt;outDir&gt;/&lt;timestamp&gt;/</c>, archiving this session while leaving the latest files in place.
    /// Returns the archive directory path.</summary>
    public static string Archive(string outDir, string timestamp)
    {
        string archiveDir = Path.Combine(outDir, timestamp);
        Directory.CreateDirectory(archiveDir);
        foreach (string pattern in OutputPatterns)
            foreach (string file in Directory.EnumerateFiles(outDir, pattern, SearchOption.TopDirectoryOnly))
                File.Copy(file, Path.Combine(archiveDir, Path.GetFileName(file)), overwrite: true);
        return archiveDir;
    }
}
