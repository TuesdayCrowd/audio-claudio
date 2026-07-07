using System;
using System.IO;
using AudioClaudio.Tests.TestSupport; // shared RepoPaths locator (Step 0) — do not re-derive the repo root

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>
/// Locates committed fixtures and the default quarantine directory for the closed-loop suite. It
/// reuses the shared <see cref="RepoPaths"/> locator from Step 0 rather than re-implementing the
/// walk up to <c>AudioClaudio.sln</c> (one locator, not five) — <see cref="SoundFontPath"/> simply
/// forwards to <see cref="RepoPaths.SoundFontPath"/> rather than re-globbing for a <c>*.sf2</c>.
/// </summary>
public static class Fixtures
{
    public static string RepoRoot { get; } = RepoPaths.RepoRoot;

    public static string SoundFontPath { get; } = RepoPaths.SoundFontPath;

    public static string RegressionsDir { get; } = Path.Combine(RepoRoot, "fixtures", "regressions");

    public static string QuarantineDir { get; } =
        Environment.GetEnvironmentVariable("AUDIO_CLAUDIO_QUARANTINE")
        ?? Path.Combine(RepoRoot, "artifacts", "closed-loop-quarantine");
}
