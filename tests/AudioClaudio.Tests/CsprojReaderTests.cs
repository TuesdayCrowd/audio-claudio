using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests;

public class CsprojReaderTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void ReferencedProjectNames_reads_project_reference_includes()
    {
        var tmp = Path.GetTempFileName();
        File.WriteAllText(tmp, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\AudioClaudio.Domain\AudioClaudio.Domain.csproj" />
                <ProjectReference Include="..\AudioClaudio.Application\AudioClaudio.Application.csproj" />
              </ItemGroup>
            </Project>
            """);
        try
        {
            var names = CsprojReader.ReferencedProjectNames(tmp);

            Assert.Equal(
                new[] { "AudioClaudio.Application", "AudioClaudio.Domain" },
                names.OrderBy(n => n, StringComparer.Ordinal));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void ReferencedProjectNames_is_empty_when_there_are_no_references()
    {
        var tmp = Path.GetTempFileName();
        File.WriteAllText(tmp, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        try
        {
            Assert.Empty(CsprojReader.ReferencedProjectNames(tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
