using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests;

public class PackagingScriptsTests
{
    private static string SmokeTestScript =>
        Path.Combine(RepoPaths.RepoRoot, "scripts", "smoke-test-packaged.sh");

    [Fact]
    [Trait("Category", "Fast")]
    public void Smoke_test_script_exists_and_exercises_version_transcribe_and_render()
    {
        Assert.True(File.Exists(SmokeTestScript), $"missing: {SmokeTestScript}");

        var script = File.ReadAllText(SmokeTestScript);

        Assert.Contains("--version", script);
        Assert.Contains("transcribe", script);
        Assert.Contains("render", script);
        Assert.Contains("STAGE_DIR", script);
    }

    private static string PackageMacosScript =>
        Path.Combine(RepoPaths.RepoRoot, "scripts", "package-macos.sh");

    [Fact]
    [Trait("Category", "Fast")]
    public void Package_macos_script_publishes_self_contained_osx_arm64_stages_fixtures_and_zips()
    {
        Assert.True(File.Exists(PackageMacosScript), $"missing: {PackageMacosScript}");

        var script = File.ReadAllText(PackageMacosScript);

        Assert.Contains("-r osx-arm64", script);
        Assert.Contains("--self-contained", script);
        Assert.Contains("dotnet publish", script);
        Assert.DoesNotContain("PublishAot", script);
        Assert.Contains("fixtures/models", script);
        Assert.Contains("fixtures/soundfont", script);
        Assert.Contains("smoke-test-packaged.sh", script);
        Assert.Contains("zip", script);
        Assert.Contains("claudio-macos-arm64", script);
    }
}
