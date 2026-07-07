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
| `MeltySynth` | `AudioClaudio.Infrastructure` | `dotnet add package MeltySynth --version 2.4.1` | MIT | 2026-07-07 |
| `PortAudioSharp2` (+ bundled native `org.k2fsa.portaudio.runtime.*`) | `AudioClaudio.Infrastructure` | `dotnet add package PortAudioSharp2 --version 1.0.6` | Apache-2.0 | 2026-07-07 |

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

## Step 8 — Synthesis and playback

### NuGet: MeltySynth
- Version: **2.4.1** — latest stable at execution time (confirmed via the NuGet
  v3 flat-container index and the `sinshu/meltysynth` tag list; matches the
  plan's pinned version exactly). Pure C#, no native deps, targets
  `.NETStandard2.1`.
- License: **MIT**. The package's nuspec declares `<license type="file">
  LICENSE.txt</license>`; GitHub's own license detector reports "Other/
  NOASSERTION" for the repo because that file bundles MIT-license attribution
  for two inherited components (C# Synth by Alex Veltsistas; TinySoundFont by
  Bernhard Schelling, itself based on SFZero) ahead of MeltySynth's own MIT
  grant (Copyright 2021 Nobuaki Tanaka) — the auto-detector chokes on the
  multi-attribution preamble, but the operative text (fetched and read
  directly from `raw.githubusercontent.com/sinshu/meltysynth/main/LICENSE.txt`)
  is the standard MIT permission grant. Permissive, non-copyleft; compatible
  with UNLICENSE (§1 rule 7).
- Role: SoundFont synthesis engine behind `MeltySynthSynthesizer : ISynthesizer`
  (`AudioClaudio.Infrastructure.Synthesis`). Verified API surface against the
  installed `v2.4.1` source (not just the plan's illustrative sketch):
  `SoundFont(string path)`, `SynthesizerSettings(int sampleRate) { EnableReverbAndChorus }`,
  `Synthesizer(SoundFont, SynthesizerSettings)`, `ProcessMidiMessage(channel, 0xC0, program, 0)`
  for program change, `NoteOn/NoteOff(channel, key[, velocity])`, and
  `Render(Span<float> left, Span<float> right)` — the latter tiles internally
  across MeltySynth's own block size regardless of the requested span length,
  and carries voice/block state across repeated calls on the same
  `Synthesizer` instance, which is exactly what the sample-accurate scheduler
  in `MeltySynthSynthesizer.Render` relies on (render up to the next event,
  apply NoteOn/NoteOff, repeat).

### SoundFont: GeneralUser GS (Cornelius's decision — committed, not synthesized)
- **Version 2.0.3** (per the file's own license banner and the source repo's
  commit message "Update SoundFont to v2.0.3 and documentation to r5",
  2026-02-23, S. Christian Collins).
- **Source URL (downloaded from):**
  `https://raw.githubusercontent.com/mrbumpy409/GeneralUser-GS/main/GeneralUser-GS.sf2`
  — the GitHub mirror named in the plan (`mrbumpy409/GeneralUser-GS`), which
  hosts the `.sf2` directly (no GitHub Release asset exists for this repo — a
  `.../releases/latest` lookup 404s — so the raw file on `main` is the
  reliable source, confirmed reachable and content-addressed via the GitHub
  Contents API before download).
- **File:** `fixtures/soundfont/GeneralUser-GS.sf2`
  **Size:** 32,319,396 bytes (~30.8 MiB), matching the GitHub Contents API's
  reported size exactly (byte-for-byte download, no truncation).
  **SHA-256:** `9575028c7a1f589f5770fccc8cff2734566af40cd26ed836944e9a5152688cfe`
  Sanity-checked as a real SoundFont2 file: starts `52 49 46 46 ... 73 66 62
  6B` (`RIFF`...`sfbk`); loads via `new MeltySynth.SoundFont(path)` with a
  non-empty `Presets` list including a bank-0/patch-0 (Acoustic Grand Piano)
  preset (`SoundFontFixtureTests`).
- **License:** committed at `fixtures/soundfont/LICENSE-GeneralUserGS.txt`
  (fetched from the same repo's `documentation/LICENSE.txt`, "GeneralUser GS
  License v2.0"). Operative grant, quoted: *"You may use GeneralUser GS
  without restriction for your own music creation, private or commercial...
  Please feel free to use it in your software projects, and to modify the
  SoundFont bank or its packaging to suit your needs."* Freely redistributable
  and modifiable, no royalty, no copyleft, no share-alike — a permissive data
  license consistent with §1 rule 7's spirit (the rule's MIT/Apache-2.0/BSD/
  MPL-2.0 list is written for *code* dependencies; this is a data fixture, as
  the plan itself notes). The one restriction stated is etiquette, not a
  license term: don't hot-link the download from a third-party website — this
  repository commits the bytes locally, so that request is moot.
- Network access, download, and restore all succeeded on the first attempt;
  no fallback or synthesized substitute was needed.

### Golden strategy: tolerance-based comparison, not byte-exact SHA-256 (Cornelius's decision)
- **Why not a hash:** MeltySynth's stereo mixdown (`ArrayMath.MultiplyAdd`,
  read directly from the installed `v2.4.1` source) is vectorized via
  `System.Numerics.Vector<float>`, whose SIMD lane count is
  architecture-dependent (e.g. 4 lanes on ARM64/NEON vs. potentially 8 on
  x64/AVX2). Floating-point addition is not associative, so a different lane
  count changes the accumulation order and can shift low-order bits of the
  mixed result. This is a more concrete mechanism than the plan's original
  assumption ("MeltySynth avoids FMA... should be bit-identical across
  x64/arm64") — FMA is indeed avoided, but SIMD-width-driven reordering is a
  separate, real source of cross-architecture drift, which is exactly why a
  hash pinned on one CI runner's architecture is fragile across others.
- **What's committed instead:** a reference render,
  `fixtures/golden/two-bar.wav` (16-bit PCM, via the production
  `WavFileWriter`), blessed once (`AUDIO_CLAUDIO_BLESS=1`) from the in-code
  `TwoBarMelody` fixture and reviewed. `GoldenRenderTests` compares a fresh
  render against it via `WavGoldenComparer` (`tests/.../TestSupport/WavGoldenComparer.cs`).
- **Tolerance chosen:** **max absolute per-sample difference ≤ 1e-3**
  (normalized to the [-1, 1] float domain; ≈ -60 dBFS, ≈ 33 int16 codes out of
  32767). Reasoning: single-ULP-scale float differences from SIMD-width
  reordering, accumulated over a modest number of per-block mix operations,
  are expected to land many orders of magnitude below this; a genuine
  synthesis regression (wrong note, wrong onset/duration, wrong envelope)
  instead produces a materially different waveform — a max difference on the
  order of the signal's own amplitude (thousands to tens of thousands of
  int16 codes), not a handful. **This machine is ARM64 (macOS); the threshold
  has been exercised only against itself (max difference = 0, since the
  fixture was blessed on this same machine/build) — it has NOT yet been
  validated against an actual x64 run. The first CI run on the x64 runner is
  the real cross-architecture check for this threshold; if it ever needs
  widening, that's a reviewed change to this document plus the fixture, not a
  silent regeneration (§5 fixture policy).**
- Kept alongside the tolerance comparison (per the plan's "also keep the
  structural checks"): total-length, silence/energy, and a cross-platform-
  robust spectral sanity check (FFT peak-bin frequency within a documented
  cents tolerance of the note's true pitch) — these do not depend on
  bit-level reproducibility at all.
- Determinism itself (R8.2, same-machine same-build) remains an exact,
  unconditional bit-for-bit property, proved separately by
  `SynthesisDeterminismTests` (CsCheck property, seed `0009IwXOILX3`, 50
  iterations x up to 6 notes each) — the guard was verified for real: pointing
  the adapter at a single reused `Synthesizer` instance across calls (instead
  of one per `Render`) made this property fail immediately, confirming it
  actually detects the nondeterminism it exists to catch.

### `TwoBarMelody` fixture: note length chosen for an exact MIDI round trip
- The Task 5/6 golden and the CLI-over-MIDI test (`RenderCommandTests`) share
  one committed reference (`fixtures/golden/two-bar.wav`) and one tolerance.
  For that to be valid, the notes reconstructed from the committed
  `fixtures/golden/two-bar.mid` (Step 7's 480-ticks-per-quarter writer/reader)
  must reproduce the original `TwoBarMelody` notes' onsets **and durations**
  exactly in the sample domain — not just approximately within "R7.2's
  one-tick bound."
- At 44.1 kHz / 120 BPM / 480 PPQN, one MIDI tick is 735/16 samples (not an
  integer), so a sample count round-trips losslessly through ticks iff it is
  a multiple of 735. The melody's step (22050 = 735·30) already satisfies
  this; the note length was chosen as **19845 = 735·27** samples (≈450 ms,
  leaving a ≈50 ms/2205-sample gap before the next onset — "detached," per the
  plan's intent) specifically so it does too. Verified both by hand
  (`SamplesToTicks(19845, ...) = 432` exactly, `TicksToSamples(432, ...) =
  19845` exactly) and empirically: the CLI's real `render` command over the
  committed `.mid` produces a WAV with **SHA-256 identical** to the golden
  (`b25888fc8f43dd40cb79e7b3c3838eccb1a4744aa8066c8511783157d322b7c9`), not
  merely "within tolerance" of it.
- This is a deliberate deviation from the plan's illustrative `noteLen =
  20000`, which does not round-trip exactly at this PPQN/tempo (its ticks
  value is a repeating fraction, ~435.43, so the reconstructed duration would
  be off by ~17 samples). `TwoBarMelody` is a test fixture defined fresh for
  this step (not a `CONTRACTS.md`-pinned shape), so this is a values-only
  adjustment, not a contract change.

### Two deliberate deviations from the written Task 1/5 plan text
- **No `TestPaths` locator.** The plan's Task 1 sketches a new
  `tests/AudioClaudio.Tests/TestSupport/TestPaths.cs`. `CONTRACTS.md` §0
  already designates `RepoPaths` (Step 0) as "the ONE repo-root/fixture
  locator" and explicitly names Steps 8/9/11/12 as the ones that must route
  through it rather than add a parallel walk-up — and `RepoPaths` already
  exposed `SoundFontPath`/`GoldenDirectory`. So this step used `RepoPaths`
  throughout and did not create `TestPaths`.
- **No `two-bar.wav.sha256` hash file.** The plan's Task 5 pins a byte-exact
  SHA-256 in a committed `.sha256` text file. Per instruction for this
  execution, the golden is instead a committed reference WAV
  (`fixtures/golden/two-bar.wav`) compared via a documented tolerance (see
  above) — cross-architecture-robust by construction, at the cost of not
  being a literal one-line hash-equality check.

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
