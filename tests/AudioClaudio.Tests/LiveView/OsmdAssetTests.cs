using System;
using System.IO;
using System.Text;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.LiveView;

/// <summary>
/// Sanity-checks the vendored OSMD bundle the same way Step 8's SoundFontFixtureTests
/// sanity-checked the committed .sf2: not empty, not accidentally a fetch-failure HTML
/// page, and its license file is actually present and says BSD.
/// </summary>
public class OsmdAssetTests
{
    private static string WwwRoot => Path.Combine(RepoPaths.RepoRoot, "src", "AudioClaudio.Cli", "wwwroot");

    [Fact]
    [Trait("Category", "Fast")]
    public void VendoredOsmdBundleExistsAndLooksLikeRealJavaScript()
    {
        string path = Path.Combine(WwwRoot, "osmd", "opensheetmusicdisplay.min.js");
        Assert.True(File.Exists(path), $"expected the vendored OSMD bundle at {path}");

        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 100_000,
            "the OSMD UMD bundle is legitimately large (hundreds of KB); a tiny file means a bad download");

        string head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 200)).TrimStart();
        Assert.False(head.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase),
            "the file looks like an HTML error page, not JavaScript -- check the download URL");
        Assert.False(head.StartsWith("<html", StringComparison.OrdinalIgnoreCase),
            "the file looks like an HTML error page, not JavaScript -- check the download URL");
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void OsmdLicenseFileIsCommittedAndIsBsd()
    {
        string path = Path.Combine(WwwRoot, "osmd", "LICENSE-OSMD.txt");
        Assert.True(File.Exists(path), $"expected the OSMD license text at {path}");

        string text = File.ReadAllText(path);
        Assert.Contains("BSD", text, StringComparison.OrdinalIgnoreCase);
    }
}
