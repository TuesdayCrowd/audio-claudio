using Xunit;

namespace AudioClaudio.Tests;

public class ToolchainTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Dotnet_runtime_is_at_least_net10()
    {
        Assert.True(Environment.Version.Major >= 10,
            $"Expected .NET 10+, got {Environment.Version}.");
    }
}
