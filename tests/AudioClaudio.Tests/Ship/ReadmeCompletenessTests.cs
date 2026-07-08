using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Ship;

/// <summary>
/// Executable form of R12.1: the README SHALL state the problem, the pipeline
/// diagram, the non-negotiables, the closed loop and what it proves, how to
/// run the four CLI commands, and honest limitations. Each assertion pins one
/// of those obligations so the README cannot silently lose a mandated section
/// or quietly soften an honest disclosure.
///
/// Uses the single repo-root locator <see cref="RepoPaths"/> (CONTRACTS.md
/// §0) exactly as <see cref="RepoHygieneTests"/>/<see cref="CiWorkflowTests"/>
/// already do — no parallel RepositoryRoot/TestPaths walk-up is introduced.
/// </summary>
public class ReadmeCompletenessTests
{
    private static readonly string Readme =
        File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, "README.md"));

    [Theory]
    [Trait("Category", "Fast")]
    // R12.1: the six mandated sections, proven by their headings.
    [InlineData("## What it is")]          // the problem in plain English
    [InlineData("## Pipeline")]            // the pipeline diagram
    [InlineData("## The non-negotiables")] // §4 invariants
    [InlineData("## The closed loop")]     // what it proves
    [InlineData("## Usage")]               // how to run the four commands
    [InlineData("## Limitations")]         // honest limitations
    public void Contains_required_section_heading(string heading)
    {
        Assert.Contains(heading, Readme, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Category", "Fast")]
    // R12.1: how to run all four CLI commands (§7 verbs).
    [InlineData("claudio transcribe")]
    [InlineData("claudio listen")]
    [InlineData("claudio play")]
    [InlineData("claudio render")]
    public void Documents_how_to_run_each_cli_command(string invocation)
    {
        Assert.Contains(invocation, Readme, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Category", "Fast")]
    // R12.1: the four non-negotiables of §4 are each named.
    [InlineData("integer samples")] // time is integer samples
    [InlineData("wall clock")]      // the domain never reads the wall clock
    [InlineData("determinism")]     // same WAV -> identical events, bit-for-bit
    [InlineData("cents")]           // pitch decisions in cents/MIDI space
    public void Names_each_non_negotiable(string token)
    {
        Assert.Contains(token, Readme, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [Trait("Category", "Fast")]
    // R12.1: honest limitations — monophonic, declared tempo, single staff.
    [InlineData("monophonic")]
    [InlineData("declared tempo")]
    [InlineData("single staff")]
    public void States_each_honest_limitation(string token)
    {
        Assert.Contains(token, Readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Fast")]
    // R12.1: the closed loop is explained as transcribe-then-synthesize with
    // the synthesizer as oracle — what the suite actually proves.
    public void Explains_what_the_closed_loop_proves()
    {
        Assert.Contains("transcribe", Readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("synthesize", Readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("oracle", Readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Fast")]
    // R12.1: the pipeline diagram is present (fenced flow with the arrow
    // glyph and the "note events" node from the CLAUDE.md §2 diagram).
    public void Includes_the_pipeline_diagram()
    {
        Assert.Contains("note events", Readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("──▶", Readme, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Fast")]
    // Honest limitation beyond R12.1's literal three: duration is only
    // recoverable while a note stays audibly above the release threshold
    // (DECISIONS.md, "Step 9 — The closed loop"), which is a pitch-dependent
    // decay effect, not the same claim as "declared tempo"/"single staff".
    public void States_the_duration_recovery_limitation_for_high_notes()
    {
        Assert.Contains("decay", Readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("duration", Readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Fast")]
    // Honest limitation: the rare (~0.4%) YIN octave-error / onset-miss
    // residual the nightly closed-loop job discovers (DECISIONS.md, "Step 9").
    public void States_the_octave_error_residual_honestly()
    {
        Assert.Contains("octave", Readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Fast")]
    // Honest limitation: live-mic capture and the MuseScore "loads cleanly"
    // check (R11.2) are both manual, not automated (DECISIONS.md, "Step 11").
    public void States_manual_checks_are_manual()
    {
        Assert.Contains("MuseScore", Readme, StringComparison.Ordinal);
        Assert.Contains("manual", Readme, StringComparison.OrdinalIgnoreCase);
    }
}
