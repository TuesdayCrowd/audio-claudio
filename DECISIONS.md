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

## Tooling substitutions (Step 0 wrinkles)

Two SDK-behavior surprises surfaced while scaffolding on .NET 10 SDK
(10.0.301); both are mechanical substitutions with no effect on the plan's
requirements or contracts, recorded here per the plan's own convention for
documenting such wrinkles.

- **Solution file format.** `dotnet new sln` defaults to the new XML-based
  `.slnx` format on this SDK (`dotnet new sln -h` shows `--format` defaults to
  `slnx`), not the classic `.sln` the plan and `RepoPaths.FindRepoRoot` expect.
  Regenerated explicitly with `dotnet new sln -n AudioClaudio -f sln` to produce
  the classic `AudioClaudio.sln`, matching the plan's literal paths and the
  `RepoPaths` repo-root locator (CONTRACTS.md §0) unchanged.
- **Test runner.** `dotnet new xunit` on this SDK scaffolded the classic
  xUnit + `Microsoft.NET.Test.Sdk` (VSTest) runner, *not* the
  Microsoft.Testing.Platform runner — so the constitution's
  `dotnet test --filter Category=Fast` works as written, with no runner
  substitution needed.
