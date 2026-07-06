# Step 12 — README, Polish, Ship v0.1.0 — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Build-spec step:** Section 6 Step 12 (R12.1, R12.2)
**Goal:** Write the project README that states the problem, the pipeline, the non-negotiables, the closed-loop suite and what it proves, and how to run the four CLI commands and honest limitations; then verify CI is green on a fresh clone, confirm `UNLICENSE` at root, tag `v0.1.0`, and stop.
**Architecture:** This step touches no production layer — it adds prose (`README.md` at repo root) plus executable **doc-lint tests** in the single test project (`tests/AudioClaudio.Tests`) that pin the README's required content so it cannot silently regress. It respects the dependency rule trivially: the new tests read files under the repo root via a `RepositoryRoot` locator and reference no `src/` types at all.
**Tech Stack:** Markdown (README), xUnit (doc-lint / release-gate tests), `System.IO` (root locator), the .NET 10 SDK for the fresh-clone build/test, and `git tag` for the release marker.
**Prerequisites:** Steps 0–11 all green and committed (Section 1 rule 3). The README documents commands that must actually exist — `transcribe` (wired in Step 9 to emit `raw.mid` + `score.mid`, then extended in Step 11 to also emit `score.musicxml`, per CONTRACTS §9), `play`/`render` (Step 8), `listen` (Step 10) — and the fresh-clone `dotnet test` must pass the closed-loop suite (Step 9), which in turn needs the committed SoundFont fixture (Step 8). `DECISIONS.md` already exists (first created in Step 2/3).
**Commit (spec):** `docs: README; v0.1.0`

---

## Approach

Step 12 is a documentation-and-release step, so there is very little new code — but "very little" is not "none," and the constitution still governs. Two ideas keep this step honest and testable:

1. **The README's contract is executable.** R12.1 enumerates six things the README *SHALL* state (the problem, the pipeline diagram, the non-negotiables, the closed loop and what it proves, how to run the four commands, and honest limitations). Rather than eyeball a prose file, we encode that checklist as xUnit **doc-lint tests**: a test reads the root `README.md` and asserts each mandated section heading and each load-bearing token is present. The README is currently a two-line stub, so these tests start genuinely **red** and go **green** only when the full README is authored — a real red-green cycle. As a bonus, the tests guard the README forever: a future edit that drops the "Limitations" section fails CI.

2. **The ship artifacts are release-gate tests.** R12.2 requires `UNLICENSE` at the root and a fresh, green CI. We pin the artifact half (`UNLICENSE` present and actually the public-domain dedication; `DECISIONS.md` present; README declares public domain) as guard tests. These may already pass on first run because earlier steps created the artifacts — that is the point of a release gate: it fails loudly only if something was lost. The process half (fresh-clone build/test and the `v0.1.0` tag) is a final release task with no red-green cycle, and is labelled as such.

To read repo-root files from a test that runs out of `bin/Debug/net10.0/`, we need the repository root at runtime. A tiny `RepositoryRoot` helper walks up from the test assembly's directory until it finds `AudioClaudio.sln`. This is machine-independent (works on the M3 Max dev box and in CI regardless of clone path) and deterministic.

Use @superpowers:test-driven-development for each red-green loop, @elements-of-style:writing-clearly-and-concisely while drafting the README prose, and @superpowers:verification-before-completion at the release gate — do not claim "shipped" without the fresh-clone test output in hand.

---

## Requirements coverage

| Requirement | Task(s) | Proven by (test) |
|---|---|---|
| **R12.1** — README states the problem, pipeline diagram, non-negotiables (§4), the closed loop and what it proves, how to run `listen`/`transcribe`/`play`/`render`, and honest limitations (monophonic, declared tempo, single staff) | Task 1 (root locator seam), Task 2 (author README + doc-lint tests) | `AudioClaudio.Tests.Ship.ReadmeCompletenessTests` (section headings, four CLI commands, four non-negotiables, closed-loop meaning, pipeline diagram, three limitations) |
| **R12.2** — CI green on a fresh clone; `UNLICENSE` at root; tag `v0.1.0`; stop | Task 3 (ship-readiness gate), Task 4 (fresh-clone verify + tag) | `AudioClaudio.Tests.Ship.ShipReadinessTests` — literal R12.2: UNLICENSE present + public-domain text; process: fresh-clone `dotnet test` output + `git tag v0.1.0`. *Supplementary release-hygiene* (beyond R12.2's text): DECISIONS.md present; README declares public domain |

Every R12.x for this step appears above. The R12.2 row's `ShipReadinessTests` also
checks two artifacts *beyond* the literal requirement text — `DECISIONS.md`
presence and the README's public-domain declaration. These are deliberate
release-hygiene guards (Section 1 rule 7's license log; the README consistency
Task 2 produces), not a claim that R12.2 itself mandates them; the strict
requirement-to-test trace for R12.2 is only the `UNLICENSE` artifact plus the
fresh-clone CI and `v0.1.0` tag process steps.

---

## Task 1: `RepositoryRoot` locator (test seam for all doc-lint tests)

**Files:**
- Create: `tests/AudioClaudio.Tests/Ship/RepositoryRoot.cs`
- Test: `tests/AudioClaudio.Tests/Ship/RepositoryRootTests.cs`

**Step 1 — Write the failing test:**

```csharp
using Xunit;

namespace AudioClaudio.Tests.Ship;

public sealed class RepositoryRootTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Locates_directory_that_holds_the_solution_and_ship_artifacts()
    {
        // The located root must contain the three files every clone has at top level.
        Assert.True(RepositoryRoot.Exists("AudioClaudio.sln"), "solution not found at located root");
        Assert.True(RepositoryRoot.Exists("UNLICENSE"), "UNLICENSE not found at located root");
        Assert.True(RepositoryRoot.Exists("CLAUDE.md"), "CLAUDE.md not found at located root");
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void ReadAllText_returns_the_content_of_a_root_file()
    {
        var text = RepositoryRoot.ReadAllText("UNLICENSE");
        Assert.False(string.IsNullOrWhiteSpace(text));
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Ship.RepositoryRootTests"
```

Expected FAILURE: a compile error — `RepositoryRoot` does not exist yet (`CS0103: The name 'RepositoryRoot' does not exist in the current context`). The red is the missing seam.

**Step 3 — Minimal implementation:**

```csharp
using System;
using System.IO;

namespace AudioClaudio.Tests.Ship;

/// <summary>
/// Locates the repository root at test time by walking up from the running test
/// assembly's directory until the solution file is found. Machine-independent:
/// it works on a developer box and in CI regardless of the clone path, and it
/// has no dependency on any <c>src/</c> project (the doc-lint tests read files
/// under the root, not domain types).
/// </summary>
internal static class RepositoryRoot
{
    private const string SolutionFileName = "AudioClaudio.sln";

    /// <summary>Absolute path to the repository root, resolved once.</summary>
    public static string Path { get; } = Locate();

    /// <summary>True if <paramref name="relativePath"/> exists under the root.</summary>
    public static bool Exists(string relativePath) =>
        File.Exists(System.IO.Path.Combine(Path, relativePath));

    /// <summary>Reads a UTF-8 file given a path relative to the repository root.</summary>
    public static string ReadAllText(string relativePath) =>
        File.ReadAllText(System.IO.Path.Combine(Path, relativePath));

    private static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, SolutionFileName)))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate '{SolutionFileName}' above '{AppContext.BaseDirectory}'.");
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Ship.RepositoryRootTests"
```

Expected PASS: both facts green — the walk-up finds the root, and `ReadAllText("UNLICENSE")` returns the dedication text.

**Step 5 — Commit:** (roll into the Step 12 commit; see Task 4 for the single `docs: README; v0.1.0` commit, or commit finer-grained here)

```bash
# gitbutler skill — inspect fresh IDs, then commit this slice
but status -fv
but commit <branch> -m "test(ship): repository-root locator for doc-lint tests" --changes <ids> --status-after
```

---

## Task 2: Author the README and pin its required content (R12.1)

**Files:**
- Modify: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/README.md` (currently a 2-line stub — replace wholesale)
- Test: `tests/AudioClaudio.Tests/Ship/ReadmeCompletenessTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System;
using Xunit;

namespace AudioClaudio.Tests.Ship;

/// <summary>
/// Executable form of R12.1: the README SHALL state the problem, the pipeline
/// diagram, the non-negotiables, the closed loop and what it proves, how to run
/// the four CLI commands, and honest limitations. Each assertion pins one of
/// those obligations so the README cannot silently lose a mandated section.
/// </summary>
public sealed class ReadmeCompletenessTests
{
    private static readonly string Readme = RepositoryRoot.ReadAllText("README.md");

    [Theory]
    [Trait("Category", "Fast")]
    // R12.1: the six mandated sections, proven by their headings.
    [InlineData("## What it is")]              // the problem in plain English
    [InlineData("## Pipeline")]                // the pipeline diagram
    [InlineData("## The non-negotiables")]     // §4 invariants
    [InlineData("## The closed loop")]         // what it proves
    [InlineData("## Usage")]                   // how to run the four commands
    [InlineData("## Limitations")]             // honest limitations
    public void Contains_required_section_heading(string heading)
    {
        Assert.Contains(heading, Readme, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Category", "Fast")]
    // R12.1: how to run all four CLI commands (Section 7 verbs).
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
    // R12.1: the four non-negotiables of Section 4 are each named.
    [InlineData("integer samples")]  // time is integer samples
    [InlineData("wall clock")]       // the domain never reads the wall clock
    [InlineData("determinism")]      // same WAV -> identical events, bit-for-bit
    [InlineData("cents")]            // pitch decisions in cents/MIDI space
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
    // R12.1: the closed loop is explained as transcribe ∘ synthesize with the
    // synthesizer as oracle (what the suite proves).
    public void Explains_what_the_closed_loop_proves()
    {
        Assert.Contains("transcribe", Readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("synthesize", Readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("oracle", Readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Fast")]
    // R12.1: the pipeline diagram is present (fenced flow with the arrow glyph
    // and the "note events" node from the Section 2 diagram).
    public void Includes_the_pipeline_diagram()
    {
        Assert.Contains("note events", Readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("──▶", Readme, StringComparison.Ordinal);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Ship.ReadmeCompletenessTests"
```

Expected FAILURE: nearly every case red — the current `README.md` is the two-line stub (`# audio-claudio` + one tagline), so none of the mandated headings, CLI invocations, non-negotiable tokens, limitations, closed-loop terms, or the diagram glyph are present.

**Step 3 — Minimal implementation:** replace the stub `README.md` at the repo root with the full document below.

```markdown
# Audio Claudio

*audiō Claudiō* — "I hear, by means of Claude." A real-time **monophonic piano
transcriber** in C# / .NET 10 (LTS), built in open collaboration with Claude
Code. Audio comes in from a microphone or a WAV file, note events come out,
notation is emitted as MusicXML, and the transcribed piece can be played back
through a synthesized piano. Public domain (`UNLICENSE`).

## What it is

Play a single melody line into a microphone — or hand it a WAV file — and Audio
Claudio tells you **which notes were played, when, and for how long**, then
writes them out as MIDI and as engravable sheet music (MusicXML). It listens to
one note at a time (see [Limitations](#limitations)), detects each note's pitch
and attack, snaps the timing to a tempo you declare, and can sing the result
back to you. It is a small, honest transcriber whose correctness is *earned* by
a closed-loop proof rather than asserted.

## Pipeline

```
microphone / WAV file
      │
      ▼
  audio frames ──▶ spectral analysis ──▶ pitch + onset detection
      │                                        │
      ▼                                        ▼
  (adapters)                            note events (domain)
                                               │
                        ┌──────────────────────┼──────────────────────┐
                        ▼                      ▼                      ▼
                  quantization           MIDI export           MusicXML emission
                        │                      │                      │
                        ▼                      ▼                      ▼
                  a "score"            playback (MeltySynth)    notation render
```

Audio enters only through **adapters** (a WAV reader; a live PortAudio capture).
Everything from *note events* rightward is pure domain logic with no knowledge
of files or devices — the microphone is just one more adapter, added last.

## The non-negotiables

Four invariants hold everywhere in the domain, forever; a change that violates
one is a bug regardless of whether it compiles.

1. **Time is integer samples.** Every position and duration is an integer count
   of samples carried with its declared sample rate. Seconds are a display
   conversion at the edge only — the domain never accumulates floating time.
2. **The domain never reads the wall clock.** "Now" enters solely through the
   `IClock` port, and only in the Application/Infrastructure layers. Live
   timestamps are sample counts from the audio stream, not `DateTime` reads.
3. **Determinism.** The same WAV file produces the identical sequence of note
   events on every run and every machine, bit-for-bit. There is no randomness in
   the domain; every algorithmic tie-break is defined, not incidental.
4. **Pitch decisions are made in cents/MIDI space, not raw Hz,** so a tolerance
   means the same thing at the bottom of the keyboard as at the top (a cent is
   1/100 of a semitone).

## The closed loop

Because this project contains **both** a transcriber (audio → notes) and a
synthesizer (notes → audio), the two compose into a self-checking loop and the
synthesizer becomes the **oracle** — no hand-labelled ground truth is ever
needed:

> generate a random constrained score → **synthesize** it to audio →
> **transcribe** that audio back through the full pipeline → demand the original
> score back, within tolerance.

This is `transcribe ∘ synthesize ≈ id` on the constrained corpus. When it holds
over thousands of randomly generated scores — matching note count, exact pitch,
onset and duration each within one grid subdivision — the correctness claim is
earned rather than asserted. This closed-loop property suite is the project's
trial balance; it runs in CI.

## Usage

Prerequisite: the **.NET 10 SDK (LTS)**. Build and test:

```bash
dotnet build
dotnet test                        # full suite: unit + property + closed loop
dotnet test --filter Category=Fast # skip the slow closed-loop properties
```

The CLI is the only place adapters are wired to ports. Four commands:

```bash
# Transcribe a WAV file at a declared tempo -> raw.mid, score.mid, score.musicxml
claudio transcribe <in.wav> --tempo 120 [--out-dir .]

# Listen live from the microphone; on stop, writes the same trio of files
claudio listen --tempo 100

# Play a MIDI file aloud through the synthesized piano (MeltySynth)
claudio play <file.mid>

# Deterministically render a MIDI file to a WAV
claudio render <file.mid> <out.wav>
```

Run them through the CLI project during development, e.g.:

```bash
dotnet run --project src/AudioClaudio.Cli -- transcribe song.wav --tempo 120
dotnet run --project src/AudioClaudio.Cli -- listen --tempo 100
dotnet run --project src/AudioClaudio.Cli -- play score.mid
dotnet run --project src/AudioClaudio.Cli -- render score.mid out.wav
```

## Limitations

This is an honest MVP; it declines scope rather than faking it.

- **Monophonic only.** One note at a time. Chords and overlapping voices are out
  of scope for v0.1.0 — polyphony (a neural model behind the same port) is
  Phase 2.
- **Declared tempo, not estimated.** You pass `--tempo`; the MVP does not guess
  it. Tempo estimation is a recorded Phase 2 item — hiding an unreliable
  estimator inside the MVP would poison the closed-loop suite.
- **Single staff.** MusicXML output is one staff with a clef chosen by range and
  a fixed 4/4 time signature; a treble/bass split is Phase 2.
- Pitch accuracy is characterised across MIDI 33–96 (A1–C7); the extreme low and
  high keys are legitimately harder and their status is documented, not hidden.

## License

This is free and unencumbered software released into the public domain. See
[`UNLICENSE`](UNLICENSE) at the repository root.
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Ship.ReadmeCompletenessTests"
```

Expected PASS: all theories and facts green — every mandated heading, the four `claudio <verb>` invocations, the four non-negotiable tokens, the three limitation tokens, the closed-loop terms, and the diagram (`note events` node + `──▶` glyph) are present.

**Step 5 — Commit:** (fold into the Step 12 commit in Task 4, or commit now)

```bash
but status -fv
but commit <branch> -m "docs: README stating problem, pipeline, invariants, usage, limits" --changes <ids> --status-after
```

---

## Task 3: Ship-readiness release gate (R12.2 artifacts)

**Files:**
- Test: `tests/AudioClaudio.Tests/Ship/ShipReadinessTests.cs`
- (No production change expected — `UNLICENSE` exists since Step 0, `DECISIONS.md` since Step 2/3. If a gate is red, **restore the missing artifact** before proceeding.)

**Note on red-green for this task.** Unlike Task 2, these assertions may pass on
their first run because the artifacts were created in earlier steps. That is the
nature of a release gate: it exists to fail loudly if a required artifact was
lost between Step 0 and ship. The `Readme_declares_public_domain` fact does give
a genuine red if Task 2's README has not yet landed. Run the gate; if all green,
the ship artifacts are intact.

**Step 1 — Write the failing test:**

```csharp
using System;
using Xunit;

namespace AudioClaudio.Tests.Ship;

/// <summary>
/// Executable form of R12.2's artifact half: the public-domain dedication lives
/// at the repository root and is the real UNLICENSE text (not a stub), the
/// decision/license log is present (Section 1 rule 7), and the README points at
/// the public-domain dedication. A red here means a required ship artifact was
/// lost and must be restored before tagging v0.1.0.
/// </summary>
public sealed class ShipReadinessTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Unlicense_is_present_at_repository_root()
    {
        Assert.True(RepositoryRoot.Exists("UNLICENSE"), "UNLICENSE missing at repo root");
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Unlicense_is_the_public_domain_dedication_not_a_stub()
    {
        var text = RepositoryRoot.ReadAllText("UNLICENSE");
        Assert.Contains("released into the public domain", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITHOUT WARRANTY OF ANY KIND", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Decisions_log_is_present_at_repository_root()
    {
        Assert.True(RepositoryRoot.Exists("DECISIONS.md"), "DECISIONS.md missing at repo root");
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Readme_declares_public_domain_and_points_at_the_unlicense()
    {
        var readme = RepositoryRoot.ReadAllText("README.md");
        Assert.Contains("UNLICENSE", readme, StringComparison.Ordinal);
        Assert.Contains("public domain", readme, StringComparison.OrdinalIgnoreCase);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Ship.ShipReadinessTests"
```

Expected result: `Readme_declares_public_domain_and_points_at_the_unlicense` is **red** until Task 2's README lands; the three artifact facts are **green** if Steps 0–11 kept `UNLICENSE` and `DECISIONS.md` intact. If any artifact fact is red, stop and restore that file — do not paper over it.

**Step 3 — Minimal implementation:** no code. If a gate flagged a missing
artifact, restore it (e.g., `UNLICENSE` from Step 0, or ensure `DECISIONS.md`
exists at the root with its decision + NuGet-license log per Section 1 rule 7).
Otherwise the README from Task 2 already satisfies the last fact.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Ship.ShipReadinessTests"
```

Expected PASS: all four facts green — `UNLICENSE` present and genuine, `DECISIONS.md` present, README declares public domain and links the dedication.

**Step 5 — Commit:** (fold into Task 4's single Step 12 commit, or commit now)

```bash
but status -fv
but commit <branch> -m "test(ship): release-gate for UNLICENSE, DECISIONS, README license" --changes <ids> --status-after
```

---

## Task 4: Release — fresh-clone verification and the `v0.1.0` tag (R12.2 ship)

**Files:**
- Modify: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/CLAUDE.md` — update the "Where the project is right now" section to record that Step 12 landed and v0.1.0 shipped (the constitution asks to keep that section honest as steps land).

**This task is a release action, not a red-green cycle** (Section 1 rule 1: a
`SHALL`/process obligation with no unit test to drive). Use
@superpowers:verification-before-completion — capture the actual command output
before claiming the ship is done; do not assert success from memory.

**Step 1 — Full suite green locally, formatting clean:**

```bash
dotnet format --verify-no-changes
dotnet test                        # entire suite, including the slow closed loop
```

Expected: `dotnet format` reports no changes; `dotnet test` is all green, including the Step 9 closed-loop property suite.

**Step 2 — Update the constitution's status pointer:** edit CLAUDE.md's
"Where the project is right now" section to state that all steps (0–12) are
complete and `v0.1.0` is tagged, so the next reader is not misled into thinking
the project is still at Step 0. Keep it to a couple of honest sentences.

**Step 3 — Commit the README, doc-lint tests, and status update with the spec message:**

```bash
# gitbutler skill: put Step 12's changes on their own branch and commit with the spec message.
but status -fv                                   # read fresh file/hunk IDs
but branch new docs-readme-ship
but mark docs-readme-ship
# ...ensure README.md, the three Ship/*.cs test files, and the CLAUDE.md status edit are staged to this branch...
but commit docs-readme-ship -m "docs: README; v0.1.0" --changes <ids> --status-after
but push docs-readme-ship
but pr new                                       # open the PR; merge to main via GitButler once CI is green
```

**Step 4 — Fresh-clone verification (R12.2 "CI green on a fresh clone"):** after
the PR merges to `main` and CI is green, prove a clean clone builds and tests
from nothing (this exercises the committed SoundFont fixture and the full suite):

```bash
tmp=$(mktemp -d)
git clone https://github.com/TuesdayCrowd/audio-claudio.git "$tmp/audio-claudio"
cd "$tmp/audio-claudio"
dotnet restore
dotnet build -c Release
dotnet test  -c Release            # must be all green on the fresh clone
```

Expected: restore, build, and the entire test suite pass on a pristine checkout with no local state.

**Step 5 — Tag `v0.1.0` and stop (R12.2).** `git tag` does not modify branches,
so it is permitted (unlike `git commit`/`checkout`/`rebase`/`merge`). Tag the
merged release commit on `main` and push only the tag (never the workspace
branch):

```bash
git fetch origin main
git tag -a v0.1.0 origin/main -m "Audio Claudio v0.1.0 — monophonic piano transcriber MVP"
git push origin v0.1.0
```

Then **stop** — v0.1.0 is shipped. Phase 2 (Section 8) begins only afterward.

---

## Verify (step exit criteria)

Restating Section 6 Step 12's obligations as a checklist:

- [ ] **R12.1** `README.md` states the problem in plain English (`## What it is`).
- [ ] **R12.1** `README.md` includes the pipeline diagram (`## Pipeline`, with the flow arrows and the `note events` node).
- [ ] **R12.1** `README.md` states the four non-negotiables of §4 (integer samples; never reads the wall clock; determinism; cents/MIDI-space pitch decisions).
- [ ] **R12.1** `README.md` explains the closed-loop suite and what it proves (`transcribe ∘ synthesize ≈ id`; the synthesizer as oracle).
- [ ] **R12.1** `README.md` shows how to run `transcribe`, `listen`, `play`, and `render`.
- [ ] **R12.1** `README.md` states honest limitations: monophonic, declared tempo, single staff.
- [ ] **R12.2** CI is green on a fresh clone (`git clone` → `dotnet build` → `dotnet test`, all pass).
- [ ] **R12.2** `UNLICENSE` is present at the repository root and is the genuine public-domain dedication.
- [ ] **R12.2** Tag `v0.1.0` created on the merged release commit and pushed; then stop.

## Definition of Done

- [ ] `dotnet build` succeeds.
- [ ] `dotnet format --verify-no-changes` is clean.
- [ ] All new Ship tests are green (`ReadmeCompletenessTests`, `ShipReadinessTests`, `RepositoryRootTests`), and the fast filter `dotnet test --filter Category=Fast` stays green.
- [ ] The full suite `dotnet test` is green, including the Step 9 closed loop.
- [ ] Dependency rule intact: the new tests reference no `src/` types; Domain still imports nothing beyond the BCL.
- [ ] Committed via GitButler with the spec message `docs: README; v0.1.0`.
- [ ] Requirement-coverage table fully satisfied (R12.1, R12.2).
- [ ] `DECISIONS.md` update: **not required** — Step 12 makes no design decision and adds no NuGet package (verify none was introduced).
- [ ] Fresh-clone verification captured and `v0.1.0` tagged; work stopped (Phase 2 not begun).
