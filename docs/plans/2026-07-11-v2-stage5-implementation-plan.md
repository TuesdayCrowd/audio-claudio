# v2 Stage 5 (UX, robustness, packaging) Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Turn `claudio` from a `dotnet run` demo into a tool a person installs and runs: a
hand-rolled CLI kernel that generates help/validates options/suggests typos from one declaration
per command (S5.1–S5.6), a friendly top-level error boundary (S5.5), a self-contained
prerequisite-free macOS package (S5.7–S5.9), and a polished `listen --view` live-notation page
(S5.10–S5.12) — with the docs/decisions log honest about what shipped and what remains a human
gate (R11.2, S5-accept).

**Architecture:** Four layers, landed in dependency order. **Area A (kernel)** — `OptionKind`,
`CliOption`, `CliArgument`, `CliCommand`, `ParsedArgs`, `Levenshtein`, `Suggest`, `AnsiStyler`,
`HelpRenderer`, `CommandLineApp` in `src/AudioClaudio.Cli/Cli/` — is the single source of truth
every other area consumes; its declared API (constructors, method names, accessor keys) is
normative for everything built on top of it. **Area B (terminal)** rebuilds `Program.cs` as a thin
composition root (`AppBuilder.Build(...).Run(...)`) registering all seven real commands against
Area A's actual kernel API, plus the top-level try/catch + `--debug` flag Area A explicitly leaves
for this layer. **Area D (live-view)** adds a VU meter, in-page playback, downloads, and
dark-mode/responsive styling to the existing `LiveNotationServer` SSE page, wiring into the
`listen` handler Area B registers. **Area C (packaging)** publishes a self-contained
`osx-arm64` `claudio-macos-arm64.zip` with models/SoundFont/`wwwroot/` staged beside the binary,
smoke-tested by both a real macOS packaging script and a linux-x64 CI mechanics job. **Area E
(docs)** records the six locked Stage 5 decisions in `DECISIONS.md`, rewrites the README/CLAUDE.md
install story, and writes the manual-acceptance checklist that carries R11.2 forward.

**Tech Stack:** C# / .NET 10, xUnit, no new NuGet dependency for the kernel (hand-rolled, per
`docs/plans/2026-07-11-v2-stage5-ux-packaging-design.md`'s rejection of `System.CommandLine`/
`Spectre.Console.Cli`); bash for the packaging scripts; plain HTML/CSS/JS for the live-view page
(no new frontend framework — the existing vendored OSMD bundle + hand-rolled SSE client).

---

## How this plan reconciles the area drafts

Two critic passes over the five independent area drafts found real integration breaks. This plan
fixes every blocker and major finding in the merged text below; anything not fully resolved is
listed in the run's `unresolved` output rather than silently dropped:

- **Area A's kernel API is authoritative.** Area B's original draft invented a different,
  incompatible API (`.Add`/`.Does`/`.Arg`/`.Opt`/dashed `ParsedArgs` keys). Every Area B task below
  is rewritten against Area A's real, delivered types: `CommandLineApp(toolName, toolSummary,
  version)`, `.Register(command, handler)`, `CliCommand.WithArgument/.WithOption/.WithExample`, and
  `ParsedArgs.Argument/Flag/String/Int/Double/Path/Enum(name)` keyed by the option's **stripped**
  name (no leading `--`).
- **Handlers write to the writers they're given.** Area A's handler delegate is
  `Func<ParsedArgs, TextWriter, TextWriter, int>` — every Area B handler below writes to its
  `stdout`/`stderr` parameters, never bare `Console.Out`/`Console.Error`, so tests never need to
  swap `Console.SetOut`.
- **`Program.cs` has exactly one owner of each edit.** Area B's Task 14 (composition root +
  try/catch + `--debug`) is the only task that rewrites `Program.cs`. Area C no longer touches it
  at all (its old `VersionCommand`/`case "--version"` task is deleted — Area A's kernel already
  implements `--version`, see Task 9). Area D's level/take-file wiring (Task 30) patches the
  `listen` handler **inside `AppBuilder.cs`** (registered by Task 21), not `Program.cs`, and is
  explicitly sequenced after Task 21.
- **One `--version`, one exit-code contract.** `CommandLineApp.UsageErrorExitCode` (64) is the only
  usage-error exit code anywhere in this plan; no task hardcodes a different literal. There is no
  separate `VersionCommand` class.
- **Color is computed once, consistently.** `AppBuilder.Build` constructs one `AnsiStyler` via its
  real 3-arg constructor (mirroring what `AnsiStyler.FromEnvironment` does internally), and every
  handler that prints a styled "error:" prefix uses that same instance — not a nonexistent
  `AnsiStyler.ForConsole` helper.
- **No custom option metavars.** Area B's original `.Opt(name, desc, kind, metavar)` idea is
  dropped; every option help label comes from Area A's `HelpRenderer`/`CliOption.KindDescription`
  exactly as built, so the Task 7/8 golden fixtures never disagree with what Area B declares.
- **S5.5 (the top-level try/catch + `--debug`) gets a real task.** No area draft implemented it;
  Task 14 below does, with its own test, before Area E documents it as shipped.
- **`notate` is flagged as a genuinely new command**, not a "verbatim port" of pre-existing code —
  nothing in `Program.cs` today has a `case "notate"` arm, so Task 19 treats it as new work.
- **Branch setup is uniform**: one `but branch new <area>-... && but mark ...` per area, not one
  branch per task (Area C's draft created five).

**Still open after this synthesis (see `unresolved` in this run's structured output):** Area C's
packaging scripts and Area D's HTML/CSS/JS are verified mostly by content/substring assertions, not
by actually executing the shell scripts end-to-end or driving the page in a browser automation
harness — real functional proof is the Stage 5 manual-acceptance checklist (Task 42), a human gate,
same as the project's existing precedent for hardware-dependent code (`PortAudioAudioSource.Start`,
Step 10). Task 21 (`listen`) and Task 30 (live-view wiring) similarly ship large composition-root
edits with only registration-level automated coverage, for the same hardware reason.

---

# Area A — CLI Kernel Framework (Tasks 1–12)

**Scope:** S5.1, S5.2, S5.3, S5.6, and the help-generation half of S5.4 (`--help`, per-command
help, `--version`). Builds `OptionKind`, `CliOption`, `CliArgument`, `CliCommand`, `ParsedArgs`,
`Levenshtein`, `Suggest`, `AnsiStyler`, `HelpRenderer`, `CommandLineApp` in
`src/AudioClaudio.Cli/Cli/`, unit- and golden-tested in `tests/AudioClaudio.Tests/Cli/Kernel/`,
with **zero changes to `Program.cs`** (that migration is Area B, Tasks 13–24).

**Architecture:** Each `CliCommand` declares its own arguments and options exactly once via a
fluent builder. `CommandLineApp.Run` tokenizes the process args, matches a command, validates every
option's value against its declared `OptionKind`, and dispatches to a registered handler —
returning a `ParsedArgs` whose accessors are already typed. `HelpRenderer` renders help from the
*same* `CliCommand` declarations the parser reads, so help can never drift from what is actually
accepted (S5.1). `Levenshtein`/`Suggest` back "did you mean…" (S5.2). `AnsiStyler` is a pure,
dependency-injected value so every color decision is unit-testable (S5.6).

## Branch setup (once, before Task 1)

```bash
but status -fv
but branch new stage5-area-a-kernel
but mark stage5-area-a-kernel
```

Run `dotnet format` before every commit in this area.

## Interfaces Areas B/C/D consume (authoritative — do not deviate)

- **Namespace:** `AudioClaudio.Cli.Cli` (folder `src/AudioClaudio.Cli/Cli/`).
- **`CommandLineApp(string toolName, string toolSummary, string version)`** — the 3-arg
  constructor. There is no 2-arg overload.
- **`CommandLineApp.Register(CliCommand command, Func<ParsedArgs, TextWriter, TextWriter, int> handler)`**
  registers a command and its handler together, returning `this` for chaining.
- **`CommandLineApp.Run(string[] args, TextWriter stdout, TextWriter stderr, AnsiStyler? styler = null)`**
  is the single entry point Area B's `Program.cs` calls.
- **`CommandLineApp.UsageErrorExitCode` (= 64, BSD `EX_USAGE`)** is the exit code for every
  parse/validation failure — the only such literal anywhere downstream.
- **`CommandLineApp.Commands`** exposes the registered `IReadOnlyList<CliCommand>` for
  introspection (Area B's registration tests read this).
- **`CliCommand.WithArgument(CliArgument)` / `.WithOption(CliOption)` / `.WithExample(string)`** —
  fluent, each returns `this`. `CliCommand` is **sealed**; there is no `.Add`/`.Does`/`.Arg`/`.Opt`/
  `.Handler` on it anywhere. A command's handler lives only in `CommandLineApp`'s registration.
- **`CliOption(string name, OptionKind kind, string description, bool required = false, string? defaultValue = null, IReadOnlyList<string>? enumValues = null)`**
  — `name` must start with `--`. No metavar parameter exists; help labels come from
  `CliOption.KindDescription`/`HelpRenderer` alone.
- **Option values are looked up by `CliOption.Key`** (the name with `--` stripped):
  `parsed.Double("tempo")`, `parsed.Int("key")`, `parsed.String("model")`, `parsed.Path("out-dir")`,
  `parsed.Flag("mono")`, `parsed.Argument("input.wav")` — never `parsed.Double("--tempo")`.
- **`--no-color` is a global pseudo-flag** stripped by `CommandLineApp.Run` wherever it appears in
  `args`, before command lookup or per-command validation — never a declared `CliOption`.

---

## Task 1: `OptionKind`, `CliOption`, `CliArgument` — the declaration vocabulary

**Files:**
- Create: `src/AudioClaudio.Cli/Cli/OptionKind.cs`
- Create: `src/AudioClaudio.Cli/Cli/CliOption.cs`
- Create: `src/AudioClaudio.Cli/Cli/CliArgument.cs`
- Test: `tests/AudioClaudio.Tests/Cli/Kernel/OptionAndArgumentTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class OptionAndArgumentTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void CliOption_Key_StripsLeadingDashes()
    {
        var option = new CliOption("--tempo", OptionKind.Double, "Declared tempo in BPM.");
        Assert.Equal("tempo", option.Key);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(OptionKind.Flag, "a flag")]
    [InlineData(OptionKind.String, "a value")]
    [InlineData(OptionKind.Int, "a whole number")]
    [InlineData(OptionKind.Double, "a number")]
    [InlineData(OptionKind.Path, "a file path")]
    public void CliOption_KindDescription_NamesTheExpectedShape(OptionKind kind, string expected)
    {
        var option = new CliOption("--x", kind, "irrelevant");
        Assert.Equal(expected, option.KindDescription);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void CliOption_EnumKindDescription_ListsTheChoices()
    {
        var option = new CliOption("--mode", OptionKind.Enum, "irrelevant", enumValues: new[] { "mono", "poly" });
        Assert.Equal("one of: mono, poly", option.KindDescription);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void CliOption_NameMustStartWithDoubleDash()
    {
        var ex = Assert.Throws<ArgumentException>(() => new CliOption("tempo", OptionKind.Double, "x"));
        Assert.Contains("--", ex.Message);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void CliOption_EnumKindWithoutValues_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CliOption("--mode", OptionKind.Enum, "x"));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void CliArgument_UsageToken_IsAngleBracketedWhenRequired()
    {
        var argument = new CliArgument("in.wav", "The input file.");
        Assert.Equal("<in.wav>", argument.UsageToken);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void CliArgument_UsageToken_IsSquareBracketedWhenOptional()
    {
        var argument = new CliArgument("out-dir", "Output directory.", required: false);
        Assert.Equal("[out-dir]", argument.UsageToken);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~OptionAndArgumentTests"
```

Expected FAILURE: compile error — `OptionKind`, `CliOption`, `CliArgument` do not exist yet.

**Step 3 — Minimal implementation:**

```csharp
namespace AudioClaudio.Cli.Cli;

/// <summary>
/// The value kind an option's argument must satisfy. Drives both parse-time
/// validation (S5.3) and the "expected kind" wording in error sentences and
/// generated help.
/// </summary>
public enum OptionKind
{
    /// <summary>A boolean switch; the option itself is the value (no token follows it).</summary>
    Flag,

    /// <summary>Any non-empty string, taken verbatim.</summary>
    String,

    /// <summary>A whole number, parsed invariantly.</summary>
    Int,

    /// <summary>A real number, parsed invariantly.</summary>
    Double,

    /// <summary>A filesystem path, taken verbatim — existence is a handler's concern, not the kernel's.</summary>
    Path,

    /// <summary>One of a fixed, named set of choices (<see cref="CliOption.EnumValues"/>).</summary>
    Enum,
}
```

```csharp
namespace AudioClaudio.Cli.Cli;

/// <summary>
/// One declared option (`--name`) of a <see cref="CliCommand"/>: its value kind,
/// its help text, and whether it is required. A command's options are the single
/// source of truth for both parsing/validation and generated help (S5.1).
/// </summary>
public sealed class CliOption
{
    public string Name { get; }
    public OptionKind Kind { get; }
    public string Description { get; }
    public bool Required { get; }
    public string? DefaultValue { get; }
    public IReadOnlyList<string> EnumValues { get; }

    public CliOption(
        string name,
        OptionKind kind,
        string description,
        bool required = false,
        string? defaultValue = null,
        IReadOnlyList<string>? enumValues = null)
    {
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"option name must start with '--', got '{name}'", nameof(name));
        if (kind == OptionKind.Enum && (enumValues is null || enumValues.Count == 0))
            throw new ArgumentException("an Enum-kind option requires at least one enum value", nameof(enumValues));

        Name = name;
        Kind = kind;
        Description = description;
        Required = required;
        DefaultValue = defaultValue;
        EnumValues = enumValues ?? Array.Empty<string>();
    }

    /// <summary>The option name without its leading "--", used as the lookup key in <see cref="ParsedArgs"/>.</summary>
    public string Key => Name[2..];

    /// <summary>The expected-value wording used in error sentences and help ("a number", "one of: a, b").</summary>
    public string KindDescription => Kind switch
    {
        OptionKind.Flag => "a flag",
        OptionKind.String => "a value",
        OptionKind.Int => "a whole number",
        OptionKind.Double => "a number",
        OptionKind.Path => "a file path",
        OptionKind.Enum => $"one of: {string.Join(", ", EnumValues)}",
        _ => "a value",
    };
}
```

```csharp
namespace AudioClaudio.Cli.Cli;

/// <summary>One declared positional argument of a <see cref="CliCommand"/> (e.g. `&lt;in.wav&gt;`).</summary>
public sealed class CliArgument
{
    public string Name { get; }
    public string Description { get; }
    public bool Required { get; }

    public CliArgument(string name, string description, bool required = true)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("argument name must not be empty", nameof(name));

        Name = name;
        Description = description;
        Required = required;
    }

    /// <summary>The bracketed rendering used in usage lines and help (`&lt;in.wav&gt;` or `[in.wav]`).</summary>
    public string UsageToken => Required ? $"<{Name}>" : $"[{Name}]";
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~OptionAndArgumentTests"
```

Expected PASS: all 8 cases green.

**Step 5 — Commit** (via the gitbutler skill; get `<ids>` from `but status -fv`):

```bash
dotnet format
but status -fv
but commit stage5-area-a-kernel -m "feat(cli): OptionKind, CliOption, CliArgument declaration types" --changes <ids from but status -fv> --status-after
```

---

## Task 2: `CliCommand` — the fluent command builder

**Files:**
- Create: `src/AudioClaudio.Cli/Cli/CliCommand.cs`
- Test: `tests/AudioClaudio.Tests/Cli/Kernel/CliCommandBuilderTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class CliCommandBuilderTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void NameAndSummary_AreExposedAsDeclared()
    {
        var command = new CliCommand("render", "Render a MIDI file to a WAV file.");

        Assert.Equal("render", command.Name);
        Assert.Equal("Render a MIDI file to a WAV file.", command.Summary);
        Assert.Empty(command.Arguments);
        Assert.Empty(command.Options);
        Assert.Null(command.Example);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void WithArgument_AppendsInDeclarationOrder()
    {
        var inMid = new CliArgument("in.mid", "The MIDI file to render.");
        var outWav = new CliArgument("out.wav", "Where to write the rendered audio.");

        var command = new CliCommand("render", "x")
            .WithArgument(inMid)
            .WithArgument(outWav);

        Assert.Equal(new[] { inMid, outWav }, command.Arguments);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void WithOption_AppendsInDeclarationOrder()
    {
        var tempo = new CliOption("--tempo", OptionKind.Double, "x");
        var key = new CliOption("--key", OptionKind.Int, "y");

        var command = new CliCommand("transcribe", "x")
            .WithOption(tempo)
            .WithOption(key);

        Assert.Equal(new[] { tempo, key }, command.Options);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void WithExample_SetsTheWorkedExample()
    {
        var command = new CliCommand("render", "x").WithExample("claudio render score.mid out.wav");

        Assert.Equal("claudio render score.mid out.wav", command.Example);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Fluent_ReturnsTheSameInstance_ForChaining()
    {
        var command = new CliCommand("render", "x");

        Assert.Same(command, command.WithArgument(new CliArgument("a", "d")));
        Assert.Same(command, command.WithOption(new CliOption("--o", OptionKind.String, "d")));
        Assert.Same(command, command.WithExample("e"));
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~CliCommandBuilderTests"
```

Expected FAILURE: compile error — `CliCommand` does not exist yet.

**Step 3 — Minimal implementation:**

```csharp
namespace AudioClaudio.Cli.Cli;

/// <summary>
/// One declared command (`claudio transcribe`): its positionals, its options, and
/// a worked example. Built with a small fluent API so a command declares itself
/// once (S5.1) — <see cref="CommandLineApp"/> parses and <see cref="HelpRenderer"/>
/// renders help from exactly this declaration, nothing duplicated.
/// </summary>
public sealed class CliCommand
{
    private readonly List<CliArgument> _arguments = new();
    private readonly List<CliOption> _options = new();

    public string Name { get; }
    public string Summary { get; }
    public string? Example { get; private set; }

    public IReadOnlyList<CliArgument> Arguments => _arguments;
    public IReadOnlyList<CliOption> Options => _options;

    public CliCommand(string name, string summary)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("command name must not be empty", nameof(name));

        Name = name;
        Summary = summary;
    }

    public CliCommand WithArgument(CliArgument argument)
    {
        _arguments.Add(argument);
        return this;
    }

    public CliCommand WithOption(CliOption option)
    {
        _options.Add(option);
        return this;
    }

    public CliCommand WithExample(string example)
    {
        Example = example;
        return this;
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~CliCommandBuilderTests"
```

Expected PASS: all 5 facts green.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-a-kernel -m "feat(cli): CliCommand fluent builder" --changes <ids from but status -fv> --status-after
```

---

## Task 3: `ParsedArgs` — typed accessors for a validated command invocation

**Files:**
- Create: `src/AudioClaudio.Cli/Cli/ParsedArgs.cs`
- Test: `tests/AudioClaudio.Tests/Cli/Kernel/ParsedArgsTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class ParsedArgsTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Argument_ReturnsTheDeclaredPositional()
    {
        var args = new ParsedArgs(
            new Dictionary<string, string> { ["in.wav"] = "recording.wav" },
            new Dictionary<string, object>());

        Assert.Equal("recording.wav", args.Argument("in.wav"));
        Assert.Null(args.ArgumentOrNull("out-dir"));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Flag_IsTrueOnlyWhenPresent()
    {
        var present = new ParsedArgs(
            new Dictionary<string, string>(), new Dictionary<string, object> { ["mono"] = true });
        var absent = new ParsedArgs(new Dictionary<string, string>(), new Dictionary<string, object>());

        Assert.True(present.Flag("mono"));
        Assert.False(absent.Flag("mono"));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void TypedAccessors_ReturnTheStoredValue_OrNullWhenAbsent()
    {
        var args = new ParsedArgs(
            new Dictionary<string, string>(),
            new Dictionary<string, object>
            {
                ["tempo"] = 132.5,
                ["key"] = -1,
                ["soundfont"] = "/fixtures/soundfont/GeneralUser-GS.sf2",
                ["model"] = "transkun",
            });

        Assert.Equal(132.5, args.Double("tempo"));
        Assert.Equal(-1, args.Int("key"));
        Assert.Equal("/fixtures/soundfont/GeneralUser-GS.sf2", args.Path("soundfont"));
        Assert.Equal("transkun", args.Enum("model"));
        Assert.Null(args.Double("missing"));
        Assert.Null(args.Int("missing"));
        Assert.Null(args.String("missing"));
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~ParsedArgsTests"
```

Expected FAILURE: compile error — `ParsedArgs` does not exist yet.

**Step 3 — Minimal implementation:**

```csharp
namespace AudioClaudio.Cli.Cli;

/// <summary>
/// The result of successfully parsing and validating one command's arguments and
/// options: positionals by name, and options already converted to their declared
/// <see cref="OptionKind"/> — a handler never re-parses a raw token.
/// </summary>
public sealed class ParsedArgs
{
    private readonly IReadOnlyDictionary<string, string> _arguments;
    private readonly IReadOnlyDictionary<string, object> _options;

    public ParsedArgs(
        IReadOnlyDictionary<string, string> arguments,
        IReadOnlyDictionary<string, object> options)
    {
        _arguments = arguments;
        _options = options;
    }

    public string Argument(string name) => _arguments[name];

    public string? ArgumentOrNull(string name) =>
        _arguments.TryGetValue(name, out var value) ? value : null;

    public bool Flag(string name) =>
        _options.TryGetValue(name, out var value) && value is bool flag && flag;

    public string? String(string name) =>
        _options.TryGetValue(name, out var value) ? (string)value : null;

    public int? Int(string name) =>
        _options.TryGetValue(name, out var value) ? (int)value : null;

    public double? Double(string name) =>
        _options.TryGetValue(name, out var value) ? (double)value : null;

    public string? Path(string name) =>
        _options.TryGetValue(name, out var value) ? (string)value : null;

    public string? Enum(string name) =>
        _options.TryGetValue(name, out var value) ? (string)value : null;
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~ParsedArgsTests"
```

Expected PASS: all 3 facts green.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-a-kernel -m "feat(cli): ParsedArgs typed accessors" --changes <ids from but status -fv> --status-after
```

---

## Task 4: `Levenshtein` — the edit-distance metric

**Files:**
- Create: `src/AudioClaudio.Cli/Cli/Levenshtein.cs`
- Test: `tests/AudioClaudio.Tests/Cli/Kernel/LevenshteinTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class LevenshteinTests
{
    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("", "", 0)]
    [InlineData("abc", "abc", 0)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("--modl", "--model", 1)]
    [InlineData("lsiten", "listen", 2)]
    public void Distance_MatchesTheClassicEditDistance(string a, string b, int expected)
    {
        Assert.Equal(expected, Levenshtein.Distance(a, b));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Distance_IsSymmetric()
    {
        Assert.Equal(
            Levenshtein.Distance("transcribe", "trascribe"),
            Levenshtein.Distance("trascribe", "transcribe"));
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~LevenshteinTests"
```

Expected FAILURE: compile error — `Levenshtein` does not exist yet.

**Step 3 — Minimal implementation:**

```csharp
namespace AudioClaudio.Cli.Cli;

/// <summary>
/// Edit distance between two strings (single-character insert/delete/substitute),
/// the metric behind "did you mean…" suggestions (S5.2).
/// </summary>
public static class Levenshtein
{
    public static int Distance(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var lenA = a.Length;
        var lenB = b.Length;
        var previous = new int[lenB + 1];
        var current = new int[lenB + 1];

        for (var j = 0; j <= lenB; j++)
            previous[j] = j;

        for (var i = 1; i <= lenA; i++)
        {
            current[0] = i;
            for (var j = 1; j <= lenB; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[lenB];
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~LevenshteinTests"
```

Expected PASS: all 8 cases green.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-a-kernel -m "feat(cli): Levenshtein edit distance" --changes <ids from but status -fv> --status-after
```

---

## Task 5: `Suggest` — nearest-match "did you mean…"

**Files:**
- Create: `src/AudioClaudio.Cli/Cli/Suggest.cs`
- Test: `tests/AudioClaudio.Tests/Cli/Kernel/SuggestTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class SuggestTests
{
    private static readonly string[] Commands = { "transcribe", "listen", "play", "render", "evaluate" };

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("trascribe", "transcribe")]
    [InlineData("lsiten", "listen")]
    [InlineData("evaluete", "evaluate")]
    [InlineData("rendr", "render")]
    public void NearestMatch_PinsTheSuggestionForCommonTypos(string typo, string expected)
    {
        Assert.Equal(expected, Suggest.NearestMatch(typo, Commands));
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("frobnicate")]
    [InlineData("xyzzyplugh")]
    public void NearestMatch_ReturnsNull_WhenNothingIsClose(string input)
    {
        Assert.Null(Suggest.NearestMatch(input, Commands));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void NearestMatch_OnOptionNames_PinsTheDesignDocExample()
    {
        var options = new[] { "--tempo", "--key", "--model" };

        Assert.Equal("--model", Suggest.NearestMatch("--modl", options));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void NearestMatch_BreaksTiesOnEarliestCandidate()
    {
        // Both "play" and "stay" are edit-distance 1 from "slay"; "play" is declared first.
        var candidates = new[] { "play", "stay" };

        Assert.Equal("play", Suggest.NearestMatch("slay", candidates));
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~SuggestTests"
```

Expected FAILURE: compile error — `Suggest` does not exist yet.

**Step 3 — Minimal implementation:**

```csharp
namespace AudioClaudio.Cli.Cli;

/// <summary>
/// Finds the closest candidate to an unrecognized token, for "did you mean…"
/// error sentences (S5.2). Ties break on the earliest candidate in input order,
/// so the same inputs always produce the same suggestion (determinism, CLAUDE.md §4).
/// </summary>
public static class Suggest
{
    /// <summary>The maximum edit distance still considered a plausible typo.</summary>
    public const int DefaultMaxDistance = 2;

    public static string? NearestMatch(
        string input, IEnumerable<string> candidates, int maxDistance = DefaultMaxDistance)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(candidates);

        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var candidate in candidates)
        {
            var distance = Levenshtein.Distance(input, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best is not null && bestDistance <= maxDistance ? best : null;
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~SuggestTests"
```

Expected PASS: all 8 cases green.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-a-kernel -m "feat(cli): Suggest nearest-match did-you-mean" --changes <ids from but status -fv> --status-after
```

---

## Task 6: `AnsiStyler` — dependency-injected color

**Files:**
- Create: `src/AudioClaudio.Cli/Cli/AnsiStyler.cs`
- Test: `tests/AudioClaudio.Tests/Cli/Kernel/AnsiStylerTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class AnsiStylerTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Enabled_WhenInteractiveAndNoOverridesSuppressIt()
    {
        var styler = new AnsiStyler(interactiveTerminal: true, noColorEnvSet: false, noColorFlag: false);

        Assert.True(styler.Enabled);
        Assert.Equal("[1;36mUsage:[0m", styler.Heading("Usage:"));
        Assert.Equal("[1;31merror[0m", styler.Error("error"));
        Assert.Equal("[33mfoo.wav[0m", styler.Token("foo.wav"));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Disabled_WhenNotAnInteractiveTerminal()
    {
        var styler = new AnsiStyler(interactiveTerminal: false, noColorEnvSet: false, noColorFlag: false);

        Assert.False(styler.Enabled);
        Assert.Equal("Usage:", styler.Heading("Usage:"));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Disabled_WhenNoColorEnvironmentVariableIsSet()
    {
        var styler = new AnsiStyler(interactiveTerminal: true, noColorEnvSet: true, noColorFlag: false);

        Assert.False(styler.Enabled);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Disabled_WhenNoColorFlagIsPassed()
    {
        var styler = new AnsiStyler(interactiveTerminal: true, noColorEnvSet: false, noColorFlag: true);

        Assert.False(styler.Enabled);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void FromEnvironment_IsDisabled_ForAnyNonConsoleWriter()
    {
        var writer = new StringWriter();

        Assert.False(AnsiStyler.FromEnvironment(writer, noColorFlag: false).Enabled);
        Assert.False(AnsiStyler.FromEnvironment(writer, noColorFlag: true).Enabled);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AnsiStylerTests"
```

Expected FAILURE: compile error — `AnsiStyler` does not exist yet.

**Step 3 — Minimal implementation:**

```csharp
namespace AudioClaudio.Cli.Cli;

/// <summary>
/// Applies tasteful ANSI color to headings, error keywords, and offending tokens —
/// and nothing else. Disabled (styling becomes a no-op) on a non-interactive stream,
/// when NO_COLOR is set, or when the caller passes --no-color (S5.6). The three
/// inputs are constructor parameters, not sensed internally, so every combination
/// is directly unit-testable.
/// </summary>
public sealed class AnsiStyler
{
    private const string Reset = "[0m";

    public bool Enabled { get; }

    public AnsiStyler(bool interactiveTerminal, bool noColorEnvSet, bool noColorFlag)
    {
        Enabled = interactiveTerminal && !noColorEnvSet && !noColorFlag;
    }

    /// <summary>Reads the ambient environment (console redirection, NO_COLOR) for real usage.</summary>
    public static AnsiStyler FromEnvironment(TextWriter output, bool noColorFlag)
    {
        var isConsoleOut = ReferenceEquals(output, Console.Out);
        var isConsoleError = ReferenceEquals(output, Console.Error);
        var interactiveTerminal = isConsoleOut
            ? !Console.IsOutputRedirected
            : isConsoleError && !Console.IsErrorRedirected;

        return new AnsiStyler(
            interactiveTerminal,
            noColorEnvSet: !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")),
            noColorFlag: noColorFlag);
    }

    public string Heading(string text) => Style(text, "[1;36m");
    public string Error(string text) => Style(text, "[1;31m");
    public string Token(string text) => Style(text, "[33m");

    private string Style(string text, string code) => Enabled ? $"{code}{text}{Reset}" : text;
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AnsiStylerTests"
```

Expected PASS: all 5 facts green.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-a-kernel -m "feat(cli): AnsiStyler injectable color" --changes <ids from but status -fv> --status-after
```

---

## Task 7: `HelpRenderer.RenderTopLevel` — the top-level page, golden-pinned

**Files:**
- Create: `src/AudioClaudio.Cli/Cli/HelpRenderer.cs`
- Create: `tests/AudioClaudio.Tests/Cli/Kernel/SampleCli.cs`
- Create: `tests/AudioClaudio.Tests/Cli/Kernel/HelpRendererTopLevelTests.cs`
- Create: `fixtures/golden/cli/top-level-help.txt`

**Step 1 — Write the failing test (and its fixture helper):**

```csharp
// tests/AudioClaudio.Tests/Cli/Kernel/SampleCli.cs
using AudioClaudio.Cli.Cli;

namespace AudioClaudio.Tests.Cli.Kernel;

/// <summary>
/// A tiny two-command "claudio" declaration shared by the CLI kernel tests in this
/// folder, so each task's test asserts only the behavior it targets.
/// </summary>
internal static class SampleCli
{
    public const string ToolName = "claudio";
    public const string ToolSummary = "a real-time piano transcriber";
    public const string Version = "2.1.0";

    public static readonly CliCommand Transcribe =
        new CliCommand("transcribe", "Transcribe an audio file to notation.")
            .WithArgument(new CliArgument("in.wav", "The audio file to transcribe."))
            .WithOption(new CliOption("--tempo", OptionKind.Double, "Declared tempo in BPM."))
            .WithOption(new CliOption("--key", OptionKind.Int, "Key signature as a fifths count.", required: true))
            .WithOption(new CliOption(
                "--model", OptionKind.String, "Which transcription engine to use.", defaultValue: "basicpitch"));

    public static readonly CliCommand Render =
        new CliCommand("render", "Render a MIDI file to a WAV file.")
            .WithArgument(new CliArgument("in.mid", "The MIDI file to render."))
            .WithArgument(new CliArgument("out.wav", "Where to write the rendered audio."))
            .WithOption(new CliOption("--soundfont", OptionKind.Path, "SoundFont (.sf2) to render with."))
            .WithExample("claudio render score.mid out.wav");
}
```

```csharp
// tests/AudioClaudio.Tests/Cli/Kernel/HelpRendererTopLevelTests.cs
using AudioClaudio.Cli.Cli;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class HelpRendererTopLevelTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void RenderTopLevel_MatchesTheGoldenFragment()
    {
        var commands = new[] { SampleCli.Transcribe, SampleCli.Render };
        var styler = new AnsiStyler(interactiveTerminal: false, noColorEnvSet: false, noColorFlag: false);

        var actual = HelpRenderer.RenderTopLevel(SampleCli.ToolName, SampleCli.ToolSummary, commands, styler);

        var expected = File.ReadAllText(RepoPaths.Fixture("golden", "cli", "top-level-help.txt"));
        Assert.Equal(expected, actual);
    }
}
```

Create the golden fixture with this **exact** content (ends with a single trailing newline):

```
claudio — a real-time piano transcriber

Usage:
  claudio <command> [options]

Commands:
  transcribe   Transcribe an audio file to notation.
  render       Render a MIDI file to a WAV file.

Run 'claudio <command> --help' for details on a command.
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~HelpRendererTopLevelTests"
```

Expected FAILURE: compile error — `HelpRenderer` does not exist yet.

**Step 3 — Minimal implementation:**

```csharp
using System.Text;

namespace AudioClaudio.Cli.Cli;

/// <summary>
/// Renders top-level and per-command help directly from a <see cref="CliCommand"/>'s
/// declaration — the same one <see cref="CommandLineApp"/> parses against — so help
/// can never disagree with what is actually accepted (S5.1). Lines are joined with a
/// literal "\n" (never <see cref="Environment.NewLine"/>) so golden fixtures are
/// byte-identical on every OS.
/// </summary>
public static class HelpRenderer
{
    public static string RenderTopLevel(
        string toolName, string toolSummary, IReadOnlyList<CliCommand> commands, AnsiStyler styler)
    {
        var sb = new StringBuilder();
        Line(sb, $"{toolName} — {toolSummary}");
        Line(sb, string.Empty);
        Line(sb, styler.Heading("Usage:"));
        Line(sb, $"  {toolName} <command> [options]");
        Line(sb, string.Empty);
        Line(sb, styler.Heading("Commands:"));

        var width = commands.Count == 0 ? 0 : commands.Max(c => c.Name.Length);
        foreach (var command in commands)
            Line(sb, $"  {command.Name.PadRight(width)}   {command.Summary}");

        Line(sb, string.Empty);
        Line(sb, $"Run '{toolName} <command> --help' for details on a command.");

        return sb.ToString();
    }

    public static string RenderCommand(string toolName, CliCommand command, AnsiStyler styler)
    {
        var sb = new StringBuilder();
        Line(sb, styler.Heading("Usage:"));

        var usage = new StringBuilder($"  {toolName} {command.Name}");
        foreach (var argument in command.Arguments)
            usage.Append(' ').Append(argument.UsageToken);
        if (command.Options.Count > 0)
            usage.Append(" [options]");
        Line(sb, usage.ToString());

        if (command.Arguments.Count > 0)
        {
            Line(sb, string.Empty);
            Line(sb, styler.Heading("Arguments:"));
            var width = command.Arguments.Max(a => a.UsageToken.Length);
            foreach (var argument in command.Arguments)
                Line(sb, $"  {argument.UsageToken.PadRight(width)}   {argument.Description}");
        }

        if (command.Options.Count > 0)
        {
            Line(sb, string.Empty);
            Line(sb, styler.Heading("Options:"));
            var labels = command.Options.Select(OptionLabel).ToList();
            var width = labels.Max(l => l.Length);
            for (var i = 0; i < command.Options.Count; i++)
            {
                var option = command.Options[i];
                var suffix = option.Required
                    ? " (required)"
                    : option.DefaultValue is null ? string.Empty : $" (default: {option.DefaultValue})";
                Line(sb, $"  {labels[i].PadRight(width)}   {option.Description}{suffix}");
            }
        }

        if (command.Example is not null)
        {
            Line(sb, string.Empty);
            Line(sb, styler.Heading("Example:"));
            Line(sb, $"  {command.Example}");
        }

        return sb.ToString();
    }

    private static string OptionLabel(CliOption option) => option.Kind switch
    {
        OptionKind.Flag => option.Name,
        OptionKind.Enum => $"{option.Name} <{string.Join("|", option.EnumValues)}>",
        _ => $"{option.Name} <{option.Kind.ToString().ToUpperInvariant()}>",
    };

    private static void Line(StringBuilder sb, string text) => sb.Append(text).Append('\n');
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~HelpRendererTopLevelTests"
```

Expected PASS: 1/1 green (byte-exact match against the golden fixture).

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-a-kernel -m "feat(cli): HelpRenderer top-level page + golden fixture" --changes <ids from but status -fv> --status-after
```

---

## Task 8: `HelpRenderer.RenderCommand` — per-command help, golden-pinned

**Files:**
- Test: `tests/AudioClaudio.Tests/Cli/Kernel/HelpRendererCommandTests.cs`
- Create: `fixtures/golden/cli/render-command-help.txt`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Cli.Cli;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class HelpRendererCommandTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void RenderCommand_MatchesTheGoldenFragment_ForRender()
    {
        var styler = new AnsiStyler(interactiveTerminal: false, noColorEnvSet: false, noColorFlag: false);

        var actual = HelpRenderer.RenderCommand(SampleCli.ToolName, SampleCli.Render, styler);

        var expected = File.ReadAllText(RepoPaths.Fixture("golden", "cli", "render-command-help.txt"));
        Assert.Equal(expected, actual);
    }
}
```

Create the golden fixture with this **exact** content:

```
Usage:
  claudio render <in.mid> <out.wav> [options]

Arguments:
  <in.mid>    The MIDI file to render.
  <out.wav>   Where to write the rendered audio.

Options:
  --soundfont <PATH>   SoundFont (.sf2) to render with.

Example:
  claudio render score.mid out.wav
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~HelpRendererCommandTests"
```

Expected FAILURE: `FileNotFoundException` — the fixture file does not exist yet until created above.

**Step 3 — Minimal implementation:** none — `HelpRenderer.RenderCommand` already exists from Task
7; this task only adds the golden fixture and its test. (If the assertion fails against a
correctly-created fixture, the bug is in Task 7's `RenderCommand`/`OptionLabel`.)

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~HelpRendererCommandTests"
```

Expected PASS: 1/1 green.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-a-kernel -m "test(cli): HelpRenderer per-command golden fixture" --changes <ids from but status -fv> --status-after
```

---

## Task 9: `CommandLineApp` — registration, parse, and dispatch

**Files:**
- Create: `src/AudioClaudio.Cli/Cli/CommandLineApp.cs`
- Modify: `tests/AudioClaudio.Tests/Cli/Kernel/SampleCli.cs` (add `BuildApp`)
- Create: `tests/AudioClaudio.Tests/Cli/Kernel/CommandLineAppDispatchTests.cs`

**Step 1 — Write the failing test (and extend the fixture helper):**

Append to `SampleCli.cs`:

```csharp
    /// <summary>Builds a fresh app with both commands registered against recording handlers, so a
    /// test can assert both the dispatch outcome and exactly what the handler received.</summary>
    public static (CommandLineApp App, List<(string Command, ParsedArgs Args)> Invocations) BuildApp(
        Func<ParsedArgs, TextWriter, TextWriter, int>? transcribeHandler = null,
        Func<ParsedArgs, TextWriter, TextWriter, int>? renderHandler = null)
    {
        var invocations = new List<(string, ParsedArgs)>();

        var app = new CommandLineApp(ToolName, ToolSummary, Version)
            .Register(Transcribe, (parsed, stdout, stderr) =>
            {
                invocations.Add(("transcribe", parsed));
                return transcribeHandler?.Invoke(parsed, stdout, stderr) ?? 0;
            })
            .Register(Render, (parsed, stdout, stderr) =>
            {
                invocations.Add(("render", parsed));
                return renderHandler?.Invoke(parsed, stdout, stderr) ?? 0;
            });

        return (app, invocations);
    }
```

```csharp
// tests/AudioClaudio.Tests/Cli/Kernel/CommandLineAppDispatchTests.cs
using AudioClaudio.Cli.Cli;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class CommandLineAppDispatchTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void NoArgs_And_TopLevelHelp_PrintTheSameTopLevelPage()
    {
        var (app, _) = SampleCli.BuildApp();
        var expected = File.ReadAllText(RepoPaths.Fixture("golden", "cli", "top-level-help.txt"));

        foreach (var args in new[] { Array.Empty<string>(), new[] { "--help" } })
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = app.Run(args, stdout, stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(expected, stdout.ToString());
            Assert.Equal(string.Empty, stderr.ToString());
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Version_PrintsToolNameAndVersion()
    {
        var (app, _) = SampleCli.BuildApp();
        var stdout = new StringWriter();

        var exitCode = app.Run(new[] { "--version" }, stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Equal("claudio 2.1.0\n", stdout.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void CommandHelp_PrintsThePerCommandPage_AndNeverCallsTheHandler()
    {
        var (app, invocations) = SampleCli.BuildApp();
        var stdout = new StringWriter();

        var exitCode = app.Run(new[] { "render", "--help" }, stdout, new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Equal(
            File.ReadAllText(RepoPaths.Fixture("golden", "cli", "render-command-help.txt")), stdout.ToString());
        Assert.Empty(invocations);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void ValidCommand_ParsesTypedOptionsAndDispatchesToItsHandler()
    {
        var (app, invocations) = SampleCli.BuildApp();

        var exitCode = app.Run(
            new[] { "transcribe", "recording.wav", "--key", "-1", "--tempo", "132.5" },
            new StringWriter(), new StringWriter());

        Assert.Equal(0, exitCode);
        var (command, parsed) = Assert.Single(invocations);
        Assert.Equal("transcribe", command);
        Assert.Equal("recording.wav", parsed.Argument("in.wav"));
        Assert.Equal(-1, parsed.Int("key"));
        Assert.Equal(132.5, parsed.Double("tempo"));
        Assert.Equal("basicpitch", parsed.String("model")); // declared default, never typed by the user
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void HandlerReturnValue_BecomesTheProcessExitCode()
    {
        var (app, _) = SampleCli.BuildApp(renderHandler: (_, _, _) => 3);

        var exitCode = app.Run(
            new[] { "render", "score.mid", "out.wav" }, new StringWriter(), new StringWriter());

        Assert.Equal(3, exitCode);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~CommandLineAppDispatchTests"
```

Expected FAILURE: compile error — `CommandLineApp` does not exist yet.

**Step 3 — Minimal implementation:**

```csharp
using System.Globalization;

namespace AudioClaudio.Cli.Cli;

/// <summary>
/// The composition root for the hand-rolled CLI kernel: commands register
/// themselves once (name, arguments, options, handler) via <see cref="Register"/>;
/// <see cref="Run"/> parses, validates, and dispatches against exactly that
/// declaration — the same one <see cref="HelpRenderer"/> reads — so help can never
/// disagree with what the parser accepts (S5.1).
/// </summary>
public sealed class CommandLineApp
{
    /// <summary>Exit code for any usage error: unknown command/flag, a bad value, a missing argument.</summary>
    public const int UsageErrorExitCode = 64;

    private const string NoColorFlag = "--no-color";

    private readonly List<CliCommand> _commands = new();
    private readonly Dictionary<string, Func<ParsedArgs, TextWriter, TextWriter, int>> _handlers =
        new(StringComparer.Ordinal);

    public string ToolName { get; }
    public string ToolSummary { get; }
    public string Version { get; }

    public IReadOnlyList<CliCommand> Commands => _commands;

    public CommandLineApp(string toolName, string toolSummary, string version)
    {
        ToolName = toolName;
        ToolSummary = toolSummary;
        Version = version;
    }

    /// <summary>Declares one command and the handler that runs when it is dispatched.</summary>
    public CommandLineApp Register(CliCommand command, Func<ParsedArgs, TextWriter, TextWriter, int> handler)
    {
        _commands.Add(command);
        _handlers[command.Name] = handler;
        return this;
    }

    /// <summary>
    /// Parses <paramref name="args"/>, validates them against the matched command's
    /// declaration, and dispatches to its handler. Never throws on user input — every
    /// failure is a stderr sentence plus <see cref="UsageErrorExitCode"/>.
    /// </summary>
    public int Run(string[] args, TextWriter stdout, TextWriter stderr, AnsiStyler? styler = null)
    {
        var noColorFlag = args.Contains(NoColorFlag);
        var filtered = args.Where(a => a != NoColorFlag).ToArray();
        styler ??= AnsiStyler.FromEnvironment(stdout, noColorFlag);

        if (filtered.Length == 0 || filtered[0] is "--help" or "-h")
        {
            stdout.Write(HelpRenderer.RenderTopLevel(ToolName, ToolSummary, _commands, styler));
            return 0;
        }

        if (filtered[0] == "--version")
        {
            stdout.Write($"{ToolName} {Version}\n");
            return 0;
        }

        var commandName = filtered[0];
        var command = _commands.Find(c => c.Name == commandName);
        if (command is null)
        {
            stderr.Write($"error: {UnknownToken("command", commandName, _commands.Select(c => c.Name))}\n");
            return UsageErrorExitCode;
        }

        var rest = filtered[1..];
        if (rest.Length > 0 && rest[0] is "--help" or "-h")
        {
            stdout.Write(HelpRenderer.RenderCommand(ToolName, command, styler));
            return 0;
        }

        var (parsed, error) = Validate(command, rest);
        if (error is not null)
        {
            stderr.Write($"error: {error}\n");
            return UsageErrorExitCode;
        }

        return _handlers[command.Name](parsed!, stdout, stderr);
    }

    /// <summary>
    /// Parses and kind-validates one command's tokens against its declaration.
    /// Exposed directly so validation can be unit-tested without a handler or the
    /// rest of <see cref="Run"/>.
    /// </summary>
    public (ParsedArgs? Args, string? Error) Validate(CliCommand command, string[] tokens)
    {
        var options = new Dictionary<string, object>();
        var positionals = new List<string>();

        var i = 0;
        while (i < tokens.Length)
        {
            var token = tokens[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(token);
                i++;
                continue;
            }

            var option = command.Options.FirstOrDefault(o => o.Name == token);
            if (option is null)
                return (null, UnknownToken("option", token, command.Options.Select(o => o.Name)));

            if (option.Kind == OptionKind.Flag)
            {
                options[option.Key] = true;
                i++;
                continue;
            }

            if (i + 1 >= tokens.Length)
                return (null, $"{option.Name} expects a value.");

            var (value, kindError) = Convert(option, tokens[i + 1]);
            if (kindError is not null)
                return (null, kindError);

            options[option.Key] = value!;
            i += 2;
        }

        // Required positionals are assumed contiguous at the front of a command's declaration.
        var requiredArgumentCount = command.Arguments.Count(a => a.Required);
        if (positionals.Count < requiredArgumentCount)
            return (null, $"missing required argument {command.Arguments[positionals.Count].UsageToken}.");

        if (positionals.Count > command.Arguments.Count)
            return (null, $"unexpected argument '{positionals[command.Arguments.Count]}'.");

        foreach (var option in command.Options)
        {
            if (options.ContainsKey(option.Key))
                continue;

            if (option.Required)
                return (null, $"{option.Name} is required.");

            if (option.DefaultValue is not null)
            {
                var (value, kindError) = Convert(option, option.DefaultValue);
                if (kindError is not null)
                    throw new InvalidOperationException(
                        $"{option.Name}'s declared default '{option.DefaultValue}' fails its own kind check: {kindError}");
                options[option.Key] = value!;
            }
        }

        var arguments = new Dictionary<string, string>();
        for (var a = 0; a < positionals.Count; a++)
            arguments[command.Arguments[a].Name] = positionals[a];

        return (new ParsedArgs(arguments, options), null);
    }

    private static (object? Value, string? Error) Convert(CliOption option, string raw)
    {
        switch (option.Kind)
        {
            case OptionKind.String:
            case OptionKind.Path:
                return (raw, null);

            case OptionKind.Int:
                return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                    ? (i, null)
                    : (null, $"{option.Name} expects {option.KindDescription}, got '{raw}'.");

            case OptionKind.Double:
                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? (d, null)
                    : (null, $"{option.Name} expects {option.KindDescription}, got '{raw}'.");

            case OptionKind.Enum:
                var match = option.EnumValues.FirstOrDefault(
                    v => string.Equals(v, raw, StringComparison.OrdinalIgnoreCase));
                return match is not null
                    ? (match, null)
                    : (null, $"{option.Name} expects {option.KindDescription}, got '{raw}'.");

            default:
                throw new InvalidOperationException($"unhandled option kind {option.Kind}");
        }
    }

    private static string UnknownToken(string kind, string token, IEnumerable<string> candidates)
    {
        var suggestion = Suggest.NearestMatch(token, candidates);
        return suggestion is null
            ? $"unknown {kind} '{token}'."
            : $"unknown {kind} '{token}'. Did you mean '{suggestion}'?";
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~CommandLineAppDispatchTests"
```

Expected PASS: all 5 facts green.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-a-kernel -m "feat(cli): CommandLineApp parse, validate, and dispatch" --changes <ids from but status -fv> --status-after
```

---

## Task 10: `CommandLineApp` — unknown command/flag suggestions (S5.2)

**Files:**
- Test: `tests/AudioClaudio.Tests/Cli/Kernel/CommandLineAppSuggestionTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class CommandLineAppSuggestionTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void UnknownCommand_WithACloseMatch_SuggestsIt()
    {
        var (app, _) = SampleCli.BuildApp();
        var stderr = new StringWriter();

        var exitCode = app.Run(new[] { "trascribe" }, new StringWriter(), stderr);

        Assert.Equal(CommandLineApp.UsageErrorExitCode, exitCode);
        Assert.Equal("error: unknown command 'trascribe'. Did you mean 'transcribe'?\n", stderr.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void UnknownCommand_WithNothingClose_OmitsTheSuggestion()
    {
        var (app, _) = SampleCli.BuildApp();
        var stderr = new StringWriter();

        var exitCode = app.Run(new[] { "frobnicate" }, new StringWriter(), stderr);

        Assert.Equal(CommandLineApp.UsageErrorExitCode, exitCode);
        Assert.Equal("error: unknown command 'frobnicate'.\n", stderr.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void UnknownOption_WithACloseMatch_SuggestsIt()
    {
        var (app, _) = SampleCli.BuildApp();
        var stderr = new StringWriter();

        var exitCode = app.Run(
            new[] { "transcribe", "recording.wav", "--key", "0", "--modl", "transkun" },
            new StringWriter(), stderr);

        Assert.Equal(CommandLineApp.UsageErrorExitCode, exitCode);
        Assert.Equal("error: unknown option '--modl'. Did you mean '--model'?\n", stderr.ToString());
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~CommandLineAppSuggestionTests"
```

Expected: with Task 9 already in place, these **pass immediately** — `Run`/`Validate`/
`UnknownToken` already implement S5.2. (To see red first, temporarily swap one expected string for
a wrong one, watch it fail, then restore.)

**Step 3 — Minimal implementation:** none — covered by Task 9.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~CommandLineAppSuggestionTests"
```

Expected PASS: all 3 facts green.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-a-kernel -m "test(cli): unknown command/option suggestion coverage (S5.2)" --changes <ids from but status -fv> --status-after
```

---

## Task 11: `CommandLineApp` — kind/required validation sentences (S5.3)

**Files:**
- Test: `tests/AudioClaudio.Tests/Cli/Kernel/CommandLineAppValidationTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class CommandLineAppValidationTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void MalformedNumber_NamesTheFlagTheKindAndTheToken()
    {
        var (app, _) = SampleCli.BuildApp();
        var stderr = new StringWriter();

        var exitCode = app.Run(
            new[] { "transcribe", "recording.wav", "--key", "0", "--tempo", "abc" },
            new StringWriter(), stderr);

        Assert.Equal(CommandLineApp.UsageErrorExitCode, exitCode);
        Assert.Equal("error: --tempo expects a number, got 'abc'.\n", stderr.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void MissingValue_NamesTheFlag()
    {
        var (app, _) = SampleCli.BuildApp();
        var stderr = new StringWriter();

        var exitCode = app.Run(new[] { "transcribe", "recording.wav", "--key" }, new StringWriter(), stderr);

        Assert.Equal(CommandLineApp.UsageErrorExitCode, exitCode);
        Assert.Equal("error: --key expects a value.\n", stderr.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void MissingRequiredOption_NamesTheFlag()
    {
        var (app, _) = SampleCli.BuildApp();
        var stderr = new StringWriter();

        var exitCode = app.Run(new[] { "transcribe", "recording.wav" }, new StringWriter(), stderr);

        Assert.Equal(CommandLineApp.UsageErrorExitCode, exitCode);
        Assert.Equal("error: --key is required.\n", stderr.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void MissingRequiredArgument_NamesItInAngleBrackets()
    {
        var (app, _) = SampleCli.BuildApp();
        var stderr = new StringWriter();

        var exitCode = app.Run(new[] { "transcribe" }, new StringWriter(), stderr);

        Assert.Equal(CommandLineApp.UsageErrorExitCode, exitCode);
        Assert.Equal("error: missing required argument <in.wav>.\n", stderr.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void UnexpectedExtraArgument_NamesTheOffendingToken()
    {
        var (app, _) = SampleCli.BuildApp();
        var stderr = new StringWriter();

        var exitCode = app.Run(
            new[] { "render", "score.mid", "out.wav", "extra.txt" }, new StringWriter(), stderr);

        Assert.Equal(CommandLineApp.UsageErrorExitCode, exitCode);
        Assert.Equal("error: unexpected argument 'extra.txt'.\n", stderr.ToString());
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~CommandLineAppValidationTests"
```

Expected: with Task 9 already in place, these **pass immediately** — `Validate`/`Convert` already
implement every S5.3 branch exercised here.

**Step 3 — Minimal implementation:** none — covered by Task 9.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~CommandLineAppValidationTests"
```

Expected PASS: all 5 facts green.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-a-kernel -m "test(cli): value/required validation sentence coverage (S5.3)" --changes <ids from but status -fv> --status-after
```

---

## Task 12: `CommandLineApp` — color wiring end-to-end (S5.6)

**Files:**
- Test: `tests/AudioClaudio.Tests/Cli/Kernel/CommandLineAppColorTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class CommandLineAppColorTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void InjectedInteractiveStyler_ColorsTheHelpHeadings()
    {
        var (app, _) = SampleCli.BuildApp();
        var stdout = new StringWriter();
        var interactive = new AnsiStyler(interactiveTerminal: true, noColorEnvSet: false, noColorFlag: false);

        app.Run(Array.Empty<string>(), stdout, new StringWriter(), styler: interactive);

        Assert.Contains("[1;36mUsage:[0m", stdout.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void DefaultStyler_NeverColorsOutput_ForANonConsoleWriter()
    {
        var (app, _) = SampleCli.BuildApp();
        var stdout = new StringWriter();

        app.Run(Array.Empty<string>(), stdout, new StringWriter());

        Assert.DoesNotContain("[", stdout.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void NoColorFlag_IsStrippedBeforeCommandParsing_WhereverItAppears()
    {
        var (appBefore, invocationsBefore) = SampleCli.BuildApp();
        var (appAfter, invocationsAfter) = SampleCli.BuildApp();

        var exitBefore = appBefore.Run(
            new[] { "--no-color", "render", "score.mid", "out.wav" }, new StringWriter(), new StringWriter());
        var exitAfter = appAfter.Run(
            new[] { "render", "score.mid", "out.wav", "--no-color" }, new StringWriter(), new StringWriter());

        Assert.Equal(0, exitBefore);
        Assert.Equal(0, exitAfter);
        Assert.Single(invocationsBefore);
        Assert.Single(invocationsAfter);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~CommandLineAppColorTests"
```

Expected: with Task 9's `Run` already stripping `--no-color` globally and accepting an injectable
`styler`, these **pass immediately**.

**Step 3 — Minimal implementation:** none — covered by Task 9.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~CommandLineAppColorTests"
```

Expected PASS: all 3 facts green.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-a-kernel -m "test(cli): color wiring and --no-color stripping coverage (S5.6)" --changes <ids from but status -fv> --status-after
```

**Final verification for Area A:**

```bash
dotnet build
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Cli.Kernel"
dotnet test --filter Category=Fast
dotnet test
dotnet format --verify-no-changes
```

- [ ] All 12 tasks' tests green individually and as the full `Cli.Kernel` namespace filter.
- [ ] Full `dotnet test` still green (no regression to the pre-existing suite).
- [ ] `Program.cs` untouched — `but diff` shows no changes; the kernel is additive only.
- [ ] `HelpRenderer` never uses `Environment.NewLine`/`AppendLine()`.

---

# Area B — Terminal: Program.cs Migration (Tasks 13–24)

**Scope:** S5.1–S5.6 at the integrated app level (real commands, not a sample fixture), plus S5.5
(the top-level try/catch + `--debug`), which no other area implements. Rebuilds `Program.cs` as a
thin composition root (`AppBuilder.Build(...).Run(...)`) registering the seven real commands
(`transcribe`, `notate`, `render`, `play`, `evaluate`, `evaluate-audio`, `listen`) against **Area
A's actual kernel API** — `CommandLineApp(toolName, toolSummary, version)`, `.Register(command,
handler)`, `CliCommand.WithArgument/.WithOption/.WithExample`, `ParsedArgs.Argument/Flag/String/
Int/Double/Path/Enum` keyed by the stripped option name. Every handler has the real signature
`(ParsedArgs, TextWriter stdout, TextWriter stderr) => int` and writes only to the writers it is
given — never bare `Console.Out`/`Console.Error` — so tests never swap `Console.SetOut`.

`notate` is a **new** command introduced in this stage (nothing in today's `Program.cs` has a
`case "notate"` arm) — Task 19 builds it from scratch against the same grand-staff quantizer/writer
`transcribe` already uses, not a migration of pre-existing logic.

## Branch setup (once, before Task 13)

```bash
but status -fv
but branch new stage5-area-b-terminal
but mark stage5-area-b-terminal
```

Run `dotnet format` before every commit in this area. **Depends on Area A (Tasks 1–12) being
merged/available** — every task below compiles only against Area A's real kernel types.

---

## Task 13: `AppBuilder` — register all seven commands (stub handlers)

Puts one source of truth for the command surface in place before any handler logic moves. Every
command/option is declared here, in one place, so `--help` can never drift from what the parser
accepts (S5.1). Handlers are stubs (`NotImplementedException`) — later tasks replace them one at a
time.

**Files:**
- Create: `src/AudioClaudio.Cli/AppBuilder.cs`
- Create: `tests/AudioClaudio.Tests/Cli/Kernel/AppBuilderTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System.Linq;
using System.Text;
using AudioClaudio.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class AppBuilderTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Build_registers_exactly_the_seven_commands()
    {
        var app = AppBuilder.Build(new StringBuilder(), noColor: true);

        var names = app.Commands.Select(c => c.Name).OrderBy(n => n).ToArray();

        Assert.Equal(
            new[] { "evaluate", "evaluate-audio", "listen", "notate", "play", "render", "transcribe" },
            names);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("transcribe", new[] { "--tempo", "--out-dir", "--note-names", "--mono", "--model", "--key",
        "--onset-threshold", "--frame-threshold", "--min-note-len", "--legato", "--coarse-rhythm", "--triplets" })]
    [InlineData("notate", new[] { "--out-dir", "--tempo", "--key", "--note-names", "--triplets" })]
    [InlineData("render", new[] { "--soundfont" })]
    [InlineData("play", new[] { "--soundfont" })]
    [InlineData("evaluate", new[] { "--onset-tolerance-ms", "--align", "--warp" })]
    [InlineData("evaluate-audio", new string[0])]
    [InlineData("listen", new[] { "--tempo", "--out-dir", "--view", "--record", "--skip-silence", "--note-names" })]
    public void Each_command_declares_exactly_its_option_surface(string commandName, string[] expectedOptions)
    {
        var app = AppBuilder.Build(new StringBuilder(), noColor: true);
        var cmd = app.Commands.Single(c => c.Name == commandName);

        var actual = cmd.Options.Select(o => o.Name).OrderBy(n => n).ToArray();
        var expected = expectedOptions.OrderBy(n => n).ToArray();

        Assert.Equal(expected, actual);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AppBuilderTests"
```

Expected FAILURE: `CS0246: The type or namespace name 'AppBuilder' could not be found`.

**Step 3 — Minimal implementation:**

```csharp
using System.Text;
using AudioClaudio.Cli.Cli;

namespace AudioClaudio.Cli;

/// <summary>
/// The composition root for the CLI-kernel migration (v2 Stage 5): builds the
/// <see cref="CommandLineApp"/> and registers all seven commands with their full option
/// surface. Handlers are wired one command at a time by later tasks (14–21); <see
/// cref="Program"/>'s top-level statements do nothing but call <see cref="Build"/>, wrap
/// <c>Run</c> in the top-level try/catch (Task 14), and return its exit code — every other
/// line of app behavior lives here so it is unit-testable in-process.
/// </summary>
public static class AppBuilder
{
    public static string Version { get; } =
        typeof(AppBuilder).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Builds one <see cref="AnsiStyler"/> for handler-printed messages (e.g. a styled
    /// "error:" prefix on a missing-file sentence), computed the same way
    /// <see cref="AnsiStyler.FromEnvironment"/> computes it internally for the kernel's own
    /// help/error rendering — so a handler's own color decisions never disagree with the
    /// kernel's (S5.6). Kept internal so Tasks 15+ can reuse it without recomputing.
    /// </summary>
    internal static AnsiStyler ConsoleStyler(bool noColor) =>
        new(
            interactiveTerminal: !Console.IsOutputRedirected,
            noColorEnvSet: !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")),
            noColorFlag: noColor);

    public static CommandLineApp Build(StringBuilder logBuffer, bool noColor)
    {
        ArgumentNullException.ThrowIfNull(logBuffer);
        var styler = ConsoleStyler(noColor);
        var app = new CommandLineApp("claudio", "a real-time piano transcriber", Version);

        var transcribe = new CliCommand("transcribe", "Transcribe a WAV recording to MIDI + MusicXML.")
            .WithArgument(new CliArgument("input.wav", "the recording to transcribe"))
            .WithOption(new CliOption("--tempo", OptionKind.Double, "declared tempo in BPM (auto-estimated if omitted)"))
            .WithOption(new CliOption("--out-dir", OptionKind.Path, "directory to write raw.mid/score.mid/score.musicxml", defaultValue: "."))
            .WithOption(new CliOption("--note-names", OptionKind.Flag, "print a scientific-pitch-name lyric under each note"))
            .WithOption(new CliOption("--mono", OptionKind.Flag, "use the monophonic YIN pipeline instead of the polyphonic default"))
            .WithOption(new CliOption("--model", OptionKind.String, "explicit model path, or 'transkun' for the Transkun engine"))
            .WithOption(new CliOption("--key", OptionKind.Int, "override the auto-detected key signature (sharps +, flats -)"))
            .WithOption(new CliOption("--onset-threshold", OptionKind.Double, "polyphonic onset activation threshold"))
            .WithOption(new CliOption("--frame-threshold", OptionKind.Double, "polyphonic sustained-frame activation threshold"))
            .WithOption(new CliOption("--min-note-len", OptionKind.Int, "polyphonic flicker floor in frames"))
            .WithOption(new CliOption("--legato", OptionKind.Flag, "(--mono) opt into legato note recovery"))
            .WithOption(new CliOption("--coarse-rhythm", OptionKind.Flag, "(--mono) floor note values at an eighth"))
            .WithOption(new CliOption("--triplets", OptionKind.Flag, "engrave eighth-note triplets"))
            .WithExample("claudio transcribe song.wav --out-dir out");
        app.Register(transcribe, (p, stdout, stderr) => throw new NotImplementedException("wired in Task 20"));

        var notate = new CliCommand("notate", "Engrave an existing MIDI file as a grand-staff score.")
            .WithArgument(new CliArgument("input.mid", "the MIDI file to notate"))
            .WithOption(new CliOption("--out-dir", OptionKind.Path, "directory to write score.mid/score.musicxml", defaultValue: "."))
            .WithOption(new CliOption("--tempo", OptionKind.Double, "declared tempo in BPM (auto-estimated if omitted)"))
            .WithOption(new CliOption("--key", OptionKind.Int, "override the auto-detected key signature"))
            .WithOption(new CliOption("--note-names", OptionKind.Flag, "print a scientific-pitch-name lyric under each note"))
            .WithOption(new CliOption("--triplets", OptionKind.Flag, "engrave eighth-note triplets"))
            .WithExample("claudio notate performance.mid --out-dir out");
        app.Register(notate, (p, stdout, stderr) => throw new NotImplementedException("wired in Task 19"));

        var render = new CliCommand("render", "Render a MIDI file to a deterministic WAV.")
            .WithArgument(new CliArgument("input.mid", "the MIDI file to render"))
            .WithArgument(new CliArgument("output.wav", "the WAV file to write"))
            .WithOption(new CliOption("--soundfont", OptionKind.Path, "explicit SoundFont path (auto-discovered otherwise)"))
            .WithExample("claudio render song.mid song.wav");
        app.Register(render, (p, stdout, stderr) => throw new NotImplementedException("wired in Task 15"));

        var play = new CliCommand("play", "Play a MIDI file through the default audio device.")
            .WithArgument(new CliArgument("input.mid", "the MIDI file to play"))
            .WithOption(new CliOption("--soundfont", OptionKind.Path, "explicit SoundFont path (auto-discovered otherwise)"))
            .WithExample("claudio play song.mid");
        app.Register(play, (p, stdout, stderr) => throw new NotImplementedException("wired in Task 15"));

        var evaluate = new CliCommand("evaluate", "Score a candidate transcription against a reference note-set.")
            .WithArgument(new CliArgument("candidate.mid", "the transcription to evaluate"))
            .WithArgument(new CliArgument("reference.mid", "the ground-truth reference"))
            .WithOption(new CliOption("--onset-tolerance-ms", OptionKind.Double, "onset matching tolerance in ms (default 50)"))
            .WithOption(new CliOption("--align", OptionKind.Flag, "cancel a global tempo ratio before scoring"))
            .WithOption(new CliOption("--warp", OptionKind.Flag, "DTW-warp to also remove local rubato (wins over --align)"))
            .WithExample("claudio evaluate out/score.mid reference.mid --align");
        app.Register(evaluate, (p, stdout, stderr) => throw new NotImplementedException("wired in Task 16"));

        var evaluateAudio = new CliCommand("evaluate-audio", "Compare two WAVs by pitch-content (chroma) similarity.")
            .WithArgument(new CliArgument("original.wav", "the original recording"))
            .WithArgument(new CliArgument("reproduction.wav", "the re-synthesized recording"))
            .WithExample("claudio evaluate-audio input.wav recreation.wav");
        app.Register(evaluateAudio, (p, stdout, stderr) => throw new NotImplementedException("wired in Task 17"));

        var listen = new CliCommand("listen", "Transcribe live from the microphone.")
            .WithOption(new CliOption("--tempo", OptionKind.Double, "declared tempo in BPM (auto-estimated if omitted)"))
            .WithOption(new CliOption("--out-dir", OptionKind.Path, "directory to write raw.mid/score.mid/score.musicxml", defaultValue: "."))
            .WithOption(new CliOption("--view", OptionKind.Flag, "open a live sheet-music browser view"))
            .WithOption(new CliOption("--record", OptionKind.Flag, "also write input.wav + recreation.wav"))
            .WithOption(new CliOption("--skip-silence", OptionKind.Flag, "collapse pauses > 500ms (implies --record)"))
            .WithOption(new CliOption("--note-names", OptionKind.Flag, "print a scientific-pitch-name lyric under each note"))
            .WithExample("claudio listen --view --record");
        app.Register(listen, (p, stdout, stderr) => throw new NotImplementedException("wired in Task 21"));

        return app;
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AppBuilderTests"
```

Expected PASS: `Passed! - Failed: 0, Passed: 8`.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-b-terminal -m "feat(cli): register the seven-command surface on the hand-rolled kernel" --changes <ids> --status-after
```

---

## Task 14: Testable `Main`, top-level try/catch + `--debug` (S5.5), `--no-color` threading

Shrinks `Program.cs` to build → run and adds the one behavior no other area implements: an
unhandled exception anywhere in a handler must print `error: unexpected error: <message> (run with
--debug for the stack trace)` and exit non-zero — never a raw .NET stack trace — unless `--debug`
was passed, in which case the full stack trace prints. This is the **only** task that edits
`Program.cs`; no later task in any area touches it again (Area D's live-view wiring, Task 30,
patches the `listen` handler inside `AppBuilder.cs` instead).

**Files:**
- Modify: `src/AudioClaudio.Cli/Program.cs`
- Create: `src/AudioClaudio.Cli/Composition/TeeTextWriter.cs` (if not already present from a prior
  step — a `TextWriter` that mirrors every write into a `StringBuilder` for `log.txt`)
- Create: `tests/AudioClaudio.Tests/Cli/Kernel/TopLevelErrorBoundaryTests.cs`

**Step 1 — Write the failing test.** The boundary is a small, directly-testable function
independent of `Console`/`Environment.Exit`, so it is unit-tested without spawning a process:

```csharp
using System.IO;
using AudioClaudio.Cli;
using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class TopLevelErrorBoundaryTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void An_unexpected_exception_prints_a_friendly_sentence_not_a_stack_trace()
    {
        var stderr = new StringWriter();
        var styler = new AnsiStyler(interactiveTerminal: false, noColorEnvSet: false, noColorFlag: false);

        int code = TopLevelErrorBoundary.Run(
            () => throw new InvalidOperationException("the ONNX session failed to load"),
            stderr, styler, debug: false);

        Assert.Equal(1, code);
        Assert.Equal("error: unexpected error: the ONNX session failed to load (run with --debug for the stack trace)\n", stderr.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Debug_flag_prints_the_full_stack_trace_instead()
    {
        var stderr = new StringWriter();
        var styler = new AnsiStyler(interactiveTerminal: false, noColorEnvSet: false, noColorFlag: false);

        int code = TopLevelErrorBoundary.Run(
            () => throw new InvalidOperationException("boom"), stderr, styler, debug: true);

        Assert.Equal(1, code);
        Assert.Contains("InvalidOperationException", stderr.ToString());
        Assert.Contains("boom", stderr.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void A_clean_run_passes_the_inner_exit_code_through_unchanged()
    {
        var stderr = new StringWriter();
        var styler = new AnsiStyler(interactiveTerminal: false, noColorEnvSet: false, noColorFlag: false);

        int code = TopLevelErrorBoundary.Run(() => 42, stderr, styler, debug: false);

        Assert.Equal(42, code);
        Assert.Equal(string.Empty, stderr.ToString());
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~TopLevelErrorBoundaryTests"
```

Expected FAILURE: `CS0246: The type or namespace name 'TopLevelErrorBoundary' could not be found`.

**Step 3 — Minimal implementation:**

```csharp
using AudioClaudio.Cli.Cli;

namespace AudioClaudio.Cli;

/// <summary>
/// S5.5: the single place an unhandled exception becomes a sentence instead of a raw
/// .NET stack trace. Wraps <see cref="CommandLineApp.Run"/> (or any other exit-code-
/// producing delegate) so no user-reachable path in the packaged binary ever prints a
/// stack trace unless the user explicitly asked for one via --debug.
/// </summary>
public static class TopLevelErrorBoundary
{
    public const int UnexpectedErrorExitCode = 1;

    public static int Run(Func<int> body, TextWriter stderr, AnsiStyler styler, bool debug)
    {
        try
        {
            return body();
        }
        catch (Exception ex)
        {
            stderr.Write(debug
                ? $"{styler.Error("error:")} unexpected error:\n{ex}\n"
                : $"{styler.Error("error:")} unexpected error: {ex.Message} (run with --debug for the stack trace)\n");
            return UnexpectedErrorExitCode;
        }
    }
}
```

Replace `src/AudioClaudio.Cli/Program.cs` wholesale:

```csharp
using System.Text;
using AudioClaudio.Cli;
using AudioClaudio.Cli.Composition;

var logBuffer = new StringBuilder();
Console.SetOut(new TeeTextWriter(Console.Out, logBuffer));

bool noColor = Array.IndexOf(args, "--no-color") >= 0;
bool debug = Array.IndexOf(args, "--debug") >= 0;
var styler = AppBuilder.ConsoleStyler(noColor);

return TopLevelErrorBoundary.Run(
    () => AppBuilder.Build(logBuffer, noColor).Run(args, Console.Out, Console.Error),
    Console.Error, styler, debug);
```

If `src/AudioClaudio.Cli/Composition/TeeTextWriter.cs` does not already exist from an earlier
step, add it:

```csharp
using System.Text;

namespace AudioClaudio.Cli.Composition;

/// <summary>Mirrors every write to an inner <see cref="TextWriter"/> into a <see cref="StringBuilder"/>,
/// so a command's console output can also be captured verbatim into log.txt.</summary>
public sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _inner;
    private readonly StringBuilder _log;

    public TeeTextWriter(TextWriter inner, StringBuilder log)
    {
        _inner = inner;
        _log = log;
    }

    public override Encoding Encoding => _inner.Encoding;

    public override void Write(char value)
    {
        _inner.Write(value);
        _log.Append(value);
    }

    public override void Write(string? value)
    {
        _inner.Write(value);
        _log.Append(value);
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~TopLevelErrorBoundaryTests"
```

Expected PASS: `Passed! - Failed: 0, Passed: 3`.

**Step 5 — Run the full CLI test folder** (every command handler still throws
`NotImplementedException` at this point — expected, fixed by Tasks 15–21):

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Cli"
```

**Step 6 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-b-terminal -m "feat(cli): top-level try/catch + --debug (S5.5); shrink Program.cs to build-then-run" --changes <ids> --status-after
```

---

## Task 15: Migrate `render` and `play`

**Files:**
- Modify: `src/AudioClaudio.Cli/AppBuilder.cs`
- Create: `tests/AudioClaudio.Tests/Cli/Kernel/RenderPlayHandlerTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System.IO;
using AudioClaudio.Cli;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class RenderPlayHandlerTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Render_command_writes_a_wav_matching_the_golden_within_tolerance()
    {
        var app = AppBuilder.Build(new System.Text.StringBuilder(), noColor: true);
        string midiPath = RepoPaths.Fixture("golden", "two-bar.mid");
        string outPath = Path.Combine(Path.GetTempPath(), $"claudio-render-{Guid.NewGuid():N}.wav");

        try
        {
            var stdout = new StringWriter();
            int code = app.Run(new[] { "render", midiPath, outPath }, stdout, new StringWriter());

            Assert.Equal(0, code);
            Assert.True(File.Exists(outPath));
            byte[] actualWav = File.ReadAllBytes(outPath);
            byte[] expectedWav = File.ReadAllBytes(RepoPaths.Fixture("golden", "two-bar.wav"));
            WavGoldenComparer.AssertWithinTolerance(expectedWav, actualWav);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Render_reports_a_friendly_error_for_a_missing_input_file()
    {
        var app = AppBuilder.Build(new System.Text.StringBuilder(), noColor: true);
        var stderr = new StringWriter();

        int code = app.Run(new[] { "render", "does-not-exist.mid", "out.wav" }, new StringWriter(), stderr);

        Assert.Equal(1, code);
        Assert.Contains("input file 'does-not-exist.mid' not found", stderr.ToString());
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~RenderPlayHandlerTests"
```

Expected FAILURE: `System.NotImplementedException : wired in Task 15`.

**Step 3 — Implement.** Add a shared file-existence helper near the top of `AppBuilder`, and the
`using`s the handlers need:

```csharp
using AudioClaudio.Cli.Commands;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi;
using AudioClaudio.Infrastructure.Synthesis;
```

```csharp
private static readonly SampleRate Rate = new(44100);

/// <summary>S5.5's handler-level counterpart: turn a missing input file into one clean
/// sentence via the handler's OWN stderr writer, before any reader/adapter touches the path.</summary>
private static bool TryRequireFile(string path, TextWriter stderr, AnsiStyler styler)
{
    if (File.Exists(path)) return true;
    stderr.Write($"{styler.Error("error:")} input file '{path}' not found\n");
    return false;
}
```

Replace the `render`/`play` stub registrations:

```csharp
app.Register(render, (p, stdout, stderr) =>
{
    if (!TryRequireFile(p.Argument("input.mid"), stderr, styler)) return 1;
    var notes = MidiFileReader.ReadFile(p.Argument("input.mid"), Rate).Events;
    var synth = new MeltySynthSynthesizer(
        AudioClaudio.Cli.Composition.SoundFontLocator.Resolve(p.Path("soundfont")));
    RenderCommand.RenderToWav(notes, synth, Rate, p.Argument("output.wav"));
    return 0;
});
```

```csharp
app.Register(play, (p, stdout, stderr) =>
{
    if (!TryRequireFile(p.Argument("input.mid"), stderr, styler)) return 1;
    var notes = MidiFileReader.ReadFile(p.Argument("input.mid"), Rate).Events;
    var synth = new MeltySynthSynthesizer(
        AudioClaudio.Cli.Composition.SoundFontLocator.Resolve(p.Path("soundfont")));
    PlayCommand.Play(notes, synth, Rate);
    return 0;
});
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~RenderPlayHandlerTests"
```

Expected PASS: `Passed! - Failed: 0, Passed: 2`.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-b-terminal -m "feat(cli): migrate render/play to kernel handlers; friendly missing-file error" --changes <ids> --status-after
```

---

## Task 16: Migrate `evaluate`

**Files:**
- Modify: `src/AudioClaudio.Cli/AppBuilder.cs`
- Create: `tests/AudioClaudio.Tests/Cli/Kernel/EvaluateHandlerTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System.IO;
using AudioClaudio.Cli;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class EvaluateHandlerTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Evaluate_reports_perfect_F1_for_an_identical_candidate()
    {
        var app = AppBuilder.Build(new System.Text.StringBuilder(), noColor: true);
        var rate = TwoBarMelody.Rate;
        var writer = new DryWetMidiWriter();
        string midiPath = Path.Combine(Path.GetTempPath(), $"claudio-eval-{Guid.NewGuid():N}.mid");
        using (var f = File.Create(midiPath))
            writer.Write(TwoBarMelody.Notes(rate), new Tempo(120), f);

        try
        {
            var stdout = new StringWriter();
            int code = app.Run(new[] { "evaluate", midiPath, midiPath }, stdout, new StringWriter());

            Assert.Equal(0, code);
            Assert.Contains("F1:              100.0%", stdout.ToString());
        }
        finally
        {
            if (File.Exists(midiPath)) File.Delete(midiPath);
        }
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~EvaluateHandlerTests"
```

Expected FAILURE: `System.NotImplementedException : wired in Task 16`.

**Step 3 — Implement.** `EvaluateCommand.Run`'s `print` callback is the handler's own `stdout`
writer — no `Console.SetOut` swap needed anywhere:

```csharp
using AudioClaudio.Domain.Evaluation;
```

```csharp
app.Register(evaluate, (p, stdout, stderr) =>
{
    if (!TryRequireFile(p.Argument("candidate.mid"), stderr, styler)) return 1;
    if (!TryRequireFile(p.Argument("reference.mid"), stderr, styler)) return 1;

    var candidate = MidiFileReader.ReadFile(p.Argument("candidate.mid"), Rate).Events;
    var reference = MidiFileReader.ReadFile(p.Argument("reference.mid"), Rate).Events;
    double tolMs = p.Double("onset-tolerance-ms") ?? 50.0;

    bool warp = p.Flag("warp");
    bool align = p.Flag("align");
    IReadOnlyList<NoteEvent> evalCandidate = candidate;
    if (warp)
    {
        evalCandidate = OnsetAlignment.DtwWarp(candidate, reference);
        stdout.WriteLine("(candidate DTW-warped to the reference timeline — local rubato removed)");
    }
    else if (align)
    {
        evalCandidate = OnsetAlignment.GlobalScale(candidate, reference);
        stdout.WriteLine("(candidate globally time-aligned to the reference span)");
    }

    EvaluateCommand.Run(evalCandidate, reference, new NoteMatchOptions(tolMs / 1000.0), stdout.WriteLine);
    return 0;
});
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~EvaluateHandlerTests"
```

Expected PASS: `Passed! - Failed: 0, Passed: 1`.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-b-terminal -m "feat(cli): migrate evaluate to a kernel handler" --changes <ids> --status-after
```

---

## Task 17: Migrate `evaluate-audio`

**Files:**
- Modify: `src/AudioClaudio.Cli/AppBuilder.cs`
- Create: `tests/AudioClaudio.Tests/Cli/Kernel/EvaluateAudioHandlerTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System.IO;
using AudioClaudio.Cli;
using AudioClaudio.Domain;
using AudioClaudio.Tests.Signals;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class EvaluateAudioHandlerTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void EvaluateAudio_reports_perfect_similarity_for_a_wav_against_itself()
    {
        var app = AppBuilder.Build(new System.Text.StringBuilder(), noColor: true);
        var rate = new SampleRate(44100);
        float[] pcm = SignalGenerator.HarmonicStack(new Pitch(69).Frequency(), (int)(1.0 * rate.Hz), rate, partials: 6, decay: 1.0);
        string wav = Path.Combine(Path.GetTempPath(), $"claudio-eval-audio-{Guid.NewGuid():N}.wav");
        WavWriter.WriteMonoFile(wav, pcm, rate);

        var stdout = new StringWriter();
        int code;
        try
        {
            code = app.Run(new[] { "evaluate-audio", wav, wav }, stdout, new StringWriter());
        }
        finally
        {
            if (File.Exists(wav)) File.Delete(wav);
        }

        Assert.Equal(0, code);
        Assert.Contains("100.0%", stdout.ToString());
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~EvaluateAudioHandlerTests"
```

Expected FAILURE: `System.NotImplementedException : wired in Task 17`.

**Step 3 — Implement:**

```csharp
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Domain.Spectral;
```

```csharp
app.Register(evaluateAudio, (p, stdout, stderr) =>
{
    if (!TryRequireFile(p.Argument("original.wav"), stderr, styler)) return 1;
    if (!TryRequireFile(p.Argument("reproduction.wav"), stderr, styler)) return 1;

    const int FrameSize = 4096, Hop = 2048;
    using var audioA = WavAudioSource.FromFile(p.Argument("original.wav"), new FrameParameters(FrameSize, Hop));
    using var audioB = WavAudioSource.FromFile(p.Argument("reproduction.wav"), new FrameParameters(FrameSize, Hop));
    var chromaA = Chromagram.FromFrames(audioA.Frames, FrameSize);
    var chromaB = Chromagram.FromFrames(audioB.Frames, FrameSize);
    double similarity = ChromaSimilarity.Compare(chromaA, chromaB);
    stdout.WriteLine($"Chroma (pitch-content) similarity: {similarity:P1}  ({chromaA.Count} vs {chromaB.Count} frames)");
    return 0;
});
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~EvaluateAudioHandlerTests"
```

Expected PASS: `Passed! - Failed: 0, Passed: 1`.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-b-terminal -m "feat(cli): migrate evaluate-audio to a kernel handler" --changes <ids> --status-after
```

---

## Task 18: Adapt `KeyOption`, `TranscribeModeResolver`, `PolyDecoderOptions` to `ParsedArgs`

These three existing `Commands/*` helpers read raw `string[] args`. `transcribe`/`notate` now hand
handlers a validated `ParsedArgs` instead, so each helper gets a new, additive overload keyed by
Area A's real, **stripped** option names (existing `string[]`-based methods and their tests are
untouched).

**Files:**
- Modify: `src/AudioClaudio.Cli/Commands/KeyOption.cs`
- Modify: `src/AudioClaudio.Cli/Commands/TranscribeMode.cs`
- Modify: `src/AudioClaudio.Cli/Commands/PolyDecoderOptions.cs`
- Create: `tests/AudioClaudio.Tests/Cli/Kernel/ParsedArgsAdaptersTests.cs`

**Step 1 — Write the failing test.** `ParsedArgs` has no public constructor, so a real instance is
captured by running a tiny throwaway command through the real kernel:

```csharp
using AudioClaudio.Cli.Cli;
using AudioClaudio.Cli.Commands;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class ParsedArgsAdaptersTests
{
    private static ParsedArgs Capture(CliCommand cmd, string[] args)
    {
        ParsedArgs? captured = null;
        var app = new CommandLineApp("test-tool", "x", "0.0.0");
        app.Register(cmd, (p, stdout, stderr) => { captured = p; return 0; });
        int code = app.Run(args, new System.IO.StringWriter(), new System.IO.StringWriter());
        Assert.Equal(0, code);
        return captured!;
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void TranscribeModeResolver_resolves_mono_from_ParsedArgs()
    {
        var cmd = new CliCommand("transcribe", "x")
            .WithArgument(new CliArgument("input.wav", "x"))
            .WithOption(new CliOption("--mono", OptionKind.Flag, "x"));

        var parsed = Capture(cmd, new[] { "song.wav", "--mono" });

        Assert.Equal(TranscribeMode.Monophonic, TranscribeModeResolver.Resolve(parsed));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void TranscribeModeResolver_defaults_to_polyphonic_from_ParsedArgs()
    {
        var cmd = new CliCommand("transcribe", "x")
            .WithArgument(new CliArgument("input.wav", "x"))
            .WithOption(new CliOption("--mono", OptionKind.Flag, "x"));

        var parsed = Capture(cmd, new[] { "song.wav" });

        Assert.Equal(TranscribeMode.Polyphonic, TranscribeModeResolver.Resolve(parsed));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void PolyDecoderOptions_reads_thresholds_from_ParsedArgs()
    {
        var cmd = new CliCommand("transcribe", "x")
            .WithArgument(new CliArgument("input.wav", "x"))
            .WithOption(new CliOption("--onset-threshold", OptionKind.Double, "x"))
            .WithOption(new CliOption("--frame-threshold", OptionKind.Double, "x"))
            .WithOption(new CliOption("--min-note-len", OptionKind.Int, "x"));

        var parsed = Capture(cmd, new[] { "song.wav", "--onset-threshold", "0.7", "--min-note-len", "5" });
        var o = PolyDecoderOptions.FromArgs(parsed);

        Assert.Equal(0.7, o.OnsetThreshold);
        Assert.Equal(5, o.MinNoteLenFrames);
        Assert.Equal(AudioClaudio.Domain.Polyphony.NoteDecoderOptions.Default.FrameThreshold, o.FrameThreshold);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void KeyOption_Validate_rejects_an_out_of_range_fifths()
    {
        Assert.True(KeyOption.Validate(null, out string? noError));
        Assert.Null(noError);

        Assert.False(KeyOption.Validate(8, out string? error));
        Assert.Contains("--key", error);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~ParsedArgsAdaptersTests"
```

Expected FAILURE: `CS1501: No overload for method 'Resolve' takes 1 arguments` (similarly for
`FromArgs`/`Validate`).

**Step 3 — Implement the three additive overloads**, each keyed by the **stripped** option name
(`"mono"`, `"onset-threshold"`, never `"--mono"`).

`TranscribeMode.cs` — add inside `TranscribeModeResolver`:

```csharp
public static TranscribeMode Resolve(AudioClaudio.Cli.Cli.ParsedArgs parsed)
{
    ArgumentNullException.ThrowIfNull(parsed);
    return parsed.Flag("mono") ? TranscribeMode.Monophonic : TranscribeMode.Polyphonic;
}
```

`PolyDecoderOptions.cs` — add inside `PolyDecoderOptions`:

```csharp
public static AudioClaudio.Domain.Polyphony.NoteDecoderOptions FromArgs(AudioClaudio.Cli.Cli.ParsedArgs parsed)
{
    ArgumentNullException.ThrowIfNull(parsed);
    var d = AudioClaudio.Domain.Polyphony.NoteDecoderOptions.Default;
    return d with
    {
        OnsetThreshold = parsed.Double("onset-threshold") ?? d.OnsetThreshold,
        FrameThreshold = parsed.Double("frame-threshold") ?? d.FrameThreshold,
        MinNoteLenFrames = parsed.Int("min-note-len") ?? d.MinNoteLenFrames,
    };
}
```

`KeyOption.cs` — add inside `KeyOption`:

```csharp
/// <summary>
/// Validates an already-integer-parsed --key value (the kernel's OptionKind.Int has
/// already rejected non-integer tokens by the time a handler sees this) against the real
/// key-signature range. <paramref name="fifths"/> is null when --key was omitted (always valid).
/// </summary>
public static bool Validate(int? fifths, out string? error)
{
    error = null;
    if (fifths is null) return true;
    if (fifths is < MinFifths or > MaxFifths)
    {
        error = $"--key must be an integer from {MinFifths} (C-flat major) to +{MaxFifths} (C-sharp major); got '{fifths}'.";
        return false;
    }
    return true;
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~ParsedArgsAdaptersTests"
```

Expected PASS: `Passed! - Failed: 0, Passed: 4`.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-b-terminal -m "feat(cli): add ParsedArgs overloads to KeyOption/TranscribeModeResolver/PolyDecoderOptions" --changes <ids> --status-after
```

---

## Task 19: Build `notate` (new command)

`notate` engraves an **existing** MIDI file (e.g. a richer transcriber's output, with real
durations + pedal) through the same grand-staff quantizer/writer `transcribe` uses. This is new
Stage 5 work, not a migration.

**Files:**
- Modify: `src/AudioClaudio.Cli/AppBuilder.cs`
- Create: `tests/AudioClaudio.Tests/Cli/Kernel/NotateHandlerTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System.IO;
using AudioClaudio.Cli;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class NotateHandlerTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Notate_writes_score_musicxml_and_score_mid()
    {
        var app = AppBuilder.Build(new System.Text.StringBuilder(), noColor: true);
        string dir = Path.Combine(Path.GetTempPath(), $"claudio-notate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            int code = app.Run(
                new[] { "notate", RepoPaths.Fixture("golden", "two-bar.mid"), "--out-dir", dir, "--tempo", "120" },
                new StringWriter(), new StringWriter());

            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(dir, "score.mid")));
            Assert.True(File.Exists(Path.Combine(dir, "score.musicxml")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Notate_rejects_an_out_of_range_key_with_a_clean_message()
    {
        var app = AppBuilder.Build(new System.Text.StringBuilder(), noColor: true);
        var stderr = new StringWriter();

        int code = app.Run(
            new[] { "notate", RepoPaths.Fixture("golden", "two-bar.mid"), "--key", "8" },
            new StringWriter(), stderr);

        Assert.Equal(1, code);
        Assert.Contains("--key", stderr.ToString());
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~NotateHandlerTests"
```

Expected FAILURE: `System.NotImplementedException : wired in Task 19`.

**Step 3 — Implement:**

```csharp
using AudioClaudio.Domain.Polyphony;
using AudioClaudio.Infrastructure.MusicXml;
```

```csharp
app.Register(notate, (p, stdout, stderr) =>
{
    if (!TryRequireFile(p.Argument("input.mid"), stderr, styler)) return 1;

    string outDir = p.Path("out-dir") ?? ".";
    Directory.CreateDirectory(outDir);
    bool noteNames = p.Flag("note-names");

    if (!KeyOption.Validate(p.Int("key"), out string? keyError))
    {
        stderr.Write($"{styler.Error("error:")} {keyError}\n");
        return 1;
    }

    var read = MidiFileReader.ReadFile(p.Argument("input.mid"), Rate, flattenPedal: false);
    int key = p.Int("key") ?? KeyDetector.Detect(read.Events.Select(e => e.Pitch).ToList());
    double? tempoArg = p.Double("tempo");
    Tempo scoreTempo = tempoArg is null
        ? TempoEstimator.Estimate(read.Events, read.Tempo)
        : new Tempo(tempoArg.Value);
    var notateSubdivision = p.Flag("triplets") ? Subdivision.Twelfth : Subdivision.Sixteenth;
    var grid = new QuantizationGrid(Rate, scoreTempo, TimeSignature.FourFour, notateSubdivision);
    var chordWindow = new SampleDuration(Rate.Hz / 20, Rate);
    var grandStaff = PolyphonicQuantizer.Quantize(read.Events, grid, chordWindow);
    var writer = new DryWetMidiWriter();
    var pedalMarks = read.PedalChanges.Select(c => ((int)grid.SamplesToTick(c.Sample), c.Down)).ToList();
    using (var mx = File.Create(Path.Combine(outDir, "score.musicxml")))
        new GrandStaffMusicXmlWriter(noteNames, fifths: key).Write(grandStaff, mx, pedalMarks);
    var quantized = GrandStaffFlattener.ToNoteEvents(grandStaff, grid);
    using (var score = File.Create(Path.Combine(outDir, "score.mid")))
        writer.Write(quantized, scoreTempo, score);
    stdout.WriteLine($"Notated {read.Events.Count} notes ({scoreTempo.BeatsPerMinute:F0} BPM{(tempoArg is null ? " estimated" : "")}) -> score.musicxml + score.mid (grand staff, {grandStaff.Measures.Count} bars, key {key:+#;-#;0}{(p.Int("key") is null ? " detected" : " declared")})");
    return 0;
});
```

Add `using System.Linq;` to `AppBuilder.cs` if not already present.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~NotateHandlerTests"
```

Expected PASS: `Passed! - Failed: 0, Passed: 2`.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-b-terminal -m "feat(cli): build notate (new command) on a kernel handler" --changes <ids> --status-after
```

---

## Task 20: Migrate `transcribe` (mono, polyphonic, Transkun)

The largest handler — three engines behind one command.

**Files:**
- Modify: `src/AudioClaudio.Cli/AppBuilder.cs`
- Create: `tests/AudioClaudio.Tests/Cli/Kernel/TranscribeHandlerTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System.IO;
using AudioClaudio.Cli;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi;
using AudioClaudio.Tests.Signals;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class TranscribeHandlerTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Transcribe_mono_emits_raw_and_score_midi_for_a_sustained_note()
    {
        var app = AppBuilder.Build(new System.Text.StringBuilder(), noColor: true);
        var rate = new SampleRate(44100);
        float[] pcm = SignalGenerator.HarmonicStack(new Pitch(69).Frequency(), (int)(1.0 * rate.Hz), rate, partials: 6, decay: 1.0);
        string dir = Path.Combine(Path.GetTempPath(), $"claudio-transcribe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string wav = Path.Combine(dir, "in.wav");
        WavWriter.WriteMonoFile(wav, pcm, rate);

        try
        {
            int code = app.Run(
                new[] { "transcribe", wav, "--tempo", "120", "--out-dir", dir, "--mono" },
                new StringWriter(), new StringWriter());

            Assert.Equal(0, code);
            var rawRead = MidiFileReader.ReadFile(Path.Combine(dir, "raw.mid"), rate);
            Assert.Single(rawRead.Events);
            Assert.Equal(69, rawRead.Events[0].Pitch.MidiNumber);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Transcribe_reports_a_friendly_error_for_a_missing_input_file()
    {
        var app = AppBuilder.Build(new System.Text.StringBuilder(), noColor: true);
        var stderr = new StringWriter();

        int code = app.Run(new[] { "transcribe", "does-not-exist.wav", "--mono" }, new StringWriter(), stderr);

        Assert.Equal(1, code);
        Assert.Contains("input file 'does-not-exist.wav' not found", stderr.ToString());
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~TranscribeHandlerTests"
```

Expected FAILURE: `System.NotImplementedException : wired in Task 20`.

**Step 3 — Implement.** Port the pre-Stage-5 `transcribe` logic (mono / polyphonic / Transkun
branches) into the registered handler, reading every value from `p`/`stdout`/`stderr` (never bare
`Console`), keyed by stripped option names throughout:

```csharp
using AudioClaudio.Infrastructure.Transcription;
using AudioClaudio.Cli.Composition;
```

```csharp
app.Register(transcribe, (p, stdout, stderr) =>
{
    if (!TryRequireFile(p.Argument("input.wav"), stderr, styler)) return 1;

    double? tempo = p.Double("tempo");
    string outDir = p.Path("out-dir") ?? ".";
    bool noteNames = p.Flag("note-names");
    bool legato = p.Flag("legato");
    bool coarseRhythm = p.Flag("coarse-rhythm");
    bool poly = TranscribeModeResolver.Resolve(p) == TranscribeMode.Polyphonic;

    if (!KeyOption.Validate(p.Int("key"), out string? keyError))
    {
        stderr.Write($"{styler.Error("error:")} {keyError}\n");
        return 1;
    }

    if (poly && p.String("model") == "transkun")
    {
        Directory.CreateDirectory(outDir);
        var tkRate = new SampleRate(44100);
        using var tkSource = WavAudioSource.FromFile(p.Argument("input.wav"), new FrameParameters(1024, 256));
        using var tk = new TranskunTranscriber(TranskunModelLocator.Resolve(), new Radix2Fft());
        (var tkNotes, var tkPedal) = tk.TranscribeDetailed(tkSource);
        Tempo tkTempo = tempo is { } tb ? new Tempo(tb) : TempoEstimator.Estimate(tkNotes, new Tempo(120));
        int tkKey = p.Int("key") ?? KeyDetector.Detect(tkNotes.Select(e => e.Pitch).ToList());

        var tkWriter = new DryWetMidiWriter();
        using (var raw = File.Create(Path.Combine(outDir, "raw.mid")))
            tkWriter.Write(tkNotes, tkTempo, raw);

        var tkSubdivision = p.Flag("triplets") ? Subdivision.Twelfth : Subdivision.Sixteenth;
        var tkGrid = new QuantizationGrid(tkRate, tkTempo, TimeSignature.FourFour, tkSubdivision);
        var tkChordWindow = new SampleDuration(tkRate.Hz / 20, tkRate);
        var tkGrandStaff = PolyphonicQuantizer.Quantize(tkNotes, tkGrid, tkChordWindow);
        var tkPedalMarks = tkPedal.Select(c => ((int)tkGrid.SamplesToTick(c.Sample), c.Down)).ToList();
        using (var mx = File.Create(Path.Combine(outDir, "score.musicxml")))
            new GrandStaffMusicXmlWriter(noteNames, fifths: tkKey).Write(tkGrandStaff, mx, tkPedalMarks);
        var tkQuantized = GrandStaffFlattener.ToNoteEvents(tkGrandStaff, tkGrid);
        using (var score = File.Create(Path.Combine(outDir, "score.mid")))
            tkWriter.Write(tkQuantized, tkTempo, score);
        stdout.WriteLine($"Transkun transcription: {tkNotes.Count} notes -> raw.mid; {tkQuantized.Count} quantized -> score.mid + score.musicxml (grand staff, {tkGrandStaff.Measures.Count} bars, key {tkKey:+#;-#;0}, {tkPedal.Count / 2} pedal spans)");
        File.WriteAllText(Path.Combine(outDir, "log.txt"), logBuffer.ToString());
        return 0;
    }

    if (poly)
    {
        Directory.CreateDirectory(outDir);
        string modelPath = ModelLocator.Resolve(p.String("model"));
        using var polySource = WavAudioSource.FromFile(p.Argument("input.wav"), new FrameParameters(1024, 256));
        var decoderOptions = PolyDecoderOptions.FromArgs(p);
        using var polyTx = new BasicPitchTranscriber(modelPath, decoderOptions, tempo: tempo is { } bpm ? new Tempo(bpm) : null);
        TranscriptionResult polyResult = polyTx.Transcribe(polySource);
        int key = p.Int("key") ?? KeyDetector.Detect(polyResult.RawEvents.Select(e => e.Pitch).ToList());
        var polyWriter = new DryWetMidiWriter();
        using (var raw = File.Create(Path.Combine(outDir, "raw.mid")))
            polyWriter.Write(polyResult.RawEvents, polyResult.Score.Tempo, raw);

        var polyRate = new SampleRate(BasicPitchModel.SampleRateHz);
        var polySubdivision = p.Flag("triplets") ? Subdivision.Twelfth : Subdivision.Sixteenth;
        var polyGrid = new QuantizationGrid(polyRate, polyResult.Score.Tempo, TimeSignature.FourFour, polySubdivision);
        var chordWindow = new SampleDuration(polyRate.Hz / 20, polyRate);
        var grandStaff = PolyphonicQuantizer.Quantize(polyResult.RawEvents, polyGrid, chordWindow);
        using (var mx = File.Create(Path.Combine(outDir, "score.musicxml")))
            new GrandStaffMusicXmlWriter(noteNames, fifths: key).Write(grandStaff, mx);
        var quantized = GrandStaffFlattener.ToNoteEvents(grandStaff, polyGrid);
        using (var score = File.Create(Path.Combine(outDir, "score.mid")))
            polyWriter.Write(quantized, polyResult.Score.Tempo, score);
        stdout.WriteLine($"Polyphonic transcription: {polyResult.RawEvents.Count} notes -> raw.mid; {quantized.Count} quantized -> score.mid + score.musicxml (grand staff, {grandStaff.Measures.Count} bars, key {key:+#;-#;0}{(p.Int("key") is null ? " detected" : " declared")})");
        File.WriteAllText(Path.Combine(outDir, "log.txt"), logBuffer.ToString());
        return 0;
    }

    TranscribeCommand.Run(p.Argument("input.wav"), tempo, outDir, noteNames, legato, coarseRhythm);
    File.WriteAllText(Path.Combine(outDir, "log.txt"), logBuffer.ToString());
    return 0;
});
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~TranscribeHandlerTests"
```

Expected PASS: `Passed! - Failed: 0, Passed: 2`.

**Step 5 — Run the full CLI folder to confirm no downstream regression:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Cli"
```

**Step 6 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-b-terminal -m "feat(cli): migrate transcribe (mono/poly/transkun) to a kernel handler" --changes <ids> --status-after
```

---

## Task 21: Migrate `listen`

Preserves the composition-root role: the mic, the live server, and the Start/Stop loop are still
constructed only here — **this is also the task Area D's Task 30 depends on and edits further; do
not skip or reorder it relative to Area D.**

**Files:**
- Modify: `src/AudioClaudio.Cli/AppBuilder.cs`
- Create: `tests/AudioClaudio.Tests/Cli/Kernel/ListenHandlerRegistrationTests.cs`

**Step 1 — Write the failing/registration test.** `listen` opens a real mic device, which CI
cannot exercise (R10.4/manual-acceptance precedent) — this test proves the handler is wired, not
that mic capture works (that is the Stage 5 manual-acceptance checklist, Task 42):

```csharp
using System.Linq;
using AudioClaudio.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class ListenHandlerRegistrationTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Listen_command_is_registered_with_its_full_option_surface()
    {
        var app = AppBuilder.Build(new System.Text.StringBuilder(), noColor: true);
        var cmd = app.Commands.Single(c => c.Name == "listen");

        Assert.Equal(
            new[] { "--note-names", "--out-dir", "--record", "--skip-silence", "--tempo", "--view" },
            cmd.Options.Select(o => o.Name).OrderBy(n => n));
    }
}
```

**Step 2 — Run to verify it fails (functionally):** before this task's edit, invoking the `listen`
handler throws `NotImplementedException`; the registration-only test above already passes from
Task 13 (registration exists before behavior does) — record that explicitly rather than treating it
as new signal. The real deliverable is the code change in Step 3, verified by the existing
manual-acceptance script (CLAUDE.md Step 10 precedent, formalized in Task 42).

**Step 3 — Implement.** Port the pre-Stage-5 `listen` logic (mic + optional `--view` live server +
Start/Stop loop) into the registered handler, reading from `p`/`stdout`/`stderr`, keeping
`logBuffer` as a closure:

```csharp
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Infrastructure.LiveView;
using System.Diagnostics;
using System.Threading;
```

```csharp
app.Register(listen, (p, stdout, stderr) =>
{
    double? tempoArg = p.Double("tempo");
    bool estimateTempo = tempoArg is null;
    double tempoBpm = tempoArg ?? 120.0;
    string outDir = p.Path("out-dir") ?? ".";
    Directory.CreateDirectory(outDir);
    bool view = p.Flag("view");
    bool skipSilence = p.Flag("skip-silence");
    bool record = p.Flag("record") || skipSilence;
    bool noteNames = p.Flag("note-names");
    const int SampleRateHz = 44100, FrameSize = 1024, Hop = 256;

    var synthesizer = new Lazy<MeltySynthSynthesizer>(
        () => new MeltySynthSynthesizer(AudioClaudio.Cli.Composition.SoundFontLocator.Resolve(null)));

    var settings = TranscriptionSettings.ForTempo(tempoBpm) with { FrameSize = FrameSize, Hop = Hop, EstimateTempo = estimateTempo };
    var pipeline = new TranscriptionPipeline(settings, new Radix2Fft());
    var midiWriter = new DryWetMidiWriter();
    var session = new LiveTranscriptionSession(pipeline.StreamNotes, pipeline.Transcribe);
    var grid = new QuantizationGrid(new SampleRate(SampleRateHz), new Tempo(tempoBpm),
                                    TimeSignature.FourFour, Subdivision.Sixteenth);
    var musicXml = new MusicXmlScoreWriter(noteNames);

    void FinalizeRecording(LiveSessionResult result, string timestamp, bool doRecord, bool doSkipSilence)
    {
        if (doRecord)
        {
            float[] inputPcm = result.CapturedFrames.Count > 0
                ? Framing.ReconstructMono(result.CapturedFrames)
                : Array.Empty<float>();
            IReadOnlyList<NoteEvent> recreationNotes = result.Events;

            if (doSkipSilence)
            {
                var maxSilence = new SampleDuration(Rate.Hz / 2, Rate);
                var collapsed = SilenceCollapser.Collapse(result.Events, inputPcm, Rate, maxSilence);
                inputPcm = collapsed.Audio;
                recreationNotes = collapsed.Notes;
            }

            if (inputPcm.Length > 0)
            {
                string inputPath = Path.Combine(outDir, "input.wav");
                WavFileWriter.Write(inputPath, inputPcm, Rate);
                stdout.WriteLine($"Wrote {inputPath}.");
            }

            string recreationPath = Path.Combine(outDir, "recreation.wav");
            RenderCommand.RenderToWav(recreationNotes, synthesizer.Value, Rate, recreationPath);
            stdout.WriteLine($"Wrote {recreationPath}.");
        }

        string archiveDir = SessionOutputArchive.Archive(outDir, timestamp);
        stdout.WriteLine($"Archived to {archiveDir}.");

        if (estimateTempo)
            stdout.WriteLine($"Estimated tempo: {result.Score.Tempo.BeatsPerMinute:F0} BPM.");

        string log = logBuffer.ToString();
        File.WriteAllText(Path.Combine(archiveDir, "log.txt"), log);
        File.WriteAllText(Path.Combine(outDir, "log.txt"), log);
    }

    LiveNotationServer? server = null;
    if (view)
    {
        server = new LiveNotationServer(Path.Combine(AppContext.BaseDirectory, "wwwroot"),
                                        scoreToMusicXml: musicXml.WriteToString, outDirPath: outDir);
        try { server.Start(); }
        catch (Exception ex)
        {
            stderr.WriteLine($"Live notation view unavailable ({ex.Message}); continuing without it.");
            server.Dispose();
            server = null;
        }
    }

    try
    {
        if (server is not null)
        {
            stdout.WriteLine($"Live notation view: {server.BaseUrl}");
            stdout.WriteLine("Press Start in the browser to record, Stop to save. Ctrl+C exits (saving a recording in progress).");
            TryOpenBrowser(server.BaseUrl);

            var startSignal = new SemaphoreSlim(0);
            var gate = new object();
            PortAudioAudioSource? currentMic = null;
            bool exiting = false;
            RecordOptions pendingOptions = default;

            server.StartRequested = opts => { lock (gate) { pendingOptions = opts; } startSignal.Release(); };
            server.StopRequested = () => { lock (gate) { currentMic?.Stop(); } };
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                lock (gate) { exiting = true; currentMic?.Stop(); }
                startSignal.Release();
            };

            while (true)
            {
                startSignal.Wait();
                lock (gate) { if (exiting) break; }

                RecordOptions opts;
                lock (gate) { opts = pendingOptions; }

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                logBuffer.Clear();
                server.PublishClear();
                var cleared = SessionOutputArchive.CleanLatest(outDir);
                if (cleared.Count > 0)
                    stdout.WriteLine($"Cleared {cleared.Count} previous output file(s) from {outDir}.");

                var projector = new LiveScoreProjector(grid);
                LiveNotationServer liveServer = server;
                var recordingWriter = new MusicXmlScoreWriter(opts.NoteNames, opts.Title);
                server.ScoreToMusicXml = recordingWriter.WriteToString;
                var listenCmd = new ListenCommand(session, midiWriter, midiWriter, stdout.WriteLine,
                                                musicXmlWriter: recordingWriter,
                                                onLiveNote: n => liveServer.PublishScore(projector.Add(n)),
                                                onFinalScore: s => liveServer.PublishScore(s));

                var mic = new PortAudioAudioSource(SampleRateHz, FrameSize, Hop, channels: 1);
                lock (gate) { currentMic = mic; }
                stdout.WriteLine($"Recording {timestamp}...");
                mic.Start();
                var result = listenCmd.Run(mic, (int)Math.Round(tempoBpm), outDir, CancellationToken.None);
                lock (gate) { currentMic = null; }
                mic.Dispose();

                FinalizeRecording(result, timestamp, opts.Record, opts.SkipSilence);
                Thread.Sleep(TimeSpan.FromSeconds(1));

                lock (gate) { if (exiting) break; }
            }
        }
        else
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var cleared = SessionOutputArchive.CleanLatest(outDir);
            if (cleared.Count > 0)
                stdout.WriteLine($"Cleared {cleared.Count} previous output file(s) from {outDir}.");

            using var micSource = new PortAudioAudioSource(SampleRateHz, FrameSize, Hop, channels: 1);
            var listenCmd = new ListenCommand(session, midiWriter, midiWriter, stdout.WriteLine, musicXmlWriter: musicXml);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; micSource.Stop(); cts.Cancel(); };
            logBuffer.Clear();
            micSource.Start();
            var result = listenCmd.Run(micSource, (int)Math.Round(tempoBpm), outDir, cts.Token);
            FinalizeRecording(result, timestamp, record, skipSilence);
        }
    }
    finally
    {
        server?.Dispose();
    }
    return 0;
});
```

Add a private `TryOpenBrowser` static helper to `AppBuilder` (moved verbatim from the old
`Program.cs`).

**Step 4 — Run to verify it passes (registration-level):**

```bash
dotnet test --filter "FullyQualifiedName~ListenHandlerRegistrationTests"
```

Expected PASS: `Passed! - Failed: 0, Passed: 1`.

**Step 5 — Run the full fast suite to confirm no regression:**

```bash
dotnet test --filter Category=Fast
```

**Step 6 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-b-terminal -m "feat(cli): migrate listen (view/no-view) to a kernel handler" --changes <ids> --status-after
```

---

## Task 22: Error-path tests — S5.2/S5.3/S5.5 at app level

Proves the kernel's generic behaviors (unknown command, unknown flag, malformed value, unexpected
exception) actually fire through the real, fully-wired app.

**Files:**
- Create: `tests/AudioClaudio.Tests/Cli/Kernel/ErrorPathTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System.IO;
using AudioClaudio.Cli;
using AudioClaudio.Cli.Cli;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class ErrorPathTests
{
    private static (int Code, string Out, string Err) RunCaptured(string[] args)
    {
        var app = AppBuilder.Build(new System.Text.StringBuilder(), noColor: true);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = app.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Unknown_command_suggests_the_nearest_match_and_exits_nonzero()
    {
        var (code, _, err) = RunCaptured(new[] { "trascribe", "song.wav" });

        Assert.Equal(CommandLineApp.UsageErrorExitCode, code);
        Assert.Contains("unknown command", err);
        Assert.Contains("transcribe", err);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Unknown_flag_suggests_the_nearest_match_and_exits_nonzero()
    {
        var (code, _, err) = RunCaptured(new[] { "transcribe", "song.wav", "--modl", "transkun" });

        Assert.Equal(CommandLineApp.UsageErrorExitCode, code);
        Assert.Contains("--model", err);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Malformed_tempo_names_the_flag_expected_kind_and_offending_token()
    {
        var (code, _, err) = RunCaptured(new[] { "transcribe", "song.wav", "--tempo", "abc" });

        Assert.Equal(CommandLineApp.UsageErrorExitCode, code);
        Assert.Contains("--tempo", err);
        Assert.Contains("abc", err);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Missing_input_file_reads_as_a_sentence_not_a_stack_trace()
    {
        var (code, _, err) = RunCaptured(new[] { "transcribe", "no-such-file.wav", "--mono" });

        Assert.Equal(1, code);
        Assert.Contains("input file 'no-such-file.wav' not found", err);
        Assert.DoesNotContain("at AudioClaudio", err);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~ErrorPathTests"
```

Expected: green if Tasks 9–20 are complete and faithful to their own contracts — this task's value
is pinning the app-level contract permanently, not introducing new behavior. If any assertion is
red, the fix is in the kernel/handler, never in loosening this test.

**Step 3 — No production code changes expected.**

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~ErrorPathTests"
```

Expected PASS: `Passed! - Failed: 0, Passed: 4`.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-b-terminal -m "test(cli): pin error-path behavior for unknown command/flag, malformed value, missing file" --changes <ids> --status-after
```

---

## Task 23: Golden help-text tests

Pins `--help` (top-level and per-command) against silent drift (S5.4). Goldens are seeded from the
real, fully-wired kernel's own output — never hand-typed.

**Files:**
- Create: `tests/AudioClaudio.Tests/Cli/Kernel/HelpGoldenTests.cs`
- Create (seeded by Step 3, not hand-written): `fixtures/golden/cli-help/top-level.txt`,
  `fixtures/golden/cli-help/transcribe.txt`, `notate.txt`, `render.txt`, `play.txt`, `evaluate.txt`,
  `evaluate-audio.txt`, `listen.txt`

**Step 1 — Write the failing test:**

```csharp
using System.IO;
using AudioClaudio.Cli;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class HelpGoldenTests
{
    private static string Run(string[] args)
    {
        var app = AppBuilder.Build(new System.Text.StringBuilder(), noColor: true);
        var stdout = new StringWriter();
        app.Run(args, stdout, new StringWriter());
        return stdout.ToString();
    }

    public static IEnumerable<object[]> Commands => new[]
    {
        new object[] { "transcribe" }, new object[] { "notate" }, new object[] { "render" },
        new object[] { "play" }, new object[] { "evaluate" }, new object[] { "evaluate-audio" },
        new object[] { "listen" },
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
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~HelpGoldenTests"
```

Expected FAILURE: `FileNotFoundException` — the golden files do not exist yet.

**Step 3 — Seed the goldens from the real kernel.** Add a temporary seeding test, run it once,
delete it:

```csharp
// Temporary — delete after running once.
[Fact]
public void ZZ_seed_goldens()
{
    Directory.CreateDirectory(RepoPaths.Fixture("golden", "cli-help"));
    File.WriteAllText(RepoPaths.Fixture("golden", "cli-help", "top-level.txt"), Run(new[] { "--help" }));
    foreach (var row in Commands)
    {
        string cmd = (string)row[0];
        File.WriteAllText(RepoPaths.Fixture("golden", "cli-help", $"{cmd}.txt"), Run(new[] { cmd, "--help" }));
    }
}
```

```bash
dotnet test --filter "FullyQualifiedName~ZZ_seed_goldens"
```

Then delete `ZZ_seed_goldens` from the test file. Inspect each generated `.txt` by eye before
committing (§5 fixture policy: "the diff is reviewed, never blindly regenerated") — confirm tool
name, every command's summary/option/example text, and readable line widths.

**Step 4 — Run the real tests:**

```bash
dotnet test --filter "FullyQualifiedName~HelpGoldenTests"
```

Expected PASS: `Passed! - Failed: 0, Passed: 8`.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit stage5-area-b-terminal -m "test(cli): pin top-level and per-command --help against drift (golden fixtures)" --changes <ids> --status-after
```

---

## Task 24: Remove dead ad-hoc parsing, format, final full-suite pass

**Files:**
- Modify: `src/AudioClaudio.Cli/AppBuilder.cs` (delete any leftover local statics if present)
- No test changes — this is a cleanup-and-verify task.

**Step 1 — Confirm nothing still references pre-migration ad-hoc parsing** (removed wholesale by
Task 14's `Program.cs` rewrite):

```bash
grep -rn "TryReadOption\|TryReadKeyOverride\|static int Usage" src/AudioClaudio.Cli/
```

Expected: no matches.

**Step 2 — Format:**

```bash
dotnet format
```

**Step 3 — Full build + full test run:**

```bash
dotnet build
dotnet test
```

**Step 4 — Fast-suite sanity:**

```bash
dotnet test --filter Category=Fast
```

**Step 5 — Commit:**

```bash
but status -fv
but commit stage5-area-b-terminal -m "chore(cli): remove dead ad-hoc arg-parsing helpers; dotnet format" --changes <ids> --status-after
```

---

# Area D — Live-View Polish (Tasks 25–33)

**Scope:** S5.10 (VU meter + device name + recording/idle state), S5.11 (playable + downloadable
finished take), S5.12 (styled/responsive/dark-mode page; `--view` failure still degrades to plain
`listen`). Builds entirely on the existing `LiveNotationServer` (`HttpListener` + SSE) — adds routes
and page markup, does not restructure the server. **Depends on Area B's Task 21 (`listen`
migrated into `AppBuilder.cs`)** — Task 30 below patches the handler Task 21 registers, not
`Program.cs`.

## Branch setup (once, before Task 25)

```bash
but status -fv
but branch new stage5-area-d-liveview
but mark stage5-area-d-liveview
```

---

## Task 25 — `LiveNotationServer.PublishLevel` + a new "level" SSE event

**Files**
- `src/AudioClaudio.Infrastructure/LiveView/LiveNotationServer.cs`
- `tests/AudioClaudio.Tests/LiveView/LiveNotationServerTests.cs`

**Write the failing test.** Add to `LiveNotationServerTests.cs`, right after
`AfterPublishClearANewlyConnectedClientDoesNotReceiveAStaleScore`:

```csharp
    [Fact]
    [Trait("Category", "Fast")]
    public async Task PublishLevelDeliversARmsAndDeviceNamePayloadOverSse()
    {
        using var server = new LiveNotationServer(WebRoot);
        server.Start();
        using var http = new HttpClient();
        using var response = await http.GetAsync(server.BaseUrl + "events", HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        server.PublishLevel(0.4321, "Test Microphone");

        (string eventName, string data) = await ReadSseFrameAsync(reader, TimeSpan.FromSeconds(5));
        Assert.Equal("level", eventName);
        Assert.Equal("0.4321|Test Microphone", data);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task PublishLevelIsNotReplayedToALateJoiningClient()
    {
        using var server = new LiveNotationServer(WebRoot);
        server.Start();
        server.PublishLevel(0.9, "Test Microphone"); // published BEFORE any client connects

        using var http = new HttpClient();
        using var response = await http.GetAsync(server.BaseUrl + "events", HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        await Assert.ThrowsAsync<TimeoutException>(() => ReadSseDataLineAsync(reader, TimeSpan.FromMilliseconds(500)));
    }
```

**Run it — fails:**

```bash
dotnet test tests/AudioClaudio.Tests --filter "FullyQualifiedName~LiveNotationServerTests"
```

Expected: `error CS1061: 'LiveNotationServer' does not contain a definition for 'PublishLevel' ...`

**Minimal implementation.** Add the using and the method:

```csharp
using System.Globalization;
```

Right after `PublishClear`:

```csharp
    public void PublishClear() => Broadcast(("clear", string.Empty), remembered: null);

    /// <summary>
    /// Broadcasts the current input level (RMS) and the capture device's name as a small "level"
    /// SSE event -- the VU-meter feed for the live-view page (S5.10). Never remembered for late
    /// joiners (remembered: null): a level reading is momentary, unlike a score.
    /// </summary>
    public void PublishLevel(double rms, string device)
    {
        string payload = $"{rms.ToString("F4", CultureInfo.InvariantCulture)}|{device}";
        Broadcast(("level", payload), remembered: null);
    }
```

**Run it — passes:**

```bash
dotnet test tests/AudioClaudio.Tests --filter "FullyQualifiedName~LiveNotationServerTests"
```

Expected: `Passed!  - Failed: 0, Passed: 14, Skipped: 0, Total: 14`

**Commit:**

```bash
but status -fv
but commit stage5-area-d-liveview -m "feat(liveview): broadcast input level over a new SSE \"level\" event" --changes <ids> --status-after
```

---

## Task 26 — `PortAudioAudioSource.DeviceName`

**Files**
- `src/AudioClaudio.Infrastructure/Audio/PortAudioAudioSource.cs`
- `tests/AudioClaudio.Tests/Infrastructure/PortAudioAudioSourceTests.cs`

**Write the failing test:**

```csharp
    // Device-free (never calls Start()): proves the property exists and defaults to null.
    [Fact]
    [Trait("Category", "Fast")]
    public void DeviceNameIsNullBeforeStart()
    {
        using var src = new PortAudioAudioSource(sampleRateHz: 44100, frameSize: N, hop: H);

        Assert.Null(src.DeviceName);
    }
```

**Run it — fails:**

```bash
dotnet test tests/AudioClaudio.Tests --filter "FullyQualifiedName~PortAudioAudioSourceTests"
```

Expected: `error CS1061: 'PortAudioAudioSource' does not contain a definition for 'DeviceName' ...`

**Minimal implementation.** Add the property near the other public members:

```csharp
    public SampleRate SampleRate { get; }

    /// <summary>The opened input device's name, populated in <see cref="Start"/> from
    /// <c>PortAudio.GetDeviceInfo</c> -- null until then. The live-view page (S5.10) shows this
    /// next to the VU meter so the player knows what's actually being heard.</summary>
    public string? DeviceName { get; private set; }
```

And in `Start()`, right after the device-info lookup:

```csharp
        var info = PortAudio.GetDeviceInfo(device);
        DeviceName = info.name;
        var inParams = new StreamParameters
```

**Run it — passes:**

```bash
dotnet test tests/AudioClaudio.Tests --filter "FullyQualifiedName~PortAudioAudioSourceTests"
```

Expected: `Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2`

**Commit:**

```bash
but status -fv
but commit stage5-area-d-liveview -m "feat(audio): expose PortAudioAudioSource.DeviceName" --changes <ids> --status-after
```

---

## Task 27 — `AudioClaudio.Domain.AudioLevel.Rms`, a pure level helper

**Files**
- `src/AudioClaudio.Domain/AudioLevel.cs` (new)
- `tests/AudioClaudio.Tests/Domain/AudioLevelTests.cs` (new)

**Write the failing test:**

```csharp
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

/// <summary>Root-mean-square amplitude -- the pure math behind the live-view VU meter (S5.10).
/// No frame/audio-source coupling here (R1.5).</summary>
public class AudioLevelTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void RmsOfSilenceIsZero()
    {
        Assert.Equal(0.0, AudioLevel.Rms(new float[] { 0f, 0f, 0f, 0f }));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void RmsOfAFullScaleSquareWaveIsOne()
    {
        Assert.Equal(1.0, AudioLevel.Rms(new float[] { 1f, -1f, 1f, -1f }), 6);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void RmsOfAConstantHalfScaleSignalIsThatConstant()
    {
        Assert.Equal(0.5, AudioLevel.Rms(new float[] { 0.5f, 0.5f, 0.5f }), 6);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void RmsOfAnEmptySpanIsZero()
    {
        Assert.Equal(0.0, AudioLevel.Rms(System.ReadOnlySpan<float>.Empty));
    }
}
```

**Run it — fails:**

```bash
dotnet test tests/AudioClaudio.Tests --filter "FullyQualifiedName~AudioLevelTests"
```

Expected: `error CS0246: The type or namespace name 'AudioLevel' could not be found ...`

**Minimal implementation:**

```csharp
namespace AudioClaudio.Domain;

/// <summary>
/// Root-mean-square amplitude of a block of samples -- the standard loudness proxy behind a
/// VU-style meter. Pure math, no I/O, no device (R1.5).
/// </summary>
public static class AudioLevel
{
    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return 0.0;

        double sumOfSquares = 0.0;
        foreach (float sample in samples)
        {
            sumOfSquares += (double)sample * sample;
        }

        return Math.Sqrt(sumOfSquares / samples.Length);
    }
}
```

**Run it — passes:**

```bash
dotnet test tests/AudioClaudio.Tests --filter "FullyQualifiedName~AudioLevelTests"
```

Expected: `Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4`

**Commit:**

```bash
but status -fv
but commit stage5-area-d-liveview -m "feat(domain): add AudioLevel.Rms, a pure level-meter helper" --changes <ids> --status-after
```

---

## Task 28 — `LevelTeeingAudioSource`, the CLI-side decorator (R10.4)

**Files**
- `src/AudioClaudio.Cli/Composition/LevelTeeingAudioSource.cs` (new)
- `tests/AudioClaudio.Tests/Cli/LevelTeeingAudioSourceTests.cs` (new)

**Write the failing test:**

```csharp
using AudioClaudio.Application.Ports;
using AudioClaudio.Cli.Composition;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Cli;

public class LevelTeeingAudioSourceTests
{
    private static readonly SampleRate Rate = new SampleRate(44100);

    private sealed class FakeSource : IAudioSource
    {
        private readonly Frame[] _frames;
        public FakeSource(params Frame[] frames) => _frames = frames;
        public IEnumerable<Frame> Frames => _frames;
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void PassesEveryFrameThroughUnchanged()
    {
        var frame = new Frame(new float[] { 1f, -1f, 1f, -1f }, new SamplePosition(0, Rate));
        var inner = new FakeSource(frame);
        var levels = new List<double>();
        var teed = new LevelTeeingAudioSource(inner, levels.Add);

        var outFrames = teed.Frames.ToList();

        Assert.Single(outFrames);
        Assert.Same(frame, outFrames[0]);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void ReportsTheRmsOfEachFrameAsItIsYielded()
    {
        var loud = new Frame(new float[] { 1f, -1f, 1f, -1f }, new SamplePosition(0, Rate));  // RMS = 1.0
        var quiet = new Frame(new float[] { 0f, 0f, 0f, 0f }, new SamplePosition(4, Rate));    // RMS = 0.0
        var inner = new FakeSource(loud, quiet);
        var levels = new List<double>();
        var teed = new LevelTeeingAudioSource(inner, levels.Add);

        teed.Frames.ToList();

        Assert.Equal(new[] { 1.0, 0.0 }, levels);
    }
}
```

**Run it — fails:**

```bash
dotnet test tests/AudioClaudio.Tests --filter "FullyQualifiedName~LevelTeeingAudioSourceTests"
```

Expected: `error CS0246: The type or namespace name 'LevelTeeingAudioSource' could not be found ...`

**Minimal implementation:**

```csharp
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;

namespace AudioClaudio.Cli.Composition;

/// <summary>
/// Tees an RMS level from each frame the listen loop consumes, invoking <c>onLevel</c> as a side
/// channel while passing every frame through unchanged. Lives in the CLI composition root, NOT in
/// Infrastructure.Audio -- PortAudioAudioSource stays free of any transcription/analysis logic
/// (R10.4).
/// </summary>
public sealed class LevelTeeingAudioSource : IAudioSource
{
    private readonly IAudioSource _inner;
    private readonly Action<double> _onLevel;

    public LevelTeeingAudioSource(IAudioSource inner, Action<double> onLevel)
    {
        _inner = inner;
        _onLevel = onLevel;
    }

    public IEnumerable<Frame> Frames
    {
        get
        {
            foreach (Frame frame in _inner.Frames)
            {
                _onLevel(AudioLevel.Rms(frame.Samples));
                yield return frame;
            }
        }
    }
}
```

**Run it — passes:**

```bash
dotnet test tests/AudioClaudio.Tests --filter "FullyQualifiedName~LevelTeeingAudioSourceTests"
```

Expected: `Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2`

**Commit:**

```bash
but status -fv
but commit stage5-area-d-liveview -m "feat(cli): add LevelTeeingAudioSource decorator for the live-view VU meter" --changes <ids> --status-after
```

---

## Task 29 — `LiveNotationServer` GET `/files/<name>` whitelisted take-file route

**Files**
- `src/AudioClaudio.Infrastructure/LiveView/LiveNotationServer.cs`
- `tests/AudioClaudio.Tests/LiveView/LiveNotationServerTakeFilesTests.cs` (new)

**Write the failing test:**

```csharp
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AudioClaudio.Infrastructure.LiveView;
using Xunit;

namespace AudioClaudio.Tests.LiveView;

/// <summary>
/// GET /files/&lt;name&gt; -- the whitelisted take-file route (S5.11): serves the current take's
/// raw.mid / score.mid / score.musicxml / recreation.wav / input.wav from the out-dir.
/// </summary>
public class LiveNotationServerTakeFilesTests
{
    private static readonly string WebRoot = CreateFixtureWebRoot();

    private static string CreateFixtureWebRoot()
    {
        string dir = Directory.CreateTempSubdirectory("claudio_liveview_files_webroot_").FullName;
        File.WriteAllText(Path.Combine(dir, "index.html"), "<html><body>osmd host</body></html>");
        return dir;
    }

    private static string CreateOutDir(params (string Name, byte[] Bytes)[] files)
    {
        string dir = Directory.CreateTempSubdirectory("claudio_liveview_files_outdir_").FullName;
        foreach ((string name, byte[] bytes) in files)
            File.WriteAllBytes(Path.Combine(dir, name), bytes);
        return dir;
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task ServesAWhitelistedTakeFileFromTheOutDir()
    {
        byte[] content = Encoding.UTF8.GetBytes("fake musicxml");
        string outDir = CreateOutDir(("score.musicxml", content));
        using var server = new LiveNotationServer(WebRoot, outDirPath: outDir);
        server.Start();
        using var http = new HttpClient();

        var response = await http.GetAsync(server.BaseUrl + "files/score.musicxml");
        byte[] body = await response.Content.ReadAsByteArrayAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(content, body);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task NonWhitelistedFileNameIsRejectedEvenIfItExistsInTheOutDir()
    {
        string outDir = CreateOutDir(("log.txt", Encoding.UTF8.GetBytes("run log, not a take file")));
        using var server = new LiveNotationServer(WebRoot, outDirPath: outDir);
        server.Start();
        using var http = new HttpClient();

        var response = await http.GetAsync(server.BaseUrl + "files/log.txt");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task MissingTakeFileReturns404()
    {
        string outDir = CreateOutDir();
        using var server = new LiveNotationServer(WebRoot, outDirPath: outDir);
        server.Start();
        using var http = new HttpClient();

        var response = await http.GetAsync(server.BaseUrl + "files/recreation.wav");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task WhenNoOutDirIsConfiguredTakeFileRoutesAlwaysReturn404()
    {
        using var server = new LiveNotationServer(WebRoot);
        server.Start();
        using var http = new HttpClient();

        var response = await http.GetAsync(server.BaseUrl + "files/score.mid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
```

**Run it — fails:**

```bash
dotnet test tests/AudioClaudio.Tests --filter "FullyQualifiedName~LiveNotationServerTakeFilesTests"
```

Expected: `error CS1739: The best overload for 'LiveNotationServer' does not have a parameter named 'outDirPath'`

**Minimal implementation.** Constructor + new property, replacing the existing constructor:

```csharp
    public LiveNotationServer(string webRootPath, int port = 0, Func<Score, string>? scoreToMusicXml = null,
                               string? outDirPath = null)
    {
        _webRootPath = Path.GetFullPath(webRootPath);
        Port = port == 0 ? FreeTcpPort.Find() : port;
        BaseUrl = $"http://localhost:{Port}/";
        _listener.Prefixes.Add(BaseUrl);
        ScoreToMusicXml = scoreToMusicXml ?? new MusicXmlScoreWriter().WriteToString;
        OutDirPath = outDirPath is null ? null : Path.GetFullPath(outDirPath);
    }

    /// <summary>The out-dir a running `listen --view` session writes its take files to -- null
    /// when no take output is being served. Backs GET /files/&lt;name&gt; (S5.11).</summary>
    public string? OutDirPath { get; }
```

Route dispatch, in `HandleRequestAsync`, right after the `/events` branch:

```csharp
            if (path == "/events")
            {
                await HandleSseAsync(ctx).ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/files/", StringComparison.Ordinal))
            {
                await ServeTakeFileAsync(ctx, path["/files/".Length..]).ConfigureAwait(false);
                return;
            }

            await ServeStaticFileAsync(ctx, path).ConfigureAwait(false);
```

Replace the containment-check region and `ContentTypeFor`:

```csharp
    private static bool IsInside(string fullPath, string root)
    {
        string normalizedRoot = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(normalizedRoot, StringComparison.Ordinal);
    }

    private bool IsInsideWebRoot(string fullPath) => IsInside(fullPath, _webRootPath);

    // The only take files a browser may ever fetch (S5.11).
    private static readonly HashSet<string> TakeFileNames = new(StringComparer.Ordinal)
    {
        "raw.mid", "score.mid", "score.musicxml", "recreation.wav", "input.wav",
    };

    private async Task ServeTakeFileAsync(HttpListenerContext ctx, string fileName)
    {
        if (OutDirPath is null || !TakeFileNames.Contains(fileName))
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        string filePath = Path.GetFullPath(Path.Combine(OutDirPath, fileName));
        if (!IsInside(filePath, OutDirPath) || !File.Exists(filePath))
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        ctx.Response.ContentType = ContentTypeFor(filePath);
        byte[] bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".wav" => "audio/wav",
        ".mid" => "audio/midi",
        ".musicxml" => "application/vnd.recordare.musicxml+xml",
        _ => "application/octet-stream",
    };
```

**Run it — passes:**

```bash
dotnet test tests/AudioClaudio.Tests --filter "FullyQualifiedName~LiveNotationServerTakeFilesTests"
```

Expected: `Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4`

Also re-run the full LiveView suite:

```bash
dotnet test tests/AudioClaudio.Tests --filter "FullyQualifiedName~AudioClaudio.Tests.LiveView"
```

**Commit:**

```bash
but status -fv
but commit stage5-area-d-liveview -m "feat(liveview): serve whitelisted take files over GET /files/<name>" --changes <ids> --status-after
```

---

## Task 30 — Wire level metering + take-file out-dir into the `listen` handler in `AppBuilder.cs`

**Sequencing note:** this task patches the `listen` handler **Task 21 registered inside
`AppBuilder.cs`** — it does not touch `Program.cs` (which Task 14 already finalized). Do not start
this task until Task 21 has landed.

There is no new automated test of its own beyond a build check — `PortAudioAudioSource`,
`LiveNotationServer.PublishLevel`, and `LevelTeeingAudioSource` are already unit-tested
(Tasks 25–28); there is no audio device in CI/sandbox to exercise the wired-up path end-to-end
(same reasoning already applied to `PortAudioAudioSource.Start`, CLAUDE.md Step 10). The
browser-visible result is part of the Stage 5 manual-acceptance gate (Task 42), not a new
automated test.

**Files**
- `src/AudioClaudio.Cli/AppBuilder.cs`

**The change.** Inside the `listen` handler registered in Task 21, pass the out-dir into the
server construction:

```csharp
                server = new LiveNotationServer(Path.Combine(AppContext.BaseDirectory, "wwwroot"),
                                                scoreToMusicXml: musicXml.WriteToString, outDirPath: outDir);
```

(replaces the two-argument-plus-scoreToMusicXml call already in Task 21's handler)

Wrap the mic in `LevelTeeingAudioSource` and feed `server.PublishLevel`:

```csharp
                        var mic = new PortAudioAudioSource(SampleRateHz, FrameSize, Hop, channels: 1);
                        lock (gate) { currentMic = mic; }
                        var levelSource = new LevelTeeingAudioSource(mic,
                            rms => liveServer.PublishLevel(rms, mic.DeviceName ?? "Unknown microphone"));
                        stdout.WriteLine($"Recording {timestamp}...");
                        mic.Start();
                        var result = listenCmd.Run(levelSource, (int)Math.Round(tempoBpm), outDir, CancellationToken.None);
```

(replaces the equivalent block ending in `listenCmd.Run(mic, ...)` inside Task 21's handler; `mic`
itself is still what `mic.Stop()`/`mic.Dispose()` refer to elsewhere in the loop)

Add `using AudioClaudio.Cli.Composition;` to `AppBuilder.cs` if not already present (it already is,
from Task 28's `LevelTeeingAudioSource` and pre-existing `SessionOutputArchive`/`TeeTextWriter`).

**Run it — build check:**

```bash
dotnet build
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

Then confirm no regression:

```bash
dotnet test tests/AudioClaudio.Tests --filter "FullyQualifiedName~ListenHandlerRegistrationTests|FullyQualifiedName~AudioClaudio.Tests.LiveView"
```

Expected: all green.

**Commit:**

```bash
but status -fv
but commit stage5-area-d-liveview -m "feat(cli): wire live input level + take-file serving into the listen handler" --changes <ids> --status-after
```

---

## Task 31 — `wwwroot/styles.css` + viewport/stylesheet link (S5.12)

**Files**
- `src/AudioClaudio.Cli/wwwroot/styles.css` (new)
- `src/AudioClaudio.Cli/wwwroot/index.html`
- `tests/AudioClaudio.Tests/LiveView/LiveViewPolishAssetTests.cs` (new)

**Write the failing test:**

```csharp
using System.IO;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.LiveView;

/// <summary>
/// Content checks for the Stage 5 live-view polish (S5.10-S5.12). The actual rendering is the
/// documented manual-acceptance gate (Task 42), not this suite.
/// </summary>
public class LiveViewPolishAssetTests
{
    private static string WwwRoot => Path.Combine(RepoPaths.RepoRoot, "src", "AudioClaudio.Cli", "wwwroot");

    [Fact]
    [Trait("Category", "Fast")]
    public void IndexHtmlLinksTheStylesheetAndIsResponsive()
    {
        string html = File.ReadAllText(Path.Combine(WwwRoot, "index.html"));

        Assert.Contains("href=\"styles.css\"", html);
        Assert.Contains("name=\"viewport\"", html);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void StylesCssHasDarkModeAndAResponsiveBreakpoint()
    {
        string css = File.ReadAllText(Path.Combine(WwwRoot, "styles.css"));

        Assert.Contains("prefers-color-scheme: dark", css);
        Assert.Contains("@media (max-width:", css);
    }
}
```

**Run it — fails:**

```bash
dotnet test tests/AudioClaudio.Tests --filter "FullyQualifiedName~LiveViewPolishAssetTests"
```

Expected: substring not found on both facts; `styles.css` also raises `FileNotFoundException`.

**Minimal implementation.** `src/AudioClaudio.Cli/wwwroot/index.html` — add the viewport meta tag
and stylesheet link (kept minimal here; Task 32 fills in the rest of the body):

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Audio Claudio — Live</title>
  <link rel="stylesheet" href="styles.css" />
</head>
<body>
  <div id="controls">
    <label>Title <input id="score-title" type="text" placeholder="Untitled Score" /></label>
    <label><input id="opt-record" type="checkbox" checked /> Record audio</label>
    <label><input id="opt-skip-silence" type="checkbox" /> Skip silence</label>
    <label><input id="opt-note-names" type="checkbox" /> Note names</label>
    <button id="start-recording" type="button">● Start recording</button>
    <button id="stop-recording" type="button" disabled>■ Stop recording</button>
  </div>
  <div id="osmd-container"></div>
  <script src="osmd/opensheetmusicdisplay.min.js"></script>
  <script src="app.js"></script>
</body>
</html>
```

`src/AudioClaudio.Cli/wwwroot/styles.css` (new file, complete — `wwwroot\**\*` in
`AudioClaudio.Cli.csproj` already globs it in):

```css
:root {
  color-scheme: light dark;
  --bg: #f7f7f9;
  --fg: #1b1b1f;
  --panel-bg: #ffffff;
  --border: #d8d8de;
  --accent: #2f6fed;
  --recording: #d64545;
  --idle: #6b7280;
  --meter-track: #e5e5ea;
  --meter-fill: #34c759;
}

@media (prefers-color-scheme: dark) {
  :root {
    --bg: #14151a;
    --fg: #e7e7ea;
    --panel-bg: #1d1e24;
    --border: #33343c;
    --accent: #6ea1ff;
    --recording: #ff6b6b;
    --idle: #9aa0ab;
    --meter-track: #2a2b32;
    --meter-fill: #34c759;
  }
}

* { box-sizing: border-box; }

body {
  margin: 0;
  font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif;
  background: var(--bg);
  color: var(--fg);
  min-height: 100vh;
}

header#app-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
  padding: 0.75rem 1.25rem;
  border-bottom: 1px solid var(--border);
  background: var(--panel-bg);
  flex-wrap: wrap;
}

header#app-header h1 { font-size: 1.1rem; margin: 0; font-weight: 600; }
header#app-header .tagline { font-weight: 400; color: var(--idle); }

.status {
  padding: 0.3rem 0.75rem;
  border-radius: 999px;
  font-size: 0.85rem;
  font-weight: 600;
  white-space: nowrap;
}

.status-idle { background: color-mix(in srgb, var(--idle) 20%, transparent); color: var(--idle); }
.status-recording { background: color-mix(in srgb, var(--recording) 20%, transparent); color: var(--recording); }
.status-finishing { background: color-mix(in srgb, var(--accent) 20%, transparent); color: var(--accent); }

main {
  display: flex;
  flex-direction: column;
  gap: 1rem;
  padding: 1rem 1.25rem 2rem;
  max-width: 960px;
  margin: 0 auto;
}

#controls {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.75rem 1.25rem;
  padding: 0.85rem;
  background: var(--panel-bg);
  border: 1px solid var(--border);
  border-radius: 0.6rem;
}

#controls label { display: flex; align-items: center; gap: 0.35rem; font-size: 0.9rem; }

#controls input[type="text"] {
  padding: 0.3rem 0.5rem;
  border: 1px solid var(--border);
  border-radius: 0.4rem;
  background: var(--bg);
  color: var(--fg);
}

button {
  padding: 0.4rem 0.9rem;
  border-radius: 0.5rem;
  border: 1px solid var(--border);
  background: var(--accent);
  color: #fff;
  font-weight: 600;
  cursor: pointer;
}

button:disabled { background: var(--meter-track); color: var(--idle); cursor: not-allowed; }

#input-meter {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.6rem 0.85rem;
  background: var(--panel-bg);
  border: 1px solid var(--border);
  border-radius: 0.6rem;
}

#device-name { font-size: 0.85rem; color: var(--idle); min-width: 8rem; }

#vu-meter { flex: 1; height: 0.7rem; border-radius: 999px; background: var(--meter-track); overflow: hidden; }

#vu-meter-fill { height: 100%; width: 0%; background: var(--meter-fill); transition: width 80ms linear; }

section[aria-label="Live sheet music"] {
  background: var(--panel-bg);
  border: 1px solid var(--border);
  border-radius: 0.6rem;
  padding: 1rem;
  min-height: 200px;
  overflow-x: auto;
}

#take-output {
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
  padding: 0.85rem;
  background: var(--panel-bg);
  border: 1px solid var(--border);
  border-radius: 0.6rem;
}

#take-output[hidden] { display: none; }
#take-output h2 { margin: 0; font-size: 0.95rem; }
#recreation-player { width: 100%; }
#download-links { display: flex; flex-wrap: wrap; gap: 0.6rem; }

#download-links a {
  padding: 0.35rem 0.75rem;
  border: 1px solid var(--border);
  border-radius: 0.4rem;
  color: var(--accent);
  text-decoration: none;
  font-size: 0.85rem;
}

#download-links a:hover { background: color-mix(in srgb, var(--accent) 12%, transparent); }

@media (max-width: 560px) {
  header#app-header { flex-direction: column; align-items: flex-start; }
  #controls { flex-direction: column; align-items: stretch; }
  #input-meter { flex-wrap: wrap; }
}
```

**Run it — passes:**

```bash
dotnet test tests/AudioClaudio.Tests --filter "FullyQualifiedName~LiveViewPolishAssetTests|FullyQualifiedName~WebAssetContentTests"
```

Expected: `Passed!  - Failed: 0, Passed: 8, Skipped: 0, Total: 8`

**Commit:**

```bash
but status -fv
but commit stage5-area-d-liveview -m "feat(liveview): add styles.css, dark-mode + responsive, link it from index.html" --changes <ids> --status-after
```

---

## Task 32 — `index.html`: VU meter, device name, status badge, take-output markup

**Files**
- `src/AudioClaudio.Cli/wwwroot/index.html`
- `tests/AudioClaudio.Tests/LiveView/LiveViewPolishAssetTests.cs`

**Write the failing test.** Add to `LiveViewPolishAssetTests.cs`:

```csharp
    [Fact]
    [Trait("Category", "Fast")]
    public void IndexHtmlContainsTheVuMeterAndDeviceNameAndStatusBadge()
    {
        string html = File.ReadAllText(Path.Combine(WwwRoot, "index.html"));

        Assert.Contains("id=\"vu-meter\"", html);
        Assert.Contains("id=\"vu-meter-fill\"", html);
        Assert.Contains("id=\"device-name\"", html);
        Assert.Contains("id=\"status-badge\"", html);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void IndexHtmlContainsTheTakeOutputPlayerAndDownloadLinks()
    {
        string html = File.ReadAllText(Path.Combine(WwwRoot, "index.html"));

        Assert.Contains("id=\"take-output\"", html);
        Assert.Contains("id=\"recreation-player\"", html);
        Assert.Contains("id=\"download-raw\"", html);
        Assert.Contains("id=\"download-score-mid\"", html);
        Assert.Contains("id=\"download-score-musicxml\"", html);
        Assert.Contains("id=\"download-recreation\"", html);
    }
```

**Run it — fails:**

```bash
dotnet test tests/AudioClaudio.Tests --filter "FullyQualifiedName~LiveViewPolishAssetTests"
```

Expected: substrings not found (`Failed: 2, Passed: 2`).

**Minimal implementation.** Replace `index.html`'s `<body>` with the full markup (every id the
pre-existing `WebAssetContentTests` checks is preserved verbatim):

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Audio Claudio — Live</title>
  <link rel="stylesheet" href="styles.css" />
</head>
<body>
  <header id="app-header">
    <h1>Audio Claudio <span class="tagline">— live notation</span></h1>
    <div id="status-badge" class="status status-idle">Idle</div>
  </header>

  <main>
    <section id="controls" aria-label="Recording controls">
      <label>Title <input id="score-title" type="text" placeholder="Untitled Score" /></label>
      <label><input id="opt-record" type="checkbox" checked /> Record audio</label>
      <label><input id="opt-skip-silence" type="checkbox" /> Skip silence</label>
      <label><input id="opt-note-names" type="checkbox" /> Note names</label>
      <button id="start-recording" type="button">● Start recording</button>
      <button id="stop-recording" type="button" disabled>■ Stop recording</button>
    </section>

    <section id="input-meter" aria-label="Live input level">
      <div id="device-name">No microphone yet</div>
      <div id="vu-meter" role="meter" aria-label="Input level" aria-valuemin="0" aria-valuemax="1" aria-valuenow="0">
        <div id="vu-meter-fill"></div>
      </div>
    </section>

    <section aria-label="Live sheet music">
      <div id="osmd-container"></div>
    </section>

    <section id="take-output" hidden aria-label="Finished take">
      <h2>Latest take</h2>
      <audio id="recreation-player" controls></audio>
      <div id="download-links">
        <a id="download-raw" href="#" download>raw.mid</a>
        <a id="download-score-mid" href="#" download>score.mid</a>
        <a id="download-score-musicxml" href="#" download>score.musicxml</a>
        <a id="download-recreation" href="#" download>recreation.wav</a>
      </div>
    </section>
  </main>

  <script src="osmd/opensheetmusicdisplay.min.js"></script>
  <script src="app.js"></script>
</body>
</html>
```

**Run it — passes:**

```bash
dotnet test tests/AudioClaudio.Tests --filter "FullyQualifiedName~LiveViewPolishAssetTests|FullyQualifiedName~WebAssetContentTests|FullyQualifiedName~OsmdAssetTests"
```

Expected: `Passed!  - Failed: 0, Passed: 16, Skipped: 0, Total: 16`

**Commit:**

```bash
but status -fv
but commit stage5-area-d-liveview -m "feat(liveview): add VU meter, device name, status badge, and take-output markup" --changes <ids> --status-after
```

---

## Task 33 — `app.js`: handle "level" events, reveal the take output by polling `/files/`

**Files**
- `src/AudioClaudio.Cli/wwwroot/app.js`
- `tests/AudioClaudio.Tests/LiveView/LiveViewPolishAssetTests.cs`

**Write the failing test.** Add to `LiveViewPolishAssetTests.cs`:

```csharp
    [Fact]
    [Trait("Category", "Fast")]
    public void AppJsHandlesLevelEventsAndUpdatesTheMeter()
    {
        string js = File.ReadAllText(Path.Combine(WwwRoot, "app.js"));

        Assert.Contains("addEventListener(\"level\"", js);
        Assert.Contains("vu-meter-fill", js);
        Assert.Contains("device-name", js);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void AppJsRevealsTakeOutputByPollingTheFilesRoute()
    {
        string js = File.ReadAllText(Path.Combine(WwwRoot, "app.js"));

        Assert.Contains("/files/", js);
        Assert.Contains("take-output", js);
    }
```

**Run it — fails:**

```bash
dotnet test tests/AudioClaudio.Tests --filter "FullyQualifiedName~LiveViewPolishAssetTests"
```

Expected: substrings not found (`Failed: 2, Passed: 4`).

**Minimal implementation.** Replace `src/AudioClaudio.Cli/wwwroot/app.js` in full (every literal
substring the pre-existing `WebAssetContentTests` checks is preserved verbatim):

```js
const osmd = new opensheetmusicdisplay.OpenSheetMusicDisplay("osmd-container");
const source = new EventSource("/events");

const statusBadge = document.getElementById("status-badge");
const deviceNameEl = document.getElementById("device-name");
const vuMeterFill = document.getElementById("vu-meter-fill");
const vuMeter = document.getElementById("vu-meter");
const takeOutput = document.getElementById("take-output");
const recreationPlayer = document.getElementById("recreation-player");

const TAKE_FILES = {
  "download-raw": "raw.mid",
  "download-score-mid": "score.mid",
  "download-score-musicxml": "score.musicxml",
  "download-recreation": "recreation.wav",
};

function setStatus(state, label) {
  statusBadge.className = "status status-" + state;
  statusBadge.textContent = label;
}

source.addEventListener("score", (event) => {
  const xml = atob(event.data);
  osmd.load(xml).then(() => osmd.render());
});

source.addEventListener("clear", () => {
  osmd.clear();
  hideTakeOutput();
});

source.addEventListener("level", (event) => {
  const separatorIndex = event.data.indexOf("|");
  const rms = parseFloat(event.data.slice(0, separatorIndex));
  const device = event.data.slice(separatorIndex + 1);
  updateMeter(rms, device);
});

function updateMeter(rms, device) {
  const fraction = Math.max(0, Math.min(1, rms / 0.35));
  vuMeterFill.style.width = (fraction * 100).toFixed(0) + "%";
  vuMeter.setAttribute("aria-valuenow", fraction.toFixed(2));
  deviceNameEl.textContent = device || "Unknown microphone";
  setStatus("recording", "● Recording");
}

function hideTakeOutput() {
  takeOutput.hidden = true;
  recreationPlayer.removeAttribute("src");
}

async function fileExists(name) {
  try {
    const response = await fetch("/files/" + name);
    return response.ok;
  } catch {
    return false;
  }
}

async function revealTakeOutputWhenReady(attempts = 20, delayMs = 300) {
  for (let i = 0; i < attempts; i++) {
    if (await fileExists("score.musicxml")) break;
    await new Promise((resolve) => setTimeout(resolve, delayMs));
  }

  for (const [id, fileName] of Object.entries(TAKE_FILES)) {
    document.getElementById(id).href = "/files/" + fileName;
  }
  if (await fileExists("recreation.wav")) {
    recreationPlayer.src = "/files/recreation.wav";
  }
  takeOutput.hidden = false;
  setStatus("idle", "Idle");
}

const startButton = document.getElementById("start-recording");
const stopButton = document.getElementById("stop-recording");

startButton.addEventListener("click", async () => {
  startButton.disabled = true;
  stopButton.disabled = false;
  hideTakeOutput();
  setStatus("recording", "● Recording");
  const params = new URLSearchParams({
    record: document.getElementById("opt-record").checked,
    skipSilence: document.getElementById("opt-skip-silence").checked,
    noteNames: document.getElementById("opt-note-names").checked,
    title: document.getElementById("score-title").value,
  });
  await fetch("/record/start?" + params.toString(), { method: "POST" });
});

stopButton.addEventListener("click", async () => {
  stopButton.disabled = true;
  setStatus("finishing", "Saving take…");
  await fetch("/record/stop", { method: "POST" });
  startButton.disabled = false;
  await revealTakeOutputWhenReady();
});
```

**Run it — passes:**

```bash
dotnet test tests/AudioClaudio.Tests --filter "FullyQualifiedName~AudioClaudio.Tests.LiveView"
```

Expected: `Passed!  - Failed: 0, Passed: 32, Skipped: 0, Total: 32`

Then the full suite:

```bash
dotnet test
```

Expected: `Build succeeded` and `Passed!` with 0 failures.

**Commit:**

```bash
but status -fv
but commit stage5-area-d-liveview -m "feat(liveview): handle level events and reveal take output/downloads in app.js" --changes <ids> --status-after
```

**After Tasks 25–33:** `--view` failure still degrades to plain `listen` — every edit lives either
inside the existing `if (view) { try { server.Start(); } catch { ... } }` guard (Task 30) or in
files/routes inert unless `--view` is used. **R11.2** (MuseScore) and the Stage 5
manual-acceptance checklist (Task 42) remain human gates, not closeable by an automated test.

---

# Area C — Self-Contained macOS Packaging (Tasks 34–37)

**Scope:** S5.7, S5.8, S5.9. No Windows work (explicit non-goal). No AOT — ONNX Runtime and
PortAudioSharp2 ship native libraries with P/Invoke patterns AOT does not handle. **There is no
separate `--version` task in this area** — Area A's kernel already implements `--version` (Task 9),
and Area B wires it through `AppBuilder`/`CommandLineApp`'s constructor (Task 13); the packaging
smoke test in Task 35 exercises that existing implementation, nothing new.

**Decision: how the packaged binary finds its assets (S5.8).** `ModelLocator`,
`SoundFontLocator`, and `TranskunModelLocator` (`src/AudioClaudio.Cli/Composition/*.cs`) already
walk **up** from `AppContext.BaseDirectory` looking for a `fixtures/models/...` or
`fixtures/soundfont/...` subfolder at each ancestor. For a self-contained, non-single-file publish,
`AppContext.BaseDirectory` **is** the folder holding the executable — so copying the repo's
`fixtures/models` and `fixtures/soundfont` subtrees in **verbatim**, as an immediate sibling of the
binary, satisfies that walk on its first iteration — no locator code changes needed.
`wwwroot/` needs no work either — it is already a `Content` item in `AudioClaudio.Cli.csproj`
(`CopyToOutputDirectory="PreserveNewest"`), so `dotnet publish` copies it automatically.

## Branch setup (once, before Task 34)

```bash
but status -fv
but branch new stage5-area-c-packaging
but mark stage5-area-c-packaging
```

---

## Task 34 — Name the published executable `claudio`

**Files:**
- `src/AudioClaudio.Cli/AudioClaudio.Cli.csproj` (edit)
- `tests/AudioClaudio.Tests/PackagingTests.cs` (new)

**Write the failing test:**

```csharp
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests;

public class PackagingTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Cli_project_publishes_the_binary_as_claudio()
    {
        var csproj = File.ReadAllText(RepoPaths.Src("AudioClaudio.Cli"));

        Assert.Contains("<AssemblyName>claudio</AssemblyName>", csproj);
    }
}
```

**Run it, watch it fail:**

```bash
dotnet test --filter FullyQualifiedName~PackagingTests.Cli_project_publishes_the_binary_as_claudio
```

Expected: `Assert.Contains() Failure ... Not found: "<AssemblyName>claudio</AssemblyName>"`

**Minimal implementation.** Add one line to the existing `<PropertyGroup>`:

```xml
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>claudio</AssemblyName>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
```

This renames only the output assembly/exe (`AudioClaudio.Cli.dll` → `claudio.dll`); the project
name (folder, `.csproj` filename, `ProjectReference` includes) is untouched.

**Run it, watch it pass:**

```bash
dotnet test --filter FullyQualifiedName~PackagingTests.Cli_project_publishes_the_binary_as_claudio
```

Expected: `Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1`

Corroborated by a real publish (manual, not part of the automated test):

```bash
dotnet publish src/AudioClaudio.Cli/AudioClaudio.Cli.csproj -r osx-arm64 -c Release --self-contained true -o /tmp/pub-claudio-test
ls /tmp/pub-claudio-test | grep claudio
```

**Commit:**

```bash
but status -fv
but commit stage5-area-c-packaging -m "chore(cli): publish the executable as claudio (Stage 5 packaging, S5.7)" --changes <ids> --status-after
```

---

## Task 35 — `scripts/smoke-test-packaged.sh` (shared by macOS packaging and CI mechanics)

**Files:**
- `scripts/smoke-test-packaged.sh` (new)
- `tests/AudioClaudio.Tests/PackagingScriptsTests.cs` (new)

**Write the failing test:**

```csharp
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests;

public class PackagingScriptsTests
{
    private static string SmokeTestScript =>
        Path.Combine(RepoPaths.RepoRoot, "scripts", "smoke-test-packaged.sh");

    [Fact]
    [Trait("Category", "Fast")]
    public void Smoke_test_script_exists_and_exercises_version_transcribe_and_render()
    {
        Assert.True(File.Exists(SmokeTestScript), $"missing: {SmokeTestScript}");

        var script = File.ReadAllText(SmokeTestScript);

        Assert.Contains("--version", script);
        Assert.Contains("transcribe", script);
        Assert.Contains("render", script);
        Assert.Contains("STAGE_DIR", script);
    }
}
```

**Run it, watch it fail:**

```bash
dotnet test --filter FullyQualifiedName~PackagingScriptsTests.Smoke_test_script_exists_and_exercises_version_transcribe_and_render
```

Expected: `Assert.True() Failure ... missing: .../scripts/smoke-test-packaged.sh`

**Minimal implementation.** `scripts/smoke-test-packaged.sh` (`chmod +x` after creating). Note the
`--version` invocation here exercises **Area A's kernel `--version`** (Task 9) via `AppBuilder`
(Task 13) — there is no separate version command to maintain:

```bash
#!/usr/bin/env bash
# Smoke-test a packaged (published + fixtures-staged) claudio build: --version, a
# fixture transcribe, and a render, run against the PACKAGED output -- never the dev
# tree (S5.9). Shared by scripts/package-macos.sh (osx-arm64, run on a Mac) and CI's
# linux-x64 "packaging mechanics" job.
#
# Usage: scripts/smoke-test-packaged.sh <staged-dir> <fixture.wav>

set -euo pipefail

STAGE_DIR="$1"
FIXTURE_WAV="$2"
EXE="$STAGE_DIR/claudio"

if [ ! -x "$EXE" ]; then
    echo "error: $EXE not found or not executable" >&2
    exit 1
fi

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

echo "==> $EXE --version"
VERSION_OUTPUT="$("$EXE" --version)"
echo "$VERSION_OUTPUT"
if ! echo "$VERSION_OUTPUT" | grep -qi "claudio"; then
    echo "error: --version output did not mention 'claudio': $VERSION_OUTPUT" >&2
    exit 1
fi

echo "==> $EXE transcribe (default/polyphonic engine -- exercises ModelLocator)"
"$EXE" transcribe "$FIXTURE_WAV" --tempo 120 --out-dir "$WORK_DIR"
for f in raw.mid score.mid score.musicxml; do
    if [ ! -s "$WORK_DIR/$f" ]; then
        echo "error: expected non-empty $WORK_DIR/$f after transcribe" >&2
        exit 1
    fi
done

echo "==> $EXE render (exercises SoundFontLocator)"
"$EXE" render "$WORK_DIR/score.mid" "$WORK_DIR/recreation.wav"
if [ ! -s "$WORK_DIR/recreation.wav" ]; then
    echo "error: expected non-empty $WORK_DIR/recreation.wav after render" >&2
    exit 1
fi

echo "==> Smoke test passed."
```

**Run it, watch it pass:**

```bash
dotnet test --filter FullyQualifiedName~PackagingScriptsTests.Smoke_test_script_exists_and_exercises_version_transcribe_and_render
```

Expected: `Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1`

**Commit:**

```bash
but status -fv
but commit stage5-area-c-packaging -m "feat: shared packaged-build smoke test (--version, transcribe, render — S5.9)" --changes <ids> --status-after
```

---

## Task 36 — `scripts/package-macos.sh` (self-contained osx-arm64 publish → zip)

**Files:**
- `scripts/package-macos.sh` (new)
- `tests/AudioClaudio.Tests/PackagingScriptsTests.cs` (edit — add a method)

**Write the failing test.** Add to the class from Task 35:

```csharp
    private static string PackageMacosScript =>
        Path.Combine(RepoPaths.RepoRoot, "scripts", "package-macos.sh");

    [Fact]
    [Trait("Category", "Fast")]
    public void Package_macos_script_publishes_self_contained_osx_arm64_stages_fixtures_and_zips()
    {
        Assert.True(File.Exists(PackageMacosScript), $"missing: {PackageMacosScript}");

        var script = File.ReadAllText(PackageMacosScript);

        Assert.Contains("-r osx-arm64", script);
        Assert.Contains("--self-contained", script);
        Assert.Contains("dotnet publish", script);
        Assert.DoesNotContain("PublishAot", script);
        Assert.Contains("fixtures/models", script);
        Assert.Contains("fixtures/soundfont", script);
        Assert.Contains("smoke-test-packaged.sh", script);
        Assert.Contains("zip", script);
        Assert.Contains("claudio-macos-arm64", script);
    }
```

**Run it, watch it fail:**

```bash
dotnet test --filter FullyQualifiedName~PackagingScriptsTests.Package_macos_script_publishes_self_contained_osx_arm64_stages_fixtures_and_zips
```

Expected: `Assert.True() Failure ... missing: .../scripts/package-macos.sh`

**Minimal implementation.** `scripts/package-macos.sh` (`chmod +x` after creating):

```bash
#!/usr/bin/env bash
# Package the self-contained macOS (osx-arm64) claudio build into a distributable zip.
#
# S5.7/S5.8: a self-contained (NOT trimmed, NOT AOT -- ONNX Runtime and PortAudioSharp2
# ship native libraries and P/Invoke patterns AOT does not support) publish for
# osx-arm64, with fixtures/models + fixtures/soundfont shipped VERBATIM beside the
# `claudio` executable, zipped for distribution.
#
# Usage: scripts/package-macos.sh [output-dir]
#   output-dir defaults to artifacts/ (already gitignored).

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${1:-"$REPO_ROOT/artifacts"}"
RID="osx-arm64"
CONFIGURATION="Release"
STAGE_NAME="claudio-macos-arm64"
STAGE_DIR="$OUT_DIR/$STAGE_NAME"
ZIP_PATH="$OUT_DIR/$STAGE_NAME.zip"

echo "==> Cleaning $STAGE_DIR"
rm -rf "$STAGE_DIR" "$ZIP_PATH"
mkdir -p "$STAGE_DIR"

echo "==> Publishing self-contained $RID (no AOT: ONNX Runtime + PortAudioSharp2 ship native libs)"
dotnet publish "$REPO_ROOT/src/AudioClaudio.Cli/AudioClaudio.Cli.csproj" \
    -r "$RID" \
    -c "$CONFIGURATION" \
    --self-contained true \
    -o "$STAGE_DIR"

echo "==> Staging fixtures/models + fixtures/soundfont beside the binary (verbatim, S5.8)"
mkdir -p "$STAGE_DIR/fixtures"
cp -R "$REPO_ROOT/fixtures/models" "$STAGE_DIR/fixtures/models"
cp -R "$REPO_ROOT/fixtures/soundfont" "$STAGE_DIR/fixtures/soundfont"

if [ ! -x "$STAGE_DIR/claudio" ]; then
    echo "error: expected $STAGE_DIR/claudio to exist and be executable after publish" >&2
    exit 1
fi

echo "==> Smoke-testing the staged output before zipping (S5.9)"
"$REPO_ROOT/scripts/smoke-test-packaged.sh" "$STAGE_DIR" "$REPO_ROOT/fixtures/golden/two-bar.wav"

echo "==> Zipping to $ZIP_PATH"
( cd "$OUT_DIR" && zip -r -q "$(basename "$ZIP_PATH")" "$STAGE_NAME" )

echo "==> Done: $ZIP_PATH ($(du -sh "$ZIP_PATH" | cut -f1))"
```

**Run it, watch it pass:**

```bash
dotnet test --filter FullyQualifiedName~PackagingScriptsTests.Package_macos_script_publishes_self_contained_osx_arm64_stages_fixtures_and_zips
```

Expected: `Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2`

Corroborated by a real, full end-to-end run (executed manually, then reverted — the acceptance
evidence for S5.7/S5.8/S5.9 together): `scripts/package-macos.sh /tmp/pkg-test-out` publishes,
stages fixtures, smoke-tests, and zips successfully; unzip-and-run in a clean directory (no .NET
installed) confirms no prerequisites. Note the honest size: the zip is ~128 MB (dominated by the
committed `transkun.onnx`, `libonnxruntime.dylib`, and `GeneralUser-GS.sf2` — no trimming
attempted).

**Commit:**

```bash
but status -fv
but commit stage5-area-c-packaging -m "feat: scripts/package-macos.sh — self-contained osx-arm64 publish + zip (S5.7, S5.8)" --changes <ids> --status-after
```

---

## Task 37 — CI packaging-mechanics smoke (linux-x64) — the honest gap

**Files:**
- `.github/workflows/ci.yml` (edit — new job)
- `tests/AudioClaudio.Tests/CiWorkflowTests.cs` (edit — add a method)

CI runs on `ubuntu-latest`, so it cannot build or run the `osx-arm64` artifact
`scripts/package-macos.sh` produces. This job proves the **packaging mechanics** — a
self-contained publish + fixtures staged beside the binary + the same `smoke-test-packaged.sh` —
using a `linux-x64` self-contained build instead. **The shipped `osx-arm64` artifact itself is
never validated by CI** — that is the Task 42 Mac manual-acceptance step, stated here plainly.

**Write the failing test.** Extend the existing `CiWorkflowTests` class:

```csharp
    [Fact]
    [Trait("Category", "Fast")]
    public void Ci_workflow_smokes_packaging_mechanics_on_linux_x64()
    {
        var yaml = File.ReadAllText(WorkflowPath);

        Assert.Contains("linux-x64", yaml);
        Assert.Contains("--self-contained", yaml);
        Assert.Contains("scripts/smoke-test-packaged.sh", yaml);
        Assert.Contains("PACKAGING MECHANICS", yaml);
    }
```

**Run it, watch it fail:**

```bash
dotnet test --filter FullyQualifiedName~CiWorkflowTests.Ci_workflow_smokes_packaging_mechanics_on_linux_x64
```

Expected: `Assert.Contains() Failure ... Not found: "linux-x64"`

**Minimal implementation.** Append a second job to `.github/workflows/ci.yml`:

```yaml
  package-smoke-linux:
    name: Packaging mechanics smoke (linux-x64)
    runs-on: ubuntu-latest
    # CI runs on Linux, so this job can only prove the PACKAGING MECHANICS -- a
    # self-contained publish + fixtures staged beside the binary + a --version/
    # transcribe/render smoke pass -- via a linux-x64 build (S5.9). It does NOT
    # validate the shipped osx-arm64 artifact that scripts/package-macos.sh produces;
    # that binary is a Mac manual-acceptance step (Task 42).
    steps:
      - name: Checkout
        uses: actions/checkout@v5

      - name: Set up .NET 10 SDK
        uses: actions/setup-dotnet@v5
        with:
          dotnet-version: '10.0.x'

      - name: Publish self-contained linux-x64
        run: >
          dotnet publish src/AudioClaudio.Cli/AudioClaudio.Cli.csproj
          -r linux-x64 -c Release --self-contained true
          -o artifacts/publish-linux-x64

      - name: Stage fixtures beside the binary (mirrors scripts/package-macos.sh)
        run: |
          mkdir -p artifacts/publish-linux-x64/fixtures
          cp -R fixtures/models artifacts/publish-linux-x64/fixtures/models
          cp -R fixtures/soundfont artifacts/publish-linux-x64/fixtures/soundfont

      - name: Smoke test the packaged output
        run: scripts/smoke-test-packaged.sh artifacts/publish-linux-x64 fixtures/golden/two-bar.wav
```

**Run it, watch it pass:**

```bash
dotnet test --filter FullyQualifiedName~CiWorkflowTests.Ci_workflow_smokes_packaging_mechanics_on_linux_x64
```

Expected: `Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1`

**Commit:**

```bash
but status -fv
but commit stage5-area-c-packaging -m "ci: smoke-test packaging mechanics on linux-x64 (S5.9; osx-arm64 stays a Mac manual-acceptance step)" --changes <ids> --status-after
```

**Summary of what's proven vs. what remains a human gate:**

| Requirement | How this area satisfies it | Automated in CI? |
|---|---|---|
| S5.7 — `package-macos.sh` produces the zip, unzip-and-run, no prerequisites | Task 36 | No — macOS-only tooling; CI is Linux |
| S5.8 — packaged binary resolves models/SoundFont/`wwwroot/` from shipped assets | Tasks 34–36 (zero locator changes) | Partially — Task 37 proves the same mechanics on linux-x64 |
| S5.9 — smoke test exercises `--version` and a fixture `transcribe` from packaged output | Tasks 35–37 | Yes for mechanics (Task 37); the `osx-arm64` artifact itself is not — that is Task 42, a Mac manual-acceptance step |

No Windows tasks (explicit non-goal). `AOT` is not used anywhere in this area — every publish
command is plain `--self-contained`, and Task 36's test explicitly asserts `PublishAot` does not
appear in `package-macos.sh`.

---

# Area E — Docs, Honesty, Acceptance (Tasks 38–43)

**Scope:** the documentation, honesty, and human-acceptance half of Stage 5's success criteria —
touches no production code, only `DECISIONS.md`, `README.md`, `CLAUDE.md`,
`docs/plans/README.md`, a new manual-acceptance checklist, and the doc-lint tests that pin all of
it. Depends on Areas A–D having landed (or at minimum, this area's own tasks describe the shipped
end state accurately once they have).

## Branch setup (once, before Task 38)

```bash
but status -fv
but branch new stage5-area-e-docs
but mark stage5-area-e-docs
```

**Sequencing:** Task 38 (DECISIONS.md) first, since README/CLAUDE.md cross-reference it by name;
Task 43 (cross-document consistency) last, since it checks what Tasks 38–42 produced.

---

## Task 38: `DECISIONS.md` — record the six locked Stage 5 decisions

**Files:**
- Modify: `DECISIONS.md`
- Create: `tests/AudioClaudio.Tests/Ship/DecisionsLogTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Ship;

/// <summary>
/// Pins the "v2 Stage 5" DECISIONS.md entry: all six locked decisions and at least one
/// rejected alternative per decision, so the log cannot silently lose a decision or its
/// "why not" (CLAUDE.md §1 rule 2/7).
/// </summary>
public class DecisionsLogTests
{
    private static readonly string Decisions =
        File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, "DECISIONS.md"));

    [Fact]
    [Trait("Category", "Fast")]
    public void Records_the_v2_stage_5_heading()
    {
        Assert.Contains("## v2 Stage 5", Decisions, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("hand-rolled")]
    [InlineData("claudio-macos-arm64.zip")]
    [InlineData("osx-arm64")]
    [InlineData("NO_COLOR")]
    public void Names_each_locked_decisions_choice(string token)
    {
        Assert.Contains(token, Decisions, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("System.CommandLine")]
    [InlineData("dotnet tool install")]
    [InlineData("Windows")]
    public void Names_a_rejected_alternative_per_decision(string token)
    {
        Assert.Contains(token, Decisions, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Names_all_four_live_view_polish_items()
    {
        Assert.Contains("VU meter", Decisions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("in-page playback", Decisions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("downloads", Decisions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dark mode", Decisions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Carries_forward_both_open_human_gates()
    {
        Assert.Contains("R11.2", Decisions, StringComparison.Ordinal);
        Assert.Contains("S5-accept", Decisions, StringComparison.Ordinal);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Ship.DecisionsLogTests"
```

Expected FAILURE: `Records_the_v2_stage_5_heading` and most theory cases red — `DECISIONS.md`
currently ends at the Stage 4 entry; there is no "v2 Stage 5" heading yet.

**Step 3 — Minimal implementation:** append to the end of `DECISIONS.md`:

```markdown

## v2 Stage 5 — UX, robustness, packaging (2026-07-11)

Turns the deferred Stage 5 of the [v2 release workplan](docs/plans/2026-07-10-v2-release-workplan.md)
into a concrete deliverable: `claudio` becomes a tool a person installs and runs, not a `dotnet run`
demo. Design validated with Cornelius, 2026-07-11 (see
`docs/plans/2026-07-11-v2-stage5-ux-packaging-design.md`); six decisions locked before implementation:

| # | Decision | Choice | Rejected alternative(s) |
|---|---|---|---|
| 1 | Scope | Terminal UX + live-view polish + packaging, landed together as one Stage 5 | Splitting any one surface out alone — the three surfaces share almost no code, but shipping them separately would mean re-opening "is Stage 5 done" three times for one user-facing story |
| 2 | CLI parser | A small **hand-rolled**, declaration-driven kernel (no new dependency): `CliCommand`/`CliOption`/`CommandLineApp`/`ParsedArgs`/`HelpRenderer`/`AnsiStyler` in `Cli/` | `System.CommandLine` — still shipping as `3.0.0-preview` with ongoing API churn; `Spectre.Console.Cli` — a heavier dependency with an opinionated visual style this project didn't ask for |
| 3 | Distribution | Self-contained, no prerequisites, as an **app-folder zip** (`claudio-macos-arm64.zip`: a `claudio` executable beside `fixtures/` — models, SoundFont — and `wwwroot/`) | `dotnet tool install` — requires the .NET SDK, defeating "no prerequisites"; framework-dependent publish — still requires .NET installed; a single-file exe — forces ~200 MB of per-run extraction |
| 4 | Platforms | **macOS `osx-arm64` only**, this cycle | Windows — an **explicit deferred non-goal**, not an oversight: the two native deps (ONNX Runtime + PortAudioSharp2) need their own Windows verification, a real cost this cycle declines to pay |
| 5 | Live view | All four polish items land together on the existing `LiveNotationServer` SSE server: live input feedback (**VU meter** + device name), **in-page playback** of a finished take, one-click **downloads** of its four files, and visual polish (**dark mode**, responsive, styled) | Splitting the four — they all ride the same SSE/route surface, so there is no natural seam that makes shipping a subset materially cheaper |
| 6 | Terminal color | Tasteful ANSI through `AnsiStyler`, auto-off when piped, when `NO_COLOR` is set, or when `--no-color` is passed | Plain-only output (loses the readability win); always-on color (breaks piping/redirection and CI logs) |

These carry the same discipline as every other v2 stage: each decision states its rejected
alternative(s). Requirements **S5.1–S5.12** are the executable form of decisions 2–6 above. Two
human gates are tracked, not pretended: **R11.2** — the MuseScore GUI load check, still open,
carried forward unchanged; and **S5-accept** — the Stage 5 manual-acceptance checklist
(`docs/plans/2026-07-11-v2-stage5-manual-acceptance.md`): the packaged `claudio` runs each command
from the unzipped folder, and the four live-view items work in a real browser. CI cannot validate
the shipped `osx-arm64` artifact itself (it runs on Linux, so packaging *mechanics* are
smoke-tested via a `linux-x64` self-contained build instead) — that gap is stated here plainly
rather than silently assumed away.
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Ship.DecisionsLogTests"
```

Expected PASS: all facts/theories green.

**Step 5 — Commit:**

```bash
but status -fv
but commit stage5-area-e-docs -m "docs: DECISIONS.md v2 Stage 5 — six locked decisions" --changes <ids> --status-after
```

---

## Task 39: `README.md` — the `claudio` install/run story replaces `dotnet run`

**Files:**
- Modify: `README.md`
- Modify: `tests/AudioClaudio.Tests/Ship/ReadmeCompletenessTests.cs`

**Step 1 — Write the failing test.** Add to the existing `ReadmeCompletenessTests` class:

```csharp
    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("claudio-macos-arm64.zip")]
    [InlineData("scripts/package-macos.sh")]
    [InlineData("unzip")]
    [InlineData("./claudio")]
    public void Documents_the_packaged_install_and_run_story(string token)
    {
        Assert.Contains(token, Readme, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void States_the_macos_only_packaging_limitation_honestly()
    {
        Assert.Contains("osx-arm64", Readme, StringComparison.Ordinal);
        Assert.Contains("Windows", Readme, StringComparison.Ordinal);
        Assert.Contains("deferred", Readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void No_longer_claims_there_is_no_packaged_binary()
    {
        Assert.DoesNotContain("no packaged", Readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no self-contained", Readme, StringComparison.OrdinalIgnoreCase);
    }
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Ship.ReadmeCompletenessTests"
```

Expected FAILURE: the packaged-install tokens and `osx-arm64` are absent; the stale "no packaged"
phrase is still present.

**Step 3 — Minimal implementation.** Replace the `dotnet run` block (right after the
`evaluate-audio` prose) with an install section:

```markdown
### Installing `claudio`

The supported way to run Audio Claudio is the packaged, self-contained `claudio`
executable — no .NET install, no `dotnet run`. **macOS (`osx-arm64`) only this
cycle**; Windows packaging is an explicit deferred non-goal, not an oversight
(see `DECISIONS.md`, "v2 Stage 5").

```bash
scripts/package-macos.sh                       # -> artifacts/claudio-macos-arm64.zip
unzip artifacts/claudio-macos-arm64.zip -d claudio-macos-arm64
cd claudio-macos-arm64
```

The unzipped folder is everything it needs — the ONNX models, the committed
SoundFont, and the live-view `wwwroot/` all ship beside the binary under
`fixtures/`, so nothing is downloaded or extracted per run:

```bash
./claudio --version
./claudio transcribe song.wav --key -4 --out-dir out/   # polyphonic (default)
./claudio transcribe song.wav --mono --out-dir out/     # monophonic, tempo auto-estimated
./claudio listen --tempo 100 --view --out-dir out/
./claudio play out/score.mid
./claudio render out/score.mid out/score.wav
./claudio evaluate out/score.mid reference.mid --onset-tolerance-ms 50 --warp
```

During development (working in a clone, iterating on source), the identical
command surface runs via `dotnet run` instead of the packaged binary:

```bash
dotnet run --project src/AudioClaudio.Cli -- transcribe song.wav --key -4 --out-dir out/
dotnet run --project src/AudioClaudio.Cli -- listen --tempo 100 --out-dir out/
```
```

Replace the "No packaged binary yet" Limitations bullet:

```markdown
- **macOS-only packaged binary, this cycle.** `scripts/package-macos.sh` produces
  a self-contained `claudio-macos-arm64.zip` — unzip and run `./claudio`, no
  .NET install required (see Usage, above). Windows packaging is an **explicit
  deferred non-goal** for this cycle, not an oversight (see `DECISIONS.md`,
  "v2 Stage 5") — building from source still works on any platform the .NET 10
  SDK supports; only the prerequisite-free packaged artifact is macOS-only.
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Ship.ReadmeCompletenessTests"
```

Expected PASS: every existing case still green plus all three new cases.

**Step 5 — Commit:**

```bash
but status -fv
but commit stage5-area-e-docs -m "docs: README — claudio install/run story replaces dotnet run" --changes <ids> --status-after
```

---

## Task 40: `CLAUDE.md` — status note + §7 CLI summary brought current

**Files:**
- Modify: `CLAUDE.md`
- Create: `tests/AudioClaudio.Tests/Ship/ClaudeMdCompletenessTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Ship;

public class ClaudeMdCompletenessTests
{
    private static readonly string ClaudeMd =
        File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, "CLAUDE.md"));

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("claudio-macos-arm64.zip")]
    [InlineData("osx-arm64")]
    [InlineData("v2 Stage 5")]
    public void Status_note_records_the_stage_5_landing(string token)
    {
        Assert.Contains(token, ClaudeMd, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("claudio transcribe")]
    [InlineData("claudio notate")]
    [InlineData("claudio listen")]
    [InlineData("claudio play")]
    [InlineData("claudio render")]
    [InlineData("claudio evaluate")]
    [InlineData("claudio evaluate-audio")]
    public void Cli_summary_documents_every_dispatched_command(string invocation)
    {
        Assert.Contains(invocation, ClaudeMd, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("--version")]
    [InlineData("--no-color")]
    [InlineData("did you mean")]
    public void Cli_summary_names_the_kernels_global_behaviors(string token)
    {
        Assert.Contains(token, ClaudeMd, StringComparison.OrdinalIgnoreCase);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Ship.ClaudeMdCompletenessTests"
```

Expected FAILURE: the status note says Stage 5 is deferred to v2.1, not landed; §7 lacks `notate`
and `evaluate-audio`; §7 lacks `--version`/`--no-color`/"did you mean".

**Step 3 — Minimal implementation.** Replace the "Where the project is right now" Stage 5 sentence:

```markdown
native-reference MIDIs; the third guarantee tier is now earned). **4f (the HuggingFace publish) shipped
post-v2.0.0** — the complete Transkun-ONNX package is public at
<https://huggingface.co/TuesdayCrowd/transkun-onnx> (see DECISIONS.md "v2 Stage 4"). **Stage 5 (UX,
robustness, packaging) has now landed too** (Cornelius, 2026-07-11 — "publish this as v2, save Stage 5 for
v2.1"; this is that v2.1 work): a self-contained, prerequisite-free `claudio-macos-arm64.zip`
(`scripts/package-macos.sh`; **macOS `osx-arm64` only this cycle** — Windows packaging is an explicit
deferred non-goal, not an oversight); a hand-rolled CLI kernel (`Cli/CliCommand`/`CliOption`/
`CommandLineApp`/`HelpRenderer`/`AnsiStyler`) generating every `--help`, usage line, and value
validation from one declaration per command, an unknown command/flag failing with a Levenshtein "did
you mean…" suggestion, and no user-reachable path printing a raw stack trace (`--debug` shows it on
request); new global `--version` and `--no-color` (also honoring `NO_COLOR` and auto-off when
piped). `listen --view`'s SSE page gained all four polish items — a live VU meter + device name,
in-page playback of a finished take, one-click downloads of its four files, and a restyled
dark-mode-aware layout — with the original `--view` guarantee (plain `listen` unaffected if the
server can't start) unchanged. See DECISIONS.md "v2 Stage 4"/"v2 Stage 3"/"v2 Stage 5".
```

Insert a new bullet before the `docs/plans/` bullet:

```markdown
- **Stage 5 acceptance is a human gate, not just CI.**
  `docs/plans/2026-07-11-v2-stage5-manual-acceptance.md` is the checklist Cornelius runs
  against the actual packaged binary and a real browser before Stage 5 is called done; it
  carries forward the still-open **R11.2** MuseScore human gate (untouched by this stage).
```

Replace §7's CLI summary block:

```markdown
## 7. CLI summary (composition root)

Install: unzip `claudio-macos-arm64.zip` (`scripts/package-macos.sh`; macOS `osx-arm64`
only this cycle — Windows is an explicit deferred non-goal) and run `./claudio <cmd>` — no
.NET install needed. During development, the identical surface runs via
`dotnet run --project src/AudioClaudio.Cli -- <cmd>`. Every command below has a generated
`<cmd> --help`; global flags are `--help`, `--version`, `--no-color` (also honors
`NO_COLOR` / auto-off when piped), and `--debug` (show the stack trace on an unexpected
error). An unknown command/flag fails with a Levenshtein "did you mean…" suggestion and a
non-zero exit; every other failure reads as a sentence, never a raw stack trace.

```
claudio transcribe <in.wav> [--tempo 120] [--out-dir .] [--note-names] [--mono] [--legato] [--coarse-rhythm] [--model <path|transkun>] [--key <fifths>] [--triplets] [--onset-threshold <v>] [--frame-threshold <v>] [--min-note-len <frames>]   # → raw.mid, score.mid, score.musicxml; POLYPHONIC (Basic Pitch, grand staff) by default, --mono for monophonic YIN (auto-estimates tempo when --tempo is omitted; --legato/--coarse-rhythm are --mono-only opt-ins); --model transkun selects the self-contained Transkun engine; --note-names adds a scientific-pitch-name lyric under each note; --key auto-detects the key signature (override to declare it); --triplets engraves triplets; the three thresholds tune polyphonic note density
claudio notate <in.mid> [--out-dir .] [--tempo N] [--key <fifths>] [--triplets] [--note-names]   # → score.mid, score.musicxml; engraves an EXISTING MIDI (e.g. a richer transcriber's output, with real durations + pedal) through the same grand-staff quantizer/writer transcribe uses, honoring the source's sustain pedal; tempo auto-estimated and key auto-detected unless declared
claudio listen [--tempo 100] [--out-dir .] [--view] [--record] [--skip-silence] [--note-names]  # live; writes the same trio on stop (--tempo auto-estimated if omitted); --view opens a live browser sheet-music page (VU meter + device name, in-page playback, one-click downloads, dark-mode styling; degrades to plain listen if the server can't start); --record also writes input.wav + recreation.wav (each session also archived under <out-dir>/<YYYYMMDD_HHMMSS>/); --skip-silence implies --record and collapses pauses >500ms in input.wav/recreation.wav only; --note-names shows each note's name (e.g. C4) beneath it in --view and score.musicxml
claudio play <file.mid> [--soundfont <path>]             # MeltySynth playback
claudio render <file.mid> <out.wav> [--soundfont <path>] # deterministic render
claudio evaluate <candidate.mid> <reference.mid> [--onset-tolerance-ms 50] [--align|--warp]  # note-level precision/recall/F1 of a transcription vs a reference; --align cancels a global tempo ratio, --warp (DTW) also removes local rubato and wins if both are given
claudio evaluate-audio <original.wav> <reproduction.wav>  # timbre-robust pitch-content (chroma) similarity between a real recording and a re-synthesis of its transcription; no ground-truth MIDI needed
```

The CLI is the only place adapters are constructed and wired to ports.
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Ship.ClaudeMdCompletenessTests"
```

Expected PASS: all theories/facts green.

**Step 5 — Commit:**

```bash
but status -fv
but commit stage5-area-e-docs -m "docs: CLAUDE.md — Stage 5 status + complete §7 CLI summary" --changes <ids> --status-after
```

---

## Task 41: `docs/plans/README.md` — status tracker gains the v2 Stage 5 row

**Files:**
- Modify: `docs/plans/README.md`
- Create: `tests/AudioClaudio.Tests/Ship/DocsPlansStatusTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Ship;

public class DocsPlansStatusTests
{
    private static readonly string PlansReadme =
        File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, "docs/plans/README.md"));

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("v2 Stage 5")]
    [InlineData("2026-07-11-v2-stage5-ux-packaging-design.md")]
    [InlineData("2026-07-11-v2-stage5-manual-acceptance.md")]
    public void Status_tracker_references_stage_5_and_its_docs(string token)
    {
        Assert.Contains(token, PlansReadme, StringComparison.Ordinal);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Ship.DocsPlansStatusTests"
```

Expected FAILURE: no "v2 Stage 5" row exists yet.

**Step 3 — Minimal implementation.** Insert a new section into `docs/plans/README.md` immediately
after the "Beyond v0.1.0 — Phase 2" table's closing paragraph:

```markdown
## v2 release cycle (see [`2026-07-10-v2-release-workplan.md`](2026-07-10-v2-release-workplan.md))

| Stage | Design | Status |
|---|---|---|
| 0 — Re-baseline & positioning | — | ✅ Done, shipped in **v2.0.0** |
| 1 — Earn polyphony (closed-loop F1 gate) | — | ✅ Done, shipped in **v2.0.0** |
| 2 — Core detection quality (velocity, legato, coarse-rhythm) | — | ✅ Done, shipped in **v2.0.0** |
| 3 — Notation quality (key detection, hand-split, triplets) | — | ✅ Done, shipped in **v2.0.0**; **R11.2** (MuseScore human gate) still open |
| 4 — Transkun engine via ONNX | — | ✅ Done, shipped in **v2.0.0**; **4f** (HuggingFace publish) landed post-ship |
| 5 — UX, robustness, packaging | [design](2026-07-11-v2-stage5-ux-packaging-design.md) | ✅ Done — CLI kernel (help/errors/color), packaged `claudio-macos-arm64.zip` (macOS `osx-arm64` only this cycle), `listen --view` polish (VU meter, in-page playback, downloads, dark mode). See [`DECISIONS.md`](../../DECISIONS.md) "v2 Stage 5" for the six locked decisions and [the manual-acceptance checklist](2026-07-11-v2-stage5-manual-acceptance.md) (S5-accept, plus **R11.2** carried forward) for the human gate |
| 6 — Ship v2.0.0 honestly | — | ✅ Done, tagged **v2.0.0** |
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Ship.DocsPlansStatusTests"
```

Expected PASS: all three tokens found.

**Step 5 — Commit:**

```bash
but status -fv
but commit stage5-area-e-docs -m "docs: docs/plans/README.md — v2 Stage 5 status row" --changes <ids> --status-after
```

---

## Task 42: Stage 5 manual-acceptance checklist (S5-accept) + carry forward R11.2

**Files:**
- Create: `docs/plans/2026-07-11-v2-stage5-manual-acceptance.md`
- Create: `tests/AudioClaudio.Tests/Ship/ManualAcceptanceChecklistTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Ship;

public class ManualAcceptanceChecklistTests
{
    private const string RelativePath = "docs/plans/2026-07-11-v2-stage5-manual-acceptance.md";

    [Fact]
    [Trait("Category", "Fast")]
    public void Checklist_file_exists()
    {
        Assert.True(File.Exists(Path.Combine(RepoPaths.RepoRoot, RelativePath)),
            $"Stage 5 manual-acceptance checklist missing: {RelativePath}");
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("--version")]
    [InlineData("transcribe")]
    [InlineData("listen")]
    [InlineData("stack trace")]
    public void Checklist_covers_the_packaged_binary_surface(string token)
    {
        var text = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, RelativePath));
        Assert.Contains(token, text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("VU meter")]
    [InlineData("playback")]
    [InlineData("download")]
    [InlineData("dark")]
    public void Checklist_covers_all_four_live_view_polish_items(string token)
    {
        var text = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, RelativePath));
        Assert.Contains(token, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Checklist_carries_r11_2_forward_as_still_open()
    {
        var text = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, RelativePath));
        Assert.Contains("R11.2", text, StringComparison.Ordinal);
        Assert.Contains("still open", text, StringComparison.OrdinalIgnoreCase);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Ship.ManualAcceptanceChecklistTests"
```

Expected FAILURE: `Checklist_file_exists` red; the whole class red on `FileNotFoundException` until
the file exists.

**Step 3 — Minimal implementation:** create the checklist file:

```markdown
# v2 Stage 5 — manual acceptance checklist (S5-accept)

> Human gate, like R11.2 below — no CI runs this. Cornelius checks every box on the
> actual packaged artifact, on a real Mac, in a real browser, before Stage 5 (and any
> release tag it ships under) is called done. CI cannot substitute for this: it builds
> and smoke-tests packaging *mechanics* via a `linux-x64` self-contained build (there is
> no macOS runner), so it never actually runs the shipped `osx-arm64` artifact.

## Packaged binary (`claudio-macos-arm64.zip`)

- [ ] `scripts/package-macos.sh` runs clean on a fresh checkout and produces
      `artifacts/claudio-macos-arm64.zip`.
- [ ] Unzip the artifact **somewhere other than the repo** — the true "a user just
      downloaded this" test.
- [ ] `./claudio --version` prints a version and build with no .NET SDK on the test
      machine's `PATH`.
- [ ] `./claudio --help` and `./claudio <cmd> --help` for every command render clean,
      complete help.
- [ ] `./claudio transcribe <fixture>.wav --out-dir out/` succeeds and writes
      `raw.mid`/`score.mid`/`score.musicxml` (polyphonic default).
- [ ] `./claudio transcribe <fixture>.wav --mono --out-dir out/` succeeds (monophonic path).
- [ ] `./claudio transcribe <fixture>.wav --model transkun --out-dir out/` succeeds — the
      Transkun ONNX model resolves from the shipped `fixtures/` directory.
- [ ] `./claudio listen` captures a few seconds of live mic input and writes the session
      trio on Ctrl+C.
- [ ] `./claudio play out/score.mid` and `./claudio render out/score.mid out/score.wav`
      both resolve the committed SoundFont from `fixtures/` with no `--soundfont` flag.
- [ ] `./claudio evaluate out/score.mid <reference>.mid` runs.
- [ ] An unknown flag (e.g. `--modl`) and a bad value (e.g. `--tempo abc`) each print a
      sentence-style error with a suggestion/exit code — no raw stack trace; `--debug`
      shows the stack trace on request.

## Live view (`listen --view`) — all four polish items, in a real browser

- [ ] **Input feedback:** the VU meter moves with live mic input and the shown device
      name matches the actual input device; idle vs. recording state is visually
      unambiguous.
- [ ] **In-page playback:** after a take finishes, `recreation.wav` (and `input.wav` if
      `--record`) plays back via the page's `<audio>` player with no separate download
      step required to preview it.
- [ ] **Downloads:** the finished take's `raw.mid`, `score.mid`, `score.musicxml`, and
      `recreation.wav` are each one click away from the page.
- [ ] **Visual polish:** the page respects the system dark/light mode
      (`prefers-color-scheme`), is usable at a phone-sized viewport, and carries a clear
      header/identity.
- [ ] `--view`'s failure-safety is unchanged: force-failing the server start (e.g. a port
      conflict) still leaves plain `listen` fully working with no `--view`.

## Carried-forward human gate

- [ ] **R11.2 — MuseScore GUI load check — still open.** No MuseScore in CI. Run
      `claudio notate <midi> --triplets --note-names` and a plain `transcribe`, open the
      resulting `score.musicxml` in MuseScore, and confirm both staves, clefs, the
      auto-detected key signature, dynamics, sustain-pedal lines, and triplet brackets all
      render cleanly. Record the pass in `DECISIONS.md` under "R11.2" once done — this item
      has been open since Step 11, and Stage 5 does not close it, only carries it forward.

**Sign-off:** date + initials here once every box above is checked on the actual
`osx-arm64` artifact — not inferred from the automated suite.
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Ship.ManualAcceptanceChecklistTests"
```

Expected PASS: the file exists; every token present.

**Step 5 — Commit:**

```bash
but status -fv
but commit stage5-area-e-docs -m "docs: Stage 5 manual-acceptance checklist (S5-accept), carries forward R11.2" --changes <ids> --status-after
```

---

## Task 43: Ship-style cross-document consistency tests

**Files:**
- Create: `tests/AudioClaudio.Tests/Ship/Stage5DocsConsistencyTests.cs`

**Step 1 — Write the failing test.** Some assertions may pass on first run if Tasks 38–42 were
done faithfully — that is the nature of a consistency gate, which exists to catch a *future* edit
that updates one doc and forgets its sibling:

```csharp
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Ship;

public class Stage5DocsConsistencyTests
{
    private const string ArtifactName = "claudio-macos-arm64.zip";

    [Fact]
    [Trait("Category", "Fast")]
    public void Every_doc_names_the_packaged_artifact_identically()
    {
        var readme = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, "README.md"));
        var claudeMd = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, "CLAUDE.md"));
        var decisions = File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, "DECISIONS.md"));

        Assert.Contains(ArtifactName, readme, StringComparison.Ordinal);
        Assert.Contains(ArtifactName, claudeMd, StringComparison.Ordinal);
        Assert.Contains(ArtifactName, decisions, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData("docs/plans/2026-07-11-v2-stage5-ux-packaging-design.md")]
    [InlineData("docs/plans/2026-07-11-v2-stage5-manual-acceptance.md")]
    public void Doc_links_this_pass_adds_resolve_to_a_real_file(string relativePath)
    {
        var fullPath = Path.Combine(RepoPaths.RepoRoot, relativePath);
        Assert.True(File.Exists(fullPath), $"linked doc missing on disk: {relativePath}");
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Manual_acceptance_checklist_covers_both_surfaces_and_r11_2()
    {
        var checklist = File.ReadAllText(Path.Combine(
            RepoPaths.RepoRoot, "docs/plans/2026-07-11-v2-stage5-manual-acceptance.md"));

        Assert.Contains("claudio-macos-arm64.zip", checklist, StringComparison.Ordinal);
        Assert.Contains("VU meter", checklist, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("R11.2", checklist, StringComparison.Ordinal);
        Assert.Contains("MuseScore", checklist, StringComparison.Ordinal);
    }
}
```

**Step 2 — Run to verify it fails (or passes as a self-check):**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Ship.Stage5DocsConsistencyTests"
```

Run this task **after** Tasks 38–42 to get the intended signal: a genuine red means one of the
prior tasks' edits did not actually land, or a doc drifted — not a fresh failure.

**Step 3 — No production/doc changes expected** unless red — if red, go back and fix whichever
prior task's file is missing the token; do not weaken this test.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Ship.Stage5DocsConsistencyTests"
```

Expected PASS: artifact name matches verbatim in all three docs; both linked docs exist; the
checklist covers both surfaces and R11.2.

**Step 5 — Run the full fast suite, then commit:**

```bash
dotnet test --filter Category=Fast
dotnet format --verify-no-changes
```

```bash
but status -fv
but commit stage5-area-e-docs -m "test(ship): cross-document consistency gate for the v2 Stage 5 docs pass" --changes <ids> --status-after
```

---

## Stage 5 acceptance gates

Stage 5 is not "done" on green CI alone. These gates are human, run against the real artifact, and
are recorded in `docs/plans/2026-07-11-v2-stage5-manual-acceptance.md` (Task 42):

1. **The manual macOS packaged-binary run.** `scripts/package-macos.sh` (Task 36) produces
   `claudio-macos-arm64.zip`; unzipped somewhere outside the repo on a real Mac with no .NET SDK on
   `PATH`, every command (`--version`, `--help`, `transcribe` default/`--mono`/`--model transkun`,
   `listen`, `play`, `render`, `evaluate`) runs successfully with models/SoundFont/`wwwroot/`
   resolved from the shipped `fixtures/` directory (S5.7, S5.8, S5.9).
2. **The four live-view browser checks**, exercised against a real `listen --view` session in an
   actual browser (S5.10–S5.12):
   - VU meter + device name track live mic input; idle/recording state is visually unambiguous.
   - A finished take plays back in-page via `<audio>` without a separate download step.
   - The finished take's four files (`raw.mid`, `score.mid`, `score.musicxml`, `recreation.wav`)
     are each one click away.
   - The page is dark-mode-aware, responsive at a phone-sized viewport, and visually coherent —
     and `--view` failing to start still leaves plain `listen` fully working.
3. **R11.2 carried forward** — the MuseScore GUI load check (open since Step 11; not closed by
   Stage 5, only carried forward): `notate --triplets --note-names` and a plain `transcribe`'s
   `score.musicxml` both load cleanly in MuseScore (staves, clefs, key signature, dynamics, pedal
   lines, triplet brackets). Record the pass in `DECISIONS.md` under "R11.2" once done.

Sign-off happens in the checklist file itself (Task 42), not by inference from `dotnet test`.

---

## Requirement coverage

| Requirement | Task(s) | Proven by |
|---|---|---|
| S5.1 — declare-once commands; help generated, not duplicated | 2, 7, 8, 9, 13, 22, 23 | `CliCommandBuilderTests`, `HelpRendererTopLevelTests`, `HelpRendererCommandTests`, `CommandLineAppDispatchTests`, `AppBuilderTests`, `ErrorPathTests`, `HelpGoldenTests` |
| S5.2 — unknown command/flag suggests nearest match, non-zero exit | 4, 5, 9, 10, 22 | `LevenshteinTests`, `SuggestTests`, `CommandLineAppDispatchTests`, `CommandLineAppSuggestionTests`, `ErrorPathTests` |
| S5.3 — malformed/out-of-range value names flag, kind, offending token | 1, 9, 11, 18, 22 | `OptionAndArgumentTests`, `CommandLineAppValidationTests`, `ParsedArgsAdaptersTests`, `ErrorPathTests` |
| S5.4 — `--help`, `<cmd> --help`, `--version` produce clean output | 7, 8, 9, 23 | `HelpRendererTopLevelTests`, `HelpRendererCommandTests`, `CommandLineAppDispatchTests`, `HelpGoldenTests` |
| S5.5 — top-level try/catch + `--debug`; no user-reachable stack trace | 14, 22 | `TopLevelErrorBoundaryTests`, `ErrorPathTests` |
| S5.6 — color only on an interactive terminal, honors `NO_COLOR`/`--no-color` | 6, 9, 12 | `AnsiStylerTests`, `CommandLineAppColorTests` |
| S5.7 — `package-macos.sh` produces a self-contained, prerequisite-free zip | 34, 36 | `PackagingTests`, `PackagingScriptsTests` (manual corroboration: Stage 5 acceptance gate 1) |
| S5.8 — packaged binary resolves models/SoundFont/`wwwroot/` from shipped assets | 34, 35, 36, 37 | `PackagingScriptsTests`, `CiWorkflowTests` (manual corroboration: gate 1) |
| S5.9 — smoke test exercises `--version` + a fixture `transcribe`/`render` | 35, 36, 37 | `PackagingScriptsTests`, `CiWorkflowTests` |
| S5.10 — VU meter, device name, recording/idle state | 25, 26, 27, 28, 30 | `LiveNotationServerTests`, `PortAudioAudioSourceTests`, `AudioLevelTests`, `LevelTeeingAudioSourceTests` (manual corroboration: gate 2) |
| S5.11 — playable + downloadable finished take | 29, 30, 32, 33 | `LiveNotationServerTakeFilesTests`, `LiveViewPolishAssetTests` (manual corroboration: gate 2) |
| S5.12 — styled/responsive/dark-mode page; `--view` failure degrades safely | 31, 32, 33 | `LiveViewPolishAssetTests`, `WebAssetContentTests`, `OsmdAssetTests` (manual corroboration: gate 2) |
| R11.2 (carried forward, not closed) — MuseScore GUI load check | 42 | `ManualAcceptanceChecklistTests`, `Stage5DocsConsistencyTests` (human gate 3) |
