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

*(PortAudioSharp2 was first added in Step 8 for `PortAudioPlayer` output; Step 10 reuses the
same package for `PortAudioAudioSource` microphone input — no new dependency.)*

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

## Step 9 — The closed loop

No new NuGet package. **One design decision, made by Cornelius:** the closed-loop
corpus is constrained to physically-audible note durations so the closed loop is
a real, strict property — every generated case must recover within strict R9.2
(count exact, pitch exact, onset ±1 subdivision, duration ±1 subdivision). The
rest of this section records how that cap is derived, the offset-detection design
it pairs with, and the honest coverage it achieves.

### The decision: constrain the corpus to physically-audible durations

The transcriber cannot recover a sustain the audio does not contain. A piano
note rendered by the committed SoundFont decays to inaudibility in a
**pitch-dependent** time; declaring a note longer than that and then demanding
its duration back is an unfair test, not a real bug. So `ClosedLoopGen.Cases`
keeps all of R9.1 intact (MIDI 33–96, notes ≥ eighth, 60–140 BPM, ≥1 grid rest)
and adds ONE cap: a note's declared duration must stay within the pitch's
audible window at that case's tempo. **R9.2's tolerances are not touched** —
count/pitch/onset/duration are all still asserted strictly
(`ClosedLoopComparerTests`), and the min-eighth / MIDI-range / rest / tempo
constraints all still hold (`GeneratorConstraintTests`, `CorpusCoverageTests`).

### Deriving the pitch→max-duration cap

`T_audible(p)` (seconds) is measured once against the committed SoundFont: render
a note of pitch `p` held for 3 s, and find when its per-frame RMS first falls to
`OffsetReleaseRatio` (0.50) of the reference level sampled `OffsetSettleFrames`
(5) frames after onset — the **same threshold and reference the offset detector
uses**, so the cap is self-consistent with detection. The 64-value table
(`ClosedLoopGen.AudibleSeconds`, MIDI 33–96) is committed; a note declared ≤
`margin · T_audible(p)` is thus guaranteed above threshold at its note-off.

The cap in subdivisions: one sixteenth is `15/bpm` seconds, so
`maxDurSub(p, bpm) = floor(margin · T_audible(p) · bpm / 15)` with
`margin = 0.85` (headroom so the held note clears the threshold at note-off, not
right at it). A `(pitch, bpm)` pair is offered only when `maxDurSub ≥ 2` (an
audible eighth); the generator draws each note's pitch from
`ValidPitches(bpm)` and its duration from the capped standard values, so
generation is total (no rejection sampling → deterministic for the pinned-seed
push path). `margin`, `OffsetSettleFrames`, and `OffsetReleaseRatio` were chosen
together by a joint sweep of the real, fully-wired pipeline against several
hundred SoundFont-rendered notes spanning the whole R9.1 corpus.

### The offset detector these pair with (`TranscriptionPipeline.RefineOffsets`)

Step 5's `NoteSegmenterOptions.DecayFloorRatio` (a floor relative to a note's
*attack peak*) cannot work across the keyboard: a note's peak-to-sustain ratio
is heavily pitch-dependent, so no single peak-relative floor separates "still
sustaining" from "released" for both a bass note and a treble note. It stays
disabled (`DecayFloorRatio = 0`); the Application-layer `RefineOffsets`
recomputes each note's duration from energy instead, and three findings shaped
it (each verified by inspecting the actual RMS envelope of a failing case, not
guessed):

1. **Early reference.** `OffsetSettleFrames = 5` (~58 ms), not 20. At 20 frames
   (232 ms) the reference for a short eighth-note fell almost AT its note-off —
   deep in the decay — driving the threshold absurdly low and the measured
   duration grossly long. Sampling early (just past the attack transient)
   captures the note's true loud level.
2. **Release ratio 0.50.** A low note's damped release after note-off is
   *gradual*; at a low threshold (0.28) the release takes ~1.7 subdivisions to
   cross it → overshoot. 0.50 is crossed promptly after note-off (a low note
   barely decays while held, so the higher threshold causes no undershoot; the
   cap, derived at the same 0.50, guarantees it).
3. **Energy is the duration authority, bounded by the next onset;
   `OffsetPersistFrames = 3` debounce.** Two low notes close in pitch overlap
   acoustically (a bass note rings on through the rest into the next), and their
   partials BEAT — wobbling the YIN pitch track. Step 5's `NoteSegmenter` ends a
   note on any pitch change, so a momentary wobble truncates it in the *domain*.
   `RefineOffsets` therefore recomputes duration straight from the energy
   envelope, bounded by the next note's onset (overriding the segmenter's
   possibly-truncated duration), with a short persistence debounce to reject the
   beat dips. It sets only duration — never pitch, onset, or count — so it cannot
   affect those three. Domain `NoteSegmenter` is unchanged.

### Coverage: two corpora

The 0.50 audible threshold makes the top of the keyboard decay fast: at the
fastest tempo (140 BPM) an audible eighth requires `T_audible ≥ 0.25 s`, which
MIDI 72 (C5) and above do not meet — the top two octaves cannot sustain an
audible eighth at ANY tempo in range. So the capped `Cases` corpus spans **MIDI
33–71** (39 pitches; the exact audible band is pinned by `CorpusCoverageTests`,
and it narrows further at slower tempos). Because a purely tonal high piano note
IS still briefly present (it is note-ON long enough to detect an attack and a
stable pitch — only its *duration* is unrecoverable), a second corpus
`ClosedLoopGen.FullRangeCases` (uncapped, full MIDI 33–96) is checked for **count,
pitch, and onset only** (`ClosedLoop.RunFullRangeCase`). Offset refinement
changes only durations, so this is a genuine end-to-end proof of the rest of the
pipeline across the whole keyboard.

**Together the two closed-loop tests establish: count/pitch/onset proven across
the full MIDI 33–96, and duration proven (with count/pitch/onset) within the
audible window.**

### Honest result — and a rare pre-existing residual that is NOT duration

**The duration decision worked completely.** Across every validation batch — the
joint parameter sweeps, a 500-case run, and the pinned 40-case push windows —
**zero duration failures** were observed on the audible-capped `Cases` corpus.
The constrained corpus recovers duration within R9.2's ±1 subdivision; the whole
point of the cap (no longer asking for a sustain the audio lacks) holds. No R9.1
constraint was narrowed and no R9.2 tolerance was weakened to reach this.

The push suite runs `ClosedLoopPropertyTests.PushCaseCount` (40) cases of EACH
corpus at pinned seeds consumed via the direct `Gen.Generate` loop (reproducible
— see the note below), and both are verified clean (40/40, all four criteria for
`Cases`; count/pitch/onset for `FullRangeCases`), re-checked across fresh
processes.

**A rare residual remains, and it is honestly NOT a duration or corpus problem.**
A 500-case exploratory run surfaced exactly two failures, both **pre-existing
Step 4/5 limitations on real piano audio, unrelated to this step's decision**:

- one **YIN octave error** (a mid note, MIDI 59, detected one octave low as 47).
  YIN can occasionally lock onto a sub-harmonic on the SoundFont's harmonic
  content; the error is non-monotonic in `YinThreshold` (0.12/0.18/0.20 get that
  case right, 0.10/0.15 do not), so it is not a clean single-knob fix and would
  be domain-scope (Step 4) to harden.
- one **dropped note** (a case transcribed 6 notes where 7 were played — an onset
  the detector missed).

Both are rare (~0.4% of cases in that run) and are **pitch/onset/count**, never
duration — `RefineOffsets` sets only duration, so it cannot cause them, and the
audible cap has nothing to do with them. They are the designed job of the nightly
workflow (`CLOSED_LOOP_CASES` large, unpinned) to discover and quarantine (R9.3);
the pinned push windows avoid them not by cherry-picking around a common failure
but because the failures are genuinely rare (a random 40-case window is clean
~85–90% of the time). Hardening YIN's octave selection and onset recall on real
piano audio is recorded as Phase-2 work; it is orthogonal to the audible-duration
decision this step implements.

**Physical limitation to record in the README (Step 12):** with a monophonic,
energy-based offset detector and this SoundFont, a note's *duration* is only
recoverable while the note stays audibly above the release threshold — roughly
MIDI 33–71 for an eighth note or longer. The very top of the keyboard (≈ C5–C7)
decays too fast to sustain an audible eighth; those pitches' onset and pitch are
still transcribed correctly, but their notated duration is not a claim the audio
can support. Widening this is Phase-2 work (a sturdier note-off model, likely
alongside the polyphonic ONNX detector).

### Implementation note: `Cases.Sample(..., seed:)` alone does not reproduce

While pinning the push seeds, an explicit seed string that verified cleanly
(`Cases.Sample(run, iter: N, seed: "...")` returned without throwing)
**failed on the very next, independent process run of the exact same call** —
with a *different* internally-reported reproduction seed each time. Forcing
`threads: 1` did not fix it either. The cause: `Sample` evaluates iterations
across a thread pool by default, and which of the N generated cases lands in
which evaluation slot (and therefore which one a shrink converges to and
reports) depends on run-to-run thread-scheduling timing, not purely the seed
string.

A direct `PCG.Parse(seed)` + `Gen<T>.Generate(pcg, null, out _)` loop (running
each generated case directly, bypassing `Sample` entirely) was verified to
reproduce an *identical* case sequence across independent process runs (checked
3+ times). `ClosedLoopPropertyTests`'s default (unoverridden) push path uses
this direct loop, not `Sample`, for exactly this reason — for BOTH the strict
capped corpus and the full-range count/pitch/onset corpus. The
`CLOSED_LOOP_CASES`-overridden (exploratory/nightly) path still uses `Sample`
deliberately — reproducibility does not matter there (R9.3's discovery intent),
and `Sample`'s shrinking is a genuine benefit for producing a smaller,
easier-to-triage failing case to quarantine.

## Step 10 — Live microphone capture

No new NuGet package (`PortAudioSharp2` was already added in Step 8 for output;
Step 10 reuses it for input — license row above updated to note both roles).

### Frame delivery: the Step 2 pull + bounded `Channel<Frame>` bridge, realized

The Step 2 design decision (pull `IEnumerable<Frame> Frames`) explicitly deferred
the live-mic mechanism to "a bounded `Channel<Frame>` rather than the port being
pushed to directly." Step 10 implements exactly that, split so the risky part is
tiny and the device-free part is fully testable:

- `FrameAccumulator` (Infrastructure/Capture) — pure reframing: mono samples in
  arbitrary-sized pushes out as complete overlapping frames of size N at hop H,
  each stamped with a running `SamplePosition` (a **sample count from the stream**,
  never a clock read — non-negotiable 2). `Flush` emits the trailing zero-padded
  frame(s) at end-of-stream.
- `CaptureFrameStream` (Infrastructure/Capture) — downmix (interleaved→mono mean)
  + the bounded-channel bridge exposed as the `IAudioSource.Frames` pull property.
  The real-time producer calls `Submit` only (non-blocking `TryWrite`); a full
  buffer is **counted** (`DroppedFrameCount`), never silently swallowed.
- `PortAudioAudioSource` (Infrastructure/Audio) — the ONLY device code: it opens
  the PortAudio input stream lazily in `Start()` and marshals the callback's
  `IntPtr` block into `CaptureFrameStream.Submit`. Construction is device-free, so
  the whole downmix/reframe/bridge path is exercised in CI through the internal
  `OnAudioBlock` seam; `Start()`/the native callback are covered by manual
  acceptance only (no audio device exists in CI/sandbox).

**Frame-tail contract (R10.1).** The live path emits the SAME frames as
`WavAudioSource` for the same audio, byte-for-byte — including the end-of-stream
convention: `Framing.Split` (Step 2) zero-pads the final partial frame rather than
dropping it (`ceil(length/hop)` frames), so `CaptureFrameStream.Complete()` flushes
a matching zero-padded tail. This is proven device-free by
`CaptureWavEquivalenceTests` (frames identical; full transcription identical
through both sources). *(The Step 10 plan's illustrative `FrameAccumulator`
pseudocode dropped the tail; it was corrected to the real Step 2 contract per
CONTRACTS §2 and §1 rule 8 — fix the code to the pinned invariant.)*

### `BoundedChannelFullMode.Wait` is load-bearing, not decorative

`CaptureFrameStream` only ever calls the synchronous `TryWrite` (the audio thread
must never block), so it is tempting to think `FullMode` is dead. It is not:
verified empirically (a throwaway `BoundedChannel<int>` probe) that `FullMode`
governs `TryWrite` too. Under `Wait`, `TryWrite` returns `false` on a full channel
and leaves it untouched — which is exactly what lets us detect and **count** the
overflow. Under `DropNewest`/`DropOldest`/`DropWrite`, `TryWrite` always returns
`true` and the channel silently evicts a frame internally, pinning
`DroppedFrameCount` at zero forever — the "swallow the error" failure the
constitution's Error-Handling rule forbids. So `Wait` is chosen deliberately.

### Live `StreamNotes` is genuinely incremental (not a batch alias)

`TranscriptionPipeline.StreamNotes` was a Step 9 stopgap (`=> Transcribe(source).
RawEvents`), which cannot "print detected notes as they occur" (R10.3): over a live
mic it would never return. Step 10 reimplements it as a lazy, causal iterator that
pulls frames one at a time and yields a note shortly after each onset:

- per frame: `SpectralFrontEnd` magnitude, one `YinPitchDetector` estimate, and the
  half-wave-rectified spectral flux vs. the previous frame (`SpectralFlux`'s
  definition, one pair);
- a **scale-invariant ratio** onset peak-picker (flux stands out from its local mean
  by the multiplier) with a running-max silence floor — the batch detector's
  global-max normalization is not available causally, and is actively wrong for a
  live stream: the first note (whole frame inside the note) makes one outsized flux
  spike that would bury every later mid-frame onset;
- an onset→pitch **state machine**: a confirmed onset is only emitted once a single
  voiced pitch has SUSTAINED a few frames. This is load-bearing — YIN reads
  *unvoiced* on the partial attack frames (pitch locks a few frames late), and a
  note *offset* makes its own broadband flux spike (spectral leakage from truncating
  the tone) that would otherwise be a false onset but goes unvoiced immediately.
  Same principle as the batch segmenter's flicker floor (R5.3).

The live feed is a **low-latency preview**; the accurate output files come from the
batch `Transcribe` run on the buffered session audio on stop (`LiveTranscriptionSession`
tees frames to a buffer, then batch-transcribes it). The two paths agree on count,
pitch, and onset for clean signals but the live feed reports a *provisional*
duration (the batch owns note-off). *(This changed one Step 9 test,
`PipelineIntegrationTests`, which had asserted `StreamNotes == Transcribe.RawEvents`
byte-for-byte — an identity that only held because of the stopgap and is
incompatible with genuine incrementality; it now asserts agreement on count/pitch/
onset. CONTRACTS §9 always described `StreamNotes` as the incremental live feed.)*

### Latency (R10.2 — measured/documented, not asserted)

`LatencyBudget.WorstCaseAlgorithmicMs` computes the ONSET-detection latency
(`frameSize + lookahead·hop`): at the live defaults (44.1 kHz, N=1024, H=256,
lookahead=3) that is **~41 ms**, asserted < 150 ms by `LatencyBudgetTests`. The
NOTE-EMISSION latency additionally waits `minVoiced` hops for the pitch to sustain
(~5 frames ≈ 29 ms) and then the PortAudio input buffer + OS scheduling on top —
still comfortably inside the R10.2 ~150 ms budget, but the true end-to-end figure is
a hardware measurement (the loopback procedure in the README), not a promise.

### R10.3 MusicXML ordering tension — the injected-writer seam

R10.3 lists MusicXML among `listen`'s stop-outputs, but the MusicXML writer is a
Step 11 deliverable and §1 rule 3 forbids implementing a future step now. `ListenCommand`
takes an OPTIONAL `IScoreWriter? musicXmlWriter` that stays `null` until Step 11
registers `new MusicXmlScoreWriter()` in the composition root — at which point
`score.musicxml` is emitted with zero change to `listen`. The seam (and the "written
iff a writer is supplied" behavior) is proven now by `ListenCommandTests`; only the
adapter is deferred. `raw.mid` + `score.mid` are fully delivered in Step 10.
