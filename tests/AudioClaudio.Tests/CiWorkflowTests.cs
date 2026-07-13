using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests;

public class CiWorkflowTests
{
    private static string WorkflowPath =>
        Path.Combine(RepoPaths.RepoRoot, ".github", "workflows", "ci.yml");

    [Fact]
    [Trait("Category", "Fast")]
    public void Ci_workflow_exists_and_builds_and_tests_on_push()
    {
        Assert.True(File.Exists(WorkflowPath), $"CI workflow missing: {WorkflowPath}");

        var yaml = File.ReadAllText(WorkflowPath);

        Assert.Contains("on:", yaml);                  // has triggers
        Assert.Contains("push", yaml);                 // ... including push (R0.4)
        Assert.Contains("actions/setup-dotnet", yaml); // installs the SDK
        Assert.Contains("10.0", yaml);                 // ... .NET 10
        Assert.Contains("dotnet build", yaml);         // builds
        Assert.Contains("dotnet test", yaml);          // and tests
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Ci_workflow_smokes_packaging_mechanics_on_linux_x64()
    {
        var yaml = File.ReadAllText(WorkflowPath);

        Assert.Contains("linux-x64", yaml);
        Assert.Contains("--self-contained", yaml);
        Assert.Contains("scripts/smoke-test-packaged.sh", yaml);
        Assert.Contains("PACKAGING MECHANICS", yaml);
    }
}
