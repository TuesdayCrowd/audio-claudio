# Step 0 — Scaffold and the Dependency Rule — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Build-spec step:** Section 6 Step 0 (R0.1, R0.2, R0.3, R0.4)
**Goal:** Stand up the four `src/` projects plus `tests/AudioClaudio.Tests` with project references that physically encode the §3 dependency rule, add repo hygiene (`UNLICENSE`, `.gitignore`, `README.md`, empty `DECISIONS.md`, `CLAUDE.md`) and GitHub Actions CI — all compiling empty and green.
**Architecture:** This step builds the hexagonal skeleton itself (§3). It creates every layer — Domain (BCL only), Application (→ Domain), Infrastructure (→ Application, Domain), Cli (→ everything, the composition root) — but adds *no* domain logic, ports, or adapters. Those arrive in later steps; here we only prove the boundaries hold by making the dependency rule a physical fact (project references) and guarding it with executable architecture tests.
**Tech Stack:** .NET 10 SDK (LTS), `dotnet new sln/classlib/console/xunit`, a repo-root `Directory.Build.props`, xUnit (Apache-2.0) + CsCheck (MIT) in the one test project, GitHub Actions (`actions/setup-dotnet@v4`).
**Prerequisites:** None — this is the first step. Every later step (§1 rule 3) depends on this one being green and committed.
**Commit (spec):** `chore: scaffold solution, dependency rule, CI`

---

## Approach

Step 0 has no runtime behavior to test, so the usual red-green loop is inverted: the "tests" here are **architecture guards** and **CI**, not assertions over domain logic. Three ideas drive the plan.

1. **The dependency rule is physical, not aspirational (§3).** A layer's permitted dependencies are `<ProjectReference>` entries in its `.csproj`; the compiler and MSBuild enforce them. The mechanical question §3 poses — *can `AudioClaudio.Domain` import a MIDI library, an audio device, or `DateTime.Now`?* — becomes a test: load the compiled `AudioClaudio.Domain.dll`, read its referenced assemblies, and assert none is a sibling project or a third-party audio/MIDI package. An empty Domain genuinely references only the BCL, so this passes by construction and *stays* passing only if nobody breaks the rule.

2. **Every guard is still authored test-first.** For each guard I first write a small **pure helper** (`DependencyRules.IsForbidden`, `CsprojReader.ReferencedProjectNames`) and unit-test it red-then-green against synthetic input, *then* point it at the real solution. That keeps genuine red-green on the logic while the architecture assertion rides on top of proven helpers. Where a guard reads a file that does not exist yet (`ci.yml`, `DECISIONS.md`), the red is simply the missing file.

3. **Empty but loadable.** Class libraries with their template `Class1.cs` deleted still compile to a valid assembly with zero types, and MSBuild still copies them next to the tests (they are `ProjectReference`s of the test project), so the reflection guard can load `AudioClaudio.Domain.dll` from the test output directory.

Seam left for later: the ports (`IAudioSource`, `ITranscriber`, …) live in Application and the deterministic signal generator lives in the test project — both are Step 2+; Step 0 creates the empty projects that will host them and nothing more.

**No DECISION GATE:** Step 0 lists no *Design decision* in §6, so there is no fork for Cornelius here. The first decisions are Step 2 (frame delivery model) and Step 3 (FFT: hand-rolled vs NWaves); this plan only creates the `DECISIONS.md` file they will be recorded in.

---

## Requirements coverage

| Requirement | Task(s) | Proven by (test) |
|---|---|---|
| **R0.1** — four `src/` projects + `tests/AudioClaudio.Tests`, references exactly as §3 | Task 1 (create sln + 5 projects + references), Task 3 (graph guard) | `ProjectReferenceGraphTests.Dependency_graph_matches_the_architecture`; `CsprojReaderTests.ReferencedProjectNames_reads_project_reference_includes` |
| **R0.2** — `AudioClaudio.Domain` references nothing beyond the BCL | Task 1 (Domain created empty, no references), Task 2 (reflection guard) | `DependencyRuleTests.Domain_assembly_references_only_the_bcl`; `DependencyRuleTests.IsForbidden_flags_libraries_the_domain_must_not_reference` |
| **R0.3** — `UNLICENSE`, `.gitignore`, `README.md`, empty `DECISIONS.md`, `CLAUDE.md` from the first commit | Task 5 (create `DECISIONS.md`; confirm the rest) | `RepoHygieneTests.Required_root_file_is_present`; `RepoHygieneTests.Decisions_log_records_nuget_licenses` |
| **R0.4** — CI builds and tests on every push | Task 4 (add `.github/workflows/ci.yml`) | `CiWorkflowTests.Ci_workflow_exists_and_builds_and_tests_on_push` + a green Actions run |

---

## Task 1: Scaffold the solution, five projects, and the reference graph

The bootstrap. Creates `AudioClaudio.sln`, the four `src/` projects and the one test project, wires the reference graph from §3, sets the cross-cutting build properties once, and lands a single smoke test so the runner and the `Category=Fast` trait filter are proven from the first commit.

**Files:**
- Create: `AudioClaudio.sln`
- Create: `src/AudioClaudio.Domain/AudioClaudio.Domain.csproj`
- Create: `src/AudioClaudio.Application/AudioClaudio.Application.csproj`
- Create: `src/AudioClaudio.Infrastructure/AudioClaudio.Infrastructure.csproj`
- Create: `src/AudioClaudio.Cli/AudioClaudio.Cli.csproj` (+ template `Program.cs`, the composition-root stub)
- Create: `tests/AudioClaudio.Tests/AudioClaudio.Tests.csproj`
- Create: `Directory.Build.props` (repo root — the one place that sets `net10.0`, nullable, implicit usings, warnings-as-errors)
- Create: `tests/AudioClaudio.Tests/ToolchainTests.cs`
- Delete (template noise): `src/AudioClaudio.Domain/Class1.cs`, `src/AudioClaudio.Application/Class1.cs`, `src/AudioClaudio.Infrastructure/Class1.cs`, `tests/AudioClaudio.Tests/UnitTest1.cs`

**Step 1 — Write the failing test:** author the smoke test now; it will be dropped into the test project the moment it exists in Step 3.

```csharp
// tests/AudioClaudio.Tests/ToolchainTests.cs
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
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.ToolchainTests"
```

Expected FAILURE: `MSBUILD : error MSB1003: Specify a project or solution file. The current working directory does not contain a project or solution file.` There is no solution or test project yet, so `dotnet test` cannot even build. That is the red.

**Step 3 — Minimal implementation:** create the skeleton.

Create the solution, the five projects, and remove the template stub files:

```bash
cd /Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio

dotnet new sln -n AudioClaudio

dotnet new classlib -n AudioClaudio.Domain          -o src/AudioClaudio.Domain
dotnet new classlib -n AudioClaudio.Application      -o src/AudioClaudio.Application
dotnet new classlib -n AudioClaudio.Infrastructure   -o src/AudioClaudio.Infrastructure
dotnet new console  -n AudioClaudio.Cli              -o src/AudioClaudio.Cli
dotnet new xunit    -n AudioClaudio.Tests            -o tests/AudioClaudio.Tests

# Empty projects: drop the template placeholder classes.
rm src/AudioClaudio.Domain/Class1.cs
rm src/AudioClaudio.Application/Class1.cs
rm src/AudioClaudio.Infrastructure/Class1.cs
rm tests/AudioClaudio.Tests/UnitTest1.cs
```

Wire the reference graph exactly as §3 (Domain depends on nothing):

```bash
dotnet add src/AudioClaudio.Application/AudioClaudio.Application.csproj \
  reference src/AudioClaudio.Domain/AudioClaudio.Domain.csproj

dotnet add src/AudioClaudio.Infrastructure/AudioClaudio.Infrastructure.csproj \
  reference src/AudioClaudio.Application/AudioClaudio.Application.csproj \
            src/AudioClaudio.Domain/AudioClaudio.Domain.csproj

dotnet add src/AudioClaudio.Cli/AudioClaudio.Cli.csproj \
  reference src/AudioClaudio.Domain/AudioClaudio.Domain.csproj \
            src/AudioClaudio.Application/AudioClaudio.Application.csproj \
            src/AudioClaudio.Infrastructure/AudioClaudio.Infrastructure.csproj

dotnet add tests/AudioClaudio.Tests/AudioClaudio.Tests.csproj \
  reference src/AudioClaudio.Domain/AudioClaudio.Domain.csproj \
            src/AudioClaudio.Application/AudioClaudio.Application.csproj \
            src/AudioClaudio.Infrastructure/AudioClaudio.Infrastructure.csproj \
            src/AudioClaudio.Cli/AudioClaudio.Cli.csproj
```

Add every project to the solution and add CsCheck to the test project:

```bash
dotnet sln AudioClaudio.sln add \
  src/AudioClaudio.Domain/AudioClaudio.Domain.csproj \
  src/AudioClaudio.Application/AudioClaudio.Application.csproj \
  src/AudioClaudio.Infrastructure/AudioClaudio.Infrastructure.csproj \
  src/AudioClaudio.Cli/AudioClaudio.Cli.csproj \
  tests/AudioClaudio.Tests/AudioClaudio.Tests.csproj

dotnet add tests/AudioClaudio.Tests/AudioClaudio.Tests.csproj package CsCheck
```

Set the cross-cutting build properties once, at the repo root, so every project inherits them (this is where "nullable enabled, implicit usings enabled, warnings-as-errors on" is established for the whole solution):

```xml
<!-- Directory.Build.props -->
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
```

Finally, write `tests/AudioClaudio.Tests/ToolchainTests.cs` with the smoke test from Step 1.

Note on the test runner: `dotnet new xunit` on this SDK scaffolds the classic xUnit + `Microsoft.NET.Test.Sdk` (VSTest) runner, for which `--filter Category=Fast` works as written in the constitution's Commands section. If your SDK's template instead scaffolds a Microsoft.Testing.Platform runner and `--filter Category=Fast` errors, that is a Step-0 wrinkle: record the runner substitution in `DECISIONS.md` (§1 rule 7 applies to it) and keep the `[Trait("Category", ...)]` attributes — they are portable across runners.

**Step 4 — Run to verify it passes:**

```bash
dotnet build
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.ToolchainTests"
```

Expected PASS: the solution builds with warnings-as-errors clean, and the single `Dotnet_runtime_is_at_least_net10` test passes — proving the runner and the `Category=Fast` trait path both work.

**Step 5 — Commit:** use the @gitbutler skill (never raw `git`). This is the step's first commit, so create and mark the branch first; `<ids>` come from `but status -fv` at execution time.

```bash
but status -fv                       # inspect workspace + fresh change IDs
but branch new step-00-scaffold      # this step's virtual branch
but mark step-00-scaffold            # auto-stage subsequent changes onto it
but commit step-00-scaffold \
  -m "chore: scaffold solution and four-project dependency graph" \
  --changes <ids> --status-after
```

---

## Task 2: Guard the dependency rule — Domain references only the BCL (R0.2)

Prove §3's mechanical boundary test in code. A tiny pure helper classifies an assembly name as forbidden-for-Domain; the architecture test loads the real `AudioClaudio.Domain.dll` and asserts it carries no forbidden reference. @superpowers:test-driven-development for the red-green loop on the helper.

**Files:**
- Create: `tests/AudioClaudio.Tests/TestSupport/DependencyRules.cs`
- Create: `tests/AudioClaudio.Tests/DependencyRuleTests.cs`

**Step 1 — Write the failing test:**

```csharp
// tests/AudioClaudio.Tests/DependencyRuleTests.cs
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
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.DependencyRuleTests"
```

Expected FAILURE: compile error `CS0103: The name 'DependencyRules' does not exist` (and `error CS0246` for the missing `TestSupport` namespace). The helper does not exist yet — red.

**Step 3 — Minimal implementation:**

```csharp
// tests/AudioClaudio.Tests/TestSupport/DependencyRules.cs
namespace AudioClaudio.Tests.TestSupport;

/// <summary>
/// Classifies referenced-assembly names against the §3 dependency rule for the
/// Domain layer: Domain may reference nothing but the BCL — no sibling project,
/// no audio/MIDI/DSP library.
/// </summary>
public static class DependencyRules
{
    /// <summary>Assemblies the Domain layer must never reference.</summary>
    public static readonly string[] Forbidden =
    [
        "AudioClaudio.Application",
        "AudioClaudio.Infrastructure",
        "AudioClaudio.Cli",
        "Melanchall.DryWetMidi",
        "MeltySynth",
        "PortAudioSharp",
        "PortAudioSharp2",
        "NWaves",
        "NAudio",
        "Microsoft.ML.OnnxRuntime",
    ];

    public static bool IsForbidden(string assemblyName)
    {
        var isSiblingProject =
            assemblyName.StartsWith("AudioClaudio.", StringComparison.Ordinal)
            && !assemblyName.Equals("AudioClaudio.Domain", StringComparison.Ordinal);

        var isDenylisted = Forbidden.Any(
            f => string.Equals(f, assemblyName, StringComparison.OrdinalIgnoreCase));

        return isSiblingProject || isDenylisted;
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.DependencyRuleTests"
```

Expected PASS: the helper flags every forbidden name, allows the BCL, and the empty `AudioClaudio.Domain.dll` carries zero forbidden references. R0.2 is now guarded, not merely asserted.

**Step 5 — Commit:**

```bash
but status -fv
but commit step-00-scaffold \
  -m "test: dependency-rule guard (Domain references only the BCL)" \
  --changes <ids> --status-after
```

---

## Task 3: Guard the exact reference graph (R0.1)

R0.2 covers Domain's *negative* obligation; R0.1 demands the *whole* graph be exactly §3. This task parses each `.csproj` and asserts its `<ProjectReference>` set precisely. A pure `CsprojReader` helper is unit-tested first; a `RepoPaths` helper locates the repo root from the test bin directory (reused by Tasks 4 and 5).

**Files:**
- Create: `tests/AudioClaudio.Tests/TestSupport/RepoPaths.cs`
- Create: `tests/AudioClaudio.Tests/TestSupport/CsprojReader.cs`
- Create: `tests/AudioClaudio.Tests/CsprojReaderTests.cs`
- Create: `tests/AudioClaudio.Tests/ProjectReferenceGraphTests.cs`

**Step 1 — Write the failing test:**

```csharp
// tests/AudioClaudio.Tests/CsprojReaderTests.cs
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
```

```csharp
// tests/AudioClaudio.Tests/ProjectReferenceGraphTests.cs
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests;

public class ProjectReferenceGraphTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Dependency_graph_matches_the_architecture()
    {
        // §3: Domain depends on nothing.
        AssertRefs(RepoPaths.Src("AudioClaudio.Domain"));

        // Application -> Domain
        AssertRefs(RepoPaths.Src("AudioClaudio.Application"),
            "AudioClaudio.Domain");

        // Infrastructure -> Application, Domain
        AssertRefs(RepoPaths.Src("AudioClaudio.Infrastructure"),
            "AudioClaudio.Application", "AudioClaudio.Domain");

        // Cli -> everything (composition root)
        AssertRefs(RepoPaths.Src("AudioClaudio.Cli"),
            "AudioClaudio.Application", "AudioClaudio.Domain", "AudioClaudio.Infrastructure");

        // Tests -> all four src projects
        AssertRefs(RepoPaths.Tests("AudioClaudio.Tests"),
            "AudioClaudio.Application", "AudioClaudio.Cli",
            "AudioClaudio.Domain", "AudioClaudio.Infrastructure");
    }

    private static void AssertRefs(string csprojPath, params string[] expected)
    {
        var actual = CsprojReader.ReferencedProjectNames(csprojPath);
        var want = expected.ToHashSet(StringComparer.Ordinal);

        Assert.True(want.SetEquals(actual),
            $"{Path.GetFileName(csprojPath)} references " +
            $"[{string.Join(", ", actual.OrderBy(x => x, StringComparer.Ordinal))}], " +
            $"expected [{string.Join(", ", want.OrderBy(x => x, StringComparer.Ordinal))}].");
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.CsprojReaderTests|FullyQualifiedName~AudioClaudio.Tests.ProjectReferenceGraphTests"
```

Expected FAILURE: compile error `CS0103: The name 'CsprojReader' does not exist` / `'RepoPaths' does not exist`. The helpers do not exist yet — red.

**Step 3 — Minimal implementation:**

```csharp
// tests/AudioClaudio.Tests/TestSupport/RepoPaths.cs
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
```

```csharp
// tests/AudioClaudio.Tests/TestSupport/CsprojReader.cs
using System.Xml.Linq;

namespace AudioClaudio.Tests.TestSupport;

/// <summary>
/// Reads the bare project names (no extension) declared as &lt;ProjectReference&gt;
/// in an SDK-style .csproj. SDK-style project files carry no default XML
/// namespace, so element names match without qualification.
/// </summary>
public static class CsprojReader
{
    public static ISet<string> ReferencedProjectNames(string csprojPath)
    {
        var doc = XDocument.Load(csprojPath);

        return doc.Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
            .ToHashSet(StringComparer.Ordinal);
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.CsprojReaderTests|FullyQualifiedName~AudioClaudio.Tests.ProjectReferenceGraphTests"
```

Expected PASS: the reader parses includes correctly, and every project's reference set matches §3 exactly. R0.1 is now guarded.

**Step 5 — Commit:**

```bash
but status -fv
but commit step-00-scaffold \
  -m "test: project-reference-graph guard for the four layers" \
  --changes <ids> --status-after
```

---

## Task 4: CI — build and test on every push (R0.4)

R0.4 is explicit that CI is *not* deferred to the ship step: it runs from the first commit. This task adds the GitHub Actions workflow and a guard test that asserts it exists and does the right things.

**Files:**
- Create: `.github/workflows/ci.yml`
- Create: `tests/AudioClaudio.Tests/CiWorkflowTests.cs`

**Step 1 — Write the failing test:**

```csharp
// tests/AudioClaudio.Tests/CiWorkflowTests.cs
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests;

public class CiWorkflowTests
{
    private static string WorkflowPath =>
        Path.Combine(RepoPaths.RepoRoot, ".github", "workflows", "ci.yml");

    [Fact]
    [Trait("Category", "Fast")]
    public void Ci_workflow_exists_and_builds_and_tests_on_push()
    {
        Assert.True(File.Exists(WorkflowPath), $"CI workflow missing: {WorkflowPath}");

        var yaml = File.ReadAllText(WorkflowPath);

        Assert.Contains("on:", yaml);                  // has triggers
        Assert.Contains("push", yaml);                 // ... including push (R0.4)
        Assert.Contains("actions/setup-dotnet", yaml); // installs the SDK
        Assert.Contains("10.0", yaml);                 // ... .NET 10
        Assert.Contains("dotnet build", yaml);         // builds
        Assert.Contains("dotnet test", yaml);          // and tests
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.CiWorkflowTests"
```

Expected FAILURE: assertion failure `CI workflow missing: .../.github/workflows/ci.yml`. The file does not exist yet — red.

**Step 3 — Minimal implementation:**

```yaml
# .github/workflows/ci.yml
name: CI

on:
  push:
  pull_request:

jobs:
  build-and-test:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Set up .NET 10 SDK
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Restore
        run: dotnet restore

      - name: Build
        run: dotnet build --no-restore --configuration Release

      - name: Test
        run: dotnet test --no-build --configuration Release --verbosity normal
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.CiWorkflowTests"
```

Expected PASS: the workflow file is present and contains the push trigger, the .NET 10 SDK setup, and the build + test steps. (The real proof is a green Actions run once pushed — verified at the end of the step.)

**Step 5 — Commit:**

```bash
but status -fv
but commit step-00-scaffold \
  -m "ci: GitHub Actions build+test on every push" \
  --changes <ids> --status-after
```

---

## Task 5: Repo hygiene and the decisions log (R0.3)

R0.3 requires five files at the root from the first commit. Four already exist (`UNLICENSE`, `.gitignore`, `README.md`, `CLAUDE.md`); only `DECISIONS.md` is missing. This task creates it — seeded with the NuGet license log that §1 rule 7 demands for xUnit and CsCheck — and guards all five with a test.

**Files:**
- Create: `DECISIONS.md`
- Create: `tests/AudioClaudio.Tests/RepoHygieneTests.cs`

**Step 1 — Write the failing test:**

```csharp
// tests/AudioClaudio.Tests/RepoHygieneTests.cs
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests;

public class RepoHygieneTests
{
    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("UNLICENSE")]
    [InlineData(".gitignore")]
    [InlineData("README.md")]
    [InlineData("DECISIONS.md")]
    [InlineData("CLAUDE.md")]
    public void Required_root_file_is_present(string fileName)
    {
        var path = Path.Combine(RepoPaths.RepoRoot, fileName);
        Assert.True(File.Exists(path),
            $"Required repo-hygiene file missing at root (R0.3): {fileName}");
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Decisions_log_records_nuget_licenses()
    {
        var decisions = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, "DECISIONS.md"));

        // §1 rule 7: every NuGet package's license is recorded here.
        Assert.Contains("xUnit", decisions);
        Assert.Contains("CsCheck", decisions);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.RepoHygieneTests"
```

Expected FAILURE: `Required_root_file_is_present("DECISIONS.md")` fails (`file missing at root`) and `Decisions_log_records_nuget_licenses` throws `FileNotFoundException`. `DECISIONS.md` does not exist yet — red.

**Step 3 — Minimal implementation:**

```markdown
<!-- DECISIONS.md -->
# DECISIONS

Design-decision and dependency-license log for Audio Claudio
(see `CLAUDE.md` §1 rules 2 and 7). Cornelius owns design decisions; each is
recorded here before it is implemented. The pinned stack in `CLAUDE.md` §3
governs until a step's *Design decision* is resolved below.

## NuGet dependency licenses

Every dependency SHALL be permissively licensed (MIT, Apache-2.0, BSD, MPL-2.0);
no copyleft anywhere in the graph (§1 rule 7).

| Package | Used by | Source | License | Verified |
|---|---|---|---|---|
| xUnit (`xunit`) | `tests/AudioClaudio.Tests` | `dotnet new xunit` template | Apache-2.0 | 2026-07-05 |
| `xunit.runner.visualstudio` | `tests/AudioClaudio.Tests` | template | Apache-2.0 | 2026-07-05 |
| `Microsoft.NET.Test.Sdk` | `tests/AudioClaudio.Tests` | template | MIT | 2026-07-05 |
| CsCheck | `tests/AudioClaudio.Tests` | `dotnet add package CsCheck` | MIT | 2026-07-05 |

## Design decisions

_None yet. Step 0 lists no design decision. The first forks are Step 2 (frame
delivery model: pull vs. push) and Step 3 (FFT: hand-rolled radix-2 vs. NWaves);
each will be recorded here when its step is reached._
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.RepoHygieneTests"
```

Expected PASS: all five root files are present and the decisions log names both NuGet packages. R0.3 is satisfied and guarded.

**Step 5 — Commit, then verify the whole step is green:**

```bash
# Whole-suite gate before committing (constitution Commands + §5).
dotnet format
dotnet build
dotnet test
dotnet test --filter Category=Fast     # the fast filter must be green too

but status -fv
but commit step-00-scaffold \
  -m "chore: DECISIONS.md and repo-hygiene guard" \
  --changes <ids> --status-after
```

After the commit, push the branch and confirm CI is green on the remote (this is the spec's "Done when the empty skeleton builds in CI"):

```bash
but push step-00-scaffold
# then watch the Actions run:
gh run watch $(gh run list --branch step-00-scaffold --limit 1 --json databaseId --jq '.[0].databaseId')
```

The five task-level commits above all roll up to the spec's single suggested message, `chore: scaffold solution, dependency rule, CI`; if you prefer one commit, squash them under that message via the @gitbutler skill before pushing.

---

## Verify (step exit criteria)

Restating §6 Step 0's *Done when* and the Foundation's non-negotiables as a checklist:

- [ ] `dotnet build` succeeds for the whole solution with warnings-as-errors clean.
- [ ] `dotnet test` is green; `dotnet test --filter Category=Fast` is green (all Step-0 tests are `Fast`).
- [ ] The solution contains the four `src/` projects and `tests/AudioClaudio.Tests`, with references exactly as §3 (`ProjectReferenceGraphTests`).
- [ ] `AudioClaudio.Domain` references nothing beyond the BCL — no sibling project, no audio/MIDI/DSP library (`DependencyRuleTests`).
- [ ] `UNLICENSE`, `.gitignore`, `README.md`, `DECISIONS.md`, and `CLAUDE.md` are all present at the root (`RepoHygieneTests`).
- [ ] GitHub Actions builds and tests on every push, and a real run is green on the remote (`CiWorkflowTests` + Actions).
- [ ] **The empty skeleton builds in CI** — the step's headline exit criterion.

## Definition of Done

- [ ] The solution builds (`dotnet build`) with no warnings (warnings-as-errors on).
- [ ] `dotnet format` reports no changes.
- [ ] All new tests pass (`dotnet test`), and the fast filter (`dotnet test --filter Category=Fast`) is green.
- [ ] The dependency rule is intact and physically enforced: Domain → nothing, Application → Domain, Infrastructure → Application + Domain, Cli → everything, Tests → all four (proven by `ProjectReferenceGraphTests` and `DependencyRuleTests`).
- [ ] Work committed via GitButler (the @gitbutler skill) under the spec message `chore: scaffold solution, dependency rule, CI` (or finer messages rolling up to it) — never raw `git`.
- [ ] The requirements-coverage table is fully satisfied: R0.1, R0.2, R0.3, R0.4 each have a passing guard test.
- [ ] `DECISIONS.md` exists and records the xUnit and CsCheck licenses (§1 rule 7); no design decision was required for Step 0.
- [ ] CI is green on the pushed branch (the step's exit criterion).
