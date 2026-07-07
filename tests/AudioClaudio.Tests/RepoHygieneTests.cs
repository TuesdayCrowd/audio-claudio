using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests;

public class RepoHygieneTests
{
    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("UNLICENSE")]
    [InlineData(".gitignore")]
    [InlineData("README.md")]
    [InlineData("DECISIONS.md")]
    [InlineData("CLAUDE.md")]
    public void Required_root_file_is_present(string fileName)
    {
        var path = Path.Combine(RepoPaths.RepoRoot, fileName);
        Assert.True(File.Exists(path),
            $"Required repo-hygiene file missing at root (R0.3): {fileName}");
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Decisions_log_records_nuget_licenses()
    {
        var decisions = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, "DECISIONS.md"));

        // §1 rule 7: every NuGet package's license is recorded here.
        Assert.Contains("xUnit", decisions);
        Assert.Contains("CsCheck", decisions);
    }
}
