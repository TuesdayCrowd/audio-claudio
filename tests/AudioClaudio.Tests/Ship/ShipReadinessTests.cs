using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Ship;

/// <summary>
/// Executable form of R12.2's artifact half: the public-domain dedication at
/// the repository root is the genuine UNLICENSE text (not a stub), and the
/// README both declares public domain and points at it. A red here means a
/// required ship artifact regressed and must be restored before tagging
/// v0.1.0.
///
/// Deliberately does NOT re-assert bare existence of UNLICENSE/DECISIONS.md/
/// README.md at the repo root — <see cref="RepoHygieneTests.Required_root_file_is_present"/>
/// already pins that (R0.3) for exactly these files; duplicating it here
/// would just be the same assertion under a second name. This file only adds
/// the content-genuineness checks R12.2 needs that RepoHygieneTests does not
/// already cover.
/// </summary>
public class ShipReadinessTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Unlicense_is_the_public_domain_dedication_not_a_stub()
    {
        var text = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, "UNLICENSE"));

        Assert.Contains("released into the public domain", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITHOUT WARRANTY OF ANY KIND", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Readme_declares_public_domain_and_points_at_the_unlicense()
    {
        var readme = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, "README.md"));

        Assert.Contains("UNLICENSE", readme, StringComparison.Ordinal);
        Assert.Contains("public domain", readme, StringComparison.OrdinalIgnoreCase);
    }
}
