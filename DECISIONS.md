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
| `Melanchall.DryWetMidi` | `AudioClaudio.Infrastructure` | `dotnet add package Melanchall.DryWetMidi --version 8.0.3` | MIT | 2026-07-07 |

## Design decisions

**Step 2 — frame delivery model: pull** (`IEnumerable<Frame> Frames { get; }`).
Consumer-driven; clean for files/tests (`source.Frames.ToList()`); no threads,
no callbacks, deterministic ordering by construction. The live mic (Step 10)
bridges the device callback into this via a bounded `Channel<Frame>` rather
than the port being pushed to directly. Resolved by Cornelius 2026-07-07.

**Step 2 — PCM quantisation convention** (implementation note, not a design
fork): the WAV writer/reader pair is made an exact inverse pair on the
quantisation grid rather than approximately equal. Writer: `q = round(x *
2^(bits-1))`, clamped to the signed range (`2^15` for 16-bit, `2^23` for
24-bit); reader: `x' = q / 2^(bits-1)`. Because both sides share the same
scale and rounding rule, `read(write(x')) == x'` bit-for-bit for any
already-quantised `x'`, which is what makes the round-trip tests exact rather
than tolerance-based.

**Step 3 — FFT: hand-rolled radix-2** (`Radix2Fft` in `AudioClaudio.Domain`),
dependency-free. Zero dependencies; keeps `AudioClaudio.Domain` strictly
BCL-only (R0.2); full `double` precision. Correctness is guarded by two
independent checks: Parseval's theorem (time/frequency energy conservation)
and a bin-by-bin cross-check against a naive O(N²) direct DFT reference
(computed only in the test project, never shipped). Alternative rejected:
NWaves (MIT) — deferred; if adopted for Phase 2 resampling, its adapter goes
in `AudioClaudio.Infrastructure`, never Domain, per R0.2. No new NuGet
package, so no license row above. Resolved by Cornelius 2026-07-07.

**Step 6 — "ties beyond MVP" (R6.1): KEEP structural bar-splitting.**
`ScoreElement.TiedToNext` cuts a note at a barline (a notation necessity +
bar-conservation R6.4); "beyond MVP" excludes only spelling a note's snapped
duration as a tie-chain of standard values (Step 6 stores integer
`LengthTicks`; Step 11 spells glyphs). Resolved by Cornelius 2026-07-07.

## Step 7 — MIDI export

### NuGet: Melanchall.DryWetMidi
- Version: **8.0.3** — the plan drafted this step against "latest 7.x"
  (7.2.0 was current then); by execution two days later 8.0.3 is the latest
  stable release. The 7→8 major bump's changelog is scoped to the
  `Multimedia`/`Playback` namespaces (live-playback data tracking); the
  `Core`/`Interaction` serialization API this adapter uses (`MidiFile`,
  `TrackChunk`, `Note`, `TimedEvent`, `SetTempoEvent`, `TimeSignatureEvent`,
  `TicksPerQuarterNoteTimeDivision`, `GetNotes()`, `ToTrackChunk()`) is
  unaffected, so the newer stable version was pinned instead of the
  now-stale 7.x line. Confirmed via `dotnet add package` restore (network
  reachable) and via nuspec inspection.
- License: **MIT** (`<license type="expression">MIT</license>` in the
  package's nuspec) — compatible with UNLICENSE (Section 1 rule 7); no
  copyleft in the graph.
- Role: standard MIDI file read/write, used only by the Infrastructure MIDI
  adapter (`AudioClaudio.Infrastructure.Midi`). Never referenced from Domain
  or Application; `DependencyRuleTests`/`ProjectReferenceGraphTests` guard this.

### Tick resolution (PPQN) — R7.2
- Chosen: **480 ticks per quarter note.**
- Rationale: 480 = 2^5·3·5 makes every MVP grid value land on an integer tick —
  quarter 480, eighth 240, sixteenth 120, dotted 720/360/180 — so a quantized
  `Score` serializes to MIDI *exactly*. Raw (unquantized) `NoteEvent` samples
  round only once, at the sample→tick edge (`SamplesToTicks`/`TicksToSamples`),
  bounded by one tick each way — that is the stated R7.2 tolerance for the
  performance path. Also the DAW-standard PPQN.

### Implementation note: `NoteEvent`/`Tempo`/`TimeSignature` name collisions
- DryWetMIDI's public API happens to declare its own `Melanchall.DryWetMidi.
  Core.NoteEvent` and `Melanchall.DryWetMidi.Interaction.Tempo`/`TimeSignature`
  types (confirmed by reflecting the installed assembly — no other Domain
  type name used by this step collides: `Pitch`, `SampleRate`,
  `SamplePosition`, `SampleDuration`, `Score`, `Measure`, `ScoreElement`,
  `Subdivision` are all clear). Every file that imports both
  `AudioClaudio.Domain` and the colliding `Melanchall.DryWetMidi.*` namespaces
  disambiguates with local `using NoteEvent = AudioClaudio.Domain.NoteEvent;`
  / `using Tempo = AudioClaudio.Domain.Tempo;` / `using TimeSignature =
  AudioClaudio.Domain.TimeSignature;` alias directives rather than fully
  qualifying every use — an implementation detail, not an API or contract change.

### 6th Application port: `INoteEventWriter`
- `INoteEventWriter` (write a raw `[NoteEvent]` performance) joins
  `IScoreWriter` as a deliberate 6th Application port beyond the
  constitution's five illustrative ports, justified by R7.1's requirement to
  write **both** a `Score` and a raw event list. Already canonicalized in
  `docs/plans/CONTRACTS.md` §7; recorded here for visibility per the plan's
  request to flag it. No action needed from Cornelius unless the port shape
  should change.

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
