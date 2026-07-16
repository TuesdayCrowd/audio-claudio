using System.Text;
using AudioClaudio.Cli;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

/// <summary>
/// Pins the REAL top-level and per-command `--help` output (all seven commands) against
/// silent drift (S5.4). Rendered with a disabled styler (--no-color-equivalent: plain text)
/// so the goldens are byte-exact regardless of terminal. Goldens are seeded from the real,
/// fully-wired kernel's own output — never hand-typed (see the temporary seeding fact this
/// task's plan describes; it is run once and deleted, leaving only these assertions).
/// </summary>
public class HelpGoldenTests
{
    private static string Run(string[] args)
    {
        var app = AppBuilder.Build(new StringBuilder(), noColor: true);
        var stdout = new StringWriter();
        app.Run(args, stdout, new StringWriter());
        return stdout.ToString();
    }

    public static IEnumerable<object[]> Commands => new[]
    {
        new object[] { "transcribe" }, new object[] { "notate" }, new object[] { "render" },
        new object[] { "play" }, new object[] { "evaluate" }, new object[] { "evaluate-audio" },
        new object[] { "listen" }, new object[] { "separate" }, new object[] { "pianize" },
    };

    [Fact]
    [Trait("Category", "Fast")]
    public void Top_level_help_matches_the_golden()
    {
        string actual = Run(new[] { "--help" });
        string golden = File.ReadAllText(RepoPaths.Fixture("golden", "cli-help", "top-level.txt"));
        Assert.Equal(golden, actual);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [MemberData(nameof(Commands))]
    public void Per_command_help_matches_the_golden(string command)
    {
        string actual = Run(new[] { command, "--help" });
        string golden = File.ReadAllText(RepoPaths.Fixture("golden", "cli-help", $"{command}.txt"));
        Assert.Equal(golden, actual);
    }
}
