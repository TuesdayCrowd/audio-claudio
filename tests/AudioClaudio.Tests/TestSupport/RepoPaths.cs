namespace AudioClaudio.Tests.TestSupport;

/// <summary>
/// Locates repository files from the test bin directory by walking up to the
/// folder that holds AudioClaudio.sln. Works locally and in CI checkouts.
/// </summary>
public static class RepoPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AudioClaudio.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate AudioClaudio.sln above " + AppContext.BaseDirectory);
    }

    public static string Src(string project) =>
        Path.Combine(RepoRoot, "src", project, project + ".csproj");

    public static string Tests(string project) =>
        Path.Combine(RepoRoot, "tests", project, project + ".csproj");

    // The SINGLE repo-root/fixture locator for the whole test suite (CONTRACTS.md §0).
    // Later steps route ALL fixture/root access through these — do NOT add a second
    // TestPaths / Fixtures / RepositoryRoot walk-up variant (Steps 8, 9, 11, 12).
    public static string Fixture(params string[] parts) =>
        Path.Combine(RepoRoot, "fixtures", Path.Combine(parts));

    public static string SoundFontPath => Fixture("soundfont", "GeneralUser-GS.sf2");

    public static string GoldenDirectory => Fixture("golden");
}
