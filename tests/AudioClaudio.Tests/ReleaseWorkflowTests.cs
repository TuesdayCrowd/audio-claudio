using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests;

/// <summary>
/// Pins the release-asset workflow: on a published GitHub Release, build the real
/// self-contained osx-arm64 package on an Apple-Silicon runner and attach the zip as a
/// downloadable asset (GitHub "Packages" cannot host a plain zip — a binary belongs on the
/// Release). Keeps the workflow's load-bearing wiring from silently regressing.
/// </summary>
public class ReleaseWorkflowTests
{
    private static string WorkflowPath =>
        Path.Combine(RepoPaths.RepoRoot, ".github", "workflows", "release.yml");

    [Fact]
    [Trait("Category", "Fast")]
    public void Release_workflow_builds_the_macos_arm64_zip_and_attaches_it_to_the_release()
    {
        Assert.True(File.Exists(WorkflowPath), $"missing: {WorkflowPath}");

        var yaml = File.ReadAllText(WorkflowPath);

        Assert.Contains("release:", yaml);                 // triggers on a published release
        Assert.Contains("published", yaml);
        Assert.Contains("macos-14", yaml);                 // Apple Silicon (arm64) runner
        Assert.Contains("contents: write", yaml);          // permission to upload assets
        Assert.Contains("scripts/package-macos.sh", yaml); // reuses the packaging script
        Assert.Contains("gh release upload", yaml);        // attaches as a release asset
        Assert.Contains("claudio-macos-arm64.zip", yaml);
    }
}
