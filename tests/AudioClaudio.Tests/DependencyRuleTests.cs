using System.Reflection;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests;

public class DependencyRuleTests
{
    // --- The pure helper, tested first against synthetic names (genuine red-green). ---

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("MeltySynth")]
    [InlineData("Melanchall.DryWetMidi")]
    [InlineData("PortAudioSharp2")]
    [InlineData("NAudio")]
    [InlineData("Microsoft.ML.OnnxRuntime")]
    [InlineData("AudioClaudio.Application")]
    [InlineData("AudioClaudio.Infrastructure")]
    [InlineData("AudioClaudio.Cli")]
    public void IsForbidden_flags_libraries_the_domain_must_not_reference(string name)
    {
        Assert.True(DependencyRules.IsForbidden(name));
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("System.Runtime")]
    [InlineData("System.Private.CoreLib")]
    [InlineData("System.Linq")]
    [InlineData("netstandard")]
    [InlineData("AudioClaudio.Domain")] // Domain may reference itself, trivially.
    public void IsForbidden_allows_the_bcl_and_the_domain_itself(string name)
    {
        Assert.False(DependencyRules.IsForbidden(name));
    }

    // --- The architecture assertion, riding on the proven helper. ---

    [Fact]
    [Trait("Category", "Fast")]
    public void Domain_assembly_references_only_the_bcl()
    {
        var domainPath = Path.Combine(AppContext.BaseDirectory, "AudioClaudio.Domain.dll");
        Assert.True(File.Exists(domainPath),
            $"Domain assembly not found next to the tests: {domainPath}");

        var referenced = Assembly.LoadFrom(domainPath)
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToArray();

        var violations = referenced.Where(DependencyRules.IsForbidden).ToArray();

        Assert.True(violations.Length == 0,
            "AudioClaudio.Domain must reference nothing beyond the BCL, but references: "
            + string.Join(", ", violations));
    }
}
