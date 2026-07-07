using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests;

public class ProjectReferenceGraphTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Dependency_graph_matches_the_architecture()
    {
        // §3: Domain depends on nothing.
        AssertRefs(RepoPaths.Src("AudioClaudio.Domain"));

        // Application -> Domain
        AssertRefs(RepoPaths.Src("AudioClaudio.Application"),
            "AudioClaudio.Domain");

        // Infrastructure -> Application, Domain
        AssertRefs(RepoPaths.Src("AudioClaudio.Infrastructure"),
            "AudioClaudio.Application", "AudioClaudio.Domain");

        // Cli -> everything (composition root)
        AssertRefs(RepoPaths.Src("AudioClaudio.Cli"),
            "AudioClaudio.Application", "AudioClaudio.Domain", "AudioClaudio.Infrastructure");

        // Tests -> all four src projects
        AssertRefs(RepoPaths.Tests("AudioClaudio.Tests"),
            "AudioClaudio.Application", "AudioClaudio.Cli",
            "AudioClaudio.Domain", "AudioClaudio.Infrastructure");
    }

    private static void AssertRefs(string csprojPath, params string[] expected)
    {
        var actual = CsprojReader.ReferencedProjectNames(csprojPath);
        var want = expected.ToHashSet(StringComparer.Ordinal);

        Assert.True(want.SetEquals(actual),
            $"{Path.GetFileName(csprojPath)} references " +
            $"[{string.Join(", ", actual.OrderBy(x => x, StringComparer.Ordinal))}], " +
            $"expected [{string.Join(", ", want.OrderBy(x => x, StringComparer.Ordinal))}].");
    }
}
