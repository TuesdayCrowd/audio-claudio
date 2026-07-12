# v2 Stage 5 — UX, robustness, packaging (design)

> **Status:** design validated with Cornelius (2026-07-11), not yet implemented.
> Turns the deferred Stage 5 of the [v2 release workplan](2026-07-10-v2-release-workplan.md)
> into a concrete, staged deliverable. Its headline is **UX**: make `claudio` a
> tool a person installs and runs, not a `dotnet run` demo.

## Why this exists

v2.0.0 shipped three transcription engines with earned guarantees, but the tool
around them still speaks like a prototype. Its entire command surface is one
460-line `switch` in `Program.cs` with ad-hoc flag parsing; help is a six-line
wall of stderr, some lines over 700 characters wide; unknown flags pass silently;
a missing file or a bad `--tempo` throws a raw .NET stack trace; and you run it as
`dotnet run --project src/AudioClaudio.Cli -- …`. Stage 5 closes that gap.

## Decisions (locked with Cornelius, 2026-07-11)

| Decision | Choice | Rejected alternative |
|---|---|---|
| Scope | Terminal UX + live-view polish + packaging, as one Stage 5 | Any one surface alone |
| CLI parser | Hand-rolled declaration-driven kernel (no dependency) | System.CommandLine (still `3.0.0-preview`, API churn); Spectre.Console.Cli (heavy, opinionated look) |
| Distribution | Self-contained, no prerequisites; **app-folder zip** | `dotnet tool install` (SDK-only); framework-dependent (needs .NET installed); single-file exe (per-run extraction of ~200 MB models) |
| Platforms | macOS `osx-arm64` only this cycle | Windows — an **explicit deferred non-goal**, not an oversight |
| Live view | All four: input feedback, in-page playback, downloads, visual polish | — |
| Terminal color | Tasteful ANSI, auto-off when piped / `NO_COLOR` / `--no-color` | Plain-only; always-on color |

These carry into `DECISIONS.md` as one "v2 Stage 5" entry when implementation lands.

## The five work areas

### 1. The CLI kernel — one source of truth for parsing and help

A small hand-rolled mini-framework in the Cli project (`Cli/` folder: `Command`,
`Option`, `CommandRegistry`, `ParseResult`, `HelpRenderer`, `Ansi`). Each command
declares itself once — name, one-line summary, positional arguments, and options,
each option carrying its own description and value kind. **Help text, validation,
and the top-level usage are derived from those declarations**, so the help can
never again disagree with what the parser accepts.

`Program.cs` shrinks to build-registry → parse → dispatch. The per-command bodies
move into the handlers already sketched under `Commands/`.

The parser recognizes `--flag`, `--opt value`, and positionals; rejects an unknown
flag or command with a Levenshtein "did you mean…"; and validates each value's
kind (double, int, enum, path) before a handler ever runs.

**Requirements**
- **S5.1** Every command and option declares itself once; `--help` and usage are generated, not duplicated.
- **S5.2** An unknown command or flag fails with a nearest-match suggestion and a non-zero exit code.
- **S5.3** A malformed or out-of-range value fails with a sentence naming the flag, the expected kind, and the offending token.

### 2. Terminal UX — what the kernel produces

- `claudio` / `claudio --help` → a top-level page: the tool's one-line identity, then one grouped line per command.
- `claudio <cmd> --help` → per-command help: a usage line, each positional, every option with its description, and a worked example.
- `claudio --version` → version and build.
- **Errors read as sentences**, each ending with a pointer to `claudio <cmd> --help`:
  - `error: input file 'foo.wav' not found`
  - `error: --tempo expects a number in BPM, got 'abc'`
  - `error: unknown option '--modl'. Did you mean '--model'?`
- **Color** flows through an `Ansi` helper that no-ops when stdout/stderr is not a terminal, when `NO_COLOR` is set, or when `--no-color` is passed. Only headings, the error keyword, and the offending token take color.
- A top-level `try/catch` guarantees no path prints a raw stack trace; an unexpected failure reads `unexpected error: … (run with --debug for the stack)`.

**Requirements**
- **S5.4** `--help`, `<cmd> --help`, and `--version` each produce clean, complete output.
- **S5.5** No user-reachable input produces a stack trace; every failure is a sentence plus an exit code.
- **S5.6** Color appears only on an interactive terminal and honors `NO_COLOR` and `--no-color`.

### 3. Packaging — the self-contained `claudio`

`dotnet publish -r osx-arm64 -c Release --self-contained` bundles the .NET runtime
and the native ONNX/PortAudio libraries, so the user installs no .NET. Native AOT
stays off the table: ONNX Runtime and PortAudioSharp2 ship native libraries and use
patterns AOT does not handle.

The artifact is an **app-folder zip**, `claudio-macos-arm64.zip`, that unpacks to a
`claudio` executable beside a `runtime/` assets directory (the ONNX models, the
SoundFont, `wwwroot/`). The user unzips and runs `./claudio`. This avoids per-run
extraction and lets the existing asset locators (`ModelLocator`, `SoundFontLocator`,
`AppContext.BaseDirectory/wwwroot`) keep working unchanged.

`scripts/package-macos.sh` produces the zip. A smoke test runs `claudio --version`
and a fixture `transcribe` against the *packaged* output — not the dev tree.

**Requirements**
- **S5.7** `scripts/package-macos.sh` produces `claudio-macos-arm64.zip`; unzip-and-run needs no prerequisites.
- **S5.8** The packaged binary resolves its models, SoundFont, and `wwwroot/` from the shipped `runtime/` dir.
- **S5.9** A smoke test exercises `--version` and a `transcribe` from the packaged output.

### 4. Live-view polish — all four, on the existing SSE server

Everything rides on today's `LiveNotationServer` (`HttpListener` + SSE) and keeps
its guarantee: the view is optional and cannot break plain `listen`. The work adds
routes and page markup; it does not restructure the server.

- **Live input feedback:** publish per-frame RMS level and the PortAudio device name over SSE; the page shows a VU meter and a clear recording/idle state, so you know the mic is heard before you commit to a take.
- **In-page playback:** after a take, serve `recreation.wav` (and `input.wav`) over an HTTP route; the page gains an `<audio>` player.
- **One-click downloads:** routes serve the take's `raw.mid`, `score.mid`, `score.musicxml`, and `recreation.wav`; links appear when the take finishes.
- **Visual polish:** restyle the page we own around the vendored OSMD bundle — dark mode via `prefers-color-scheme`, a responsive layout, a header carrying the tool's identity, and clear states.

**Requirements**
- **S5.10** A VU meter and device name reflect live mic input; the recording/idle state is unambiguous.
- **S5.11** A finished take is playable in the page and its four files are downloadable from it.
- **S5.12** The page is styled, responsive, and dark-mode aware; `--view` failures still degrade to plain `listen` (unchanged guarantee).

### 5. Testing, honesty, and success criteria

- **Kernel unit tests:** parsing, Levenshtein suggestion, value validation, and a **golden help text per command** that pins help against silent drift.
- **Error-path tests:** each failure maps to its sentence and exit code.
- **Packaging smoke test:** CI runs on Linux, so it smoke-tests the *packaging mechanics* via a `linux-x64` self-contained build. The shipped `osx-arm64` artifact is built and acceptance-run on the Mac. State that gap plainly — CI does not validate the Mac binary.
- **Live-view tests:** the new server routes get integration tests; the browser experience is a documented manual-acceptance gate, as the original `--view` was.

**Open human gates (tracked, not pretended):**
- **R11.2** — the MuseScore GUI load check, still open, carried forward.
- **S5-accept** — a Stage 5 manual-acceptance checklist: the packaged `claudio` runs each command from the zip, and the four live-view items work in a browser.

**Success:** `claudio <cmd>` runs standalone from the unzipped folder on macOS; every
command has real `--help`; every error is a sentence; the live view carries input
feedback, in-page playback, downloads, and finished styling; `DECISIONS.md`, README,
and `CLAUDE.md` describe the `claudio` install-and-run story in place of `dotnet run`.

## Non-goals (YAGNI)

- **No Windows binary** this cycle (deferred, per the decision above).
- **No shell tab-completion** (a later increment if wanted).
- **No synthesized in-browser playback** — the page plays the already-rendered `recreation.wav`.
- **No interactive TUI.**

## Sequencing

Area 1 (kernel) is the foundation; Area 2 (terminal behaviors) builds directly on
it. Area 3 (packaging) depends on nothing but a working CLI and is independent of
Area 4 (live-view). A natural order: **1 → 2 → 4 → 3**, finishing with packaging so
the acceptance run exercises the polished tool.
