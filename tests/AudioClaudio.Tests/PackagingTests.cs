using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests;

public class PackagingTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Cli_project_publishes_the_binary_as_claudio()
    {
        var csproj = File.ReadAllText(RepoPaths.Src("AudioClaudio.Cli"));

        Assert.Contains("<AssemblyName>claudio</AssemblyName>", csproj);
    }
}
