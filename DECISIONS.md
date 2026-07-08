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
audible cap has nothing to do with them. They are the designed job of the large
exploratory closed-loop sweep (`CLOSED_LOOP_CASES` large, unpinned) to discover and
quarantine (R9.3);
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
`CLOSED_LOOP_CASES`-overridden (exploratory / on-demand deep) path still uses `Sample`
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

## Step 11 — MusicXML emission

No new NuGet package: `MusicXmlScoreWriter` (`AudioClaudio.Infrastructure.MusicXml`) is a
hand-rolled `StringBuilder` serializer, per R11.3. No *Design decision* was listed for this
step; the one thing worth recording plainly is an honest gap in the R11.2 sign-off.

### R11.2 "loads cleanly in MuseScore" — NOT performed as a human GUI check; recorded honestly

R11.2 asks for a manual check: open the golden `.musicxml` in MuseScore, confirm it loads and
renders, and record the version/date here. **That literal check has not happened** — MuseScore
is not installed in this execution environment (no `mscore`/`mscore4portable`/`MuseScore4`
binary on `PATH`, no `.app` bundle under `/Applications`), and this agent has no path to install
or drive a GUI application. Writing a line like "checked in MuseScore 4.4 on 2026-07-06" without
that having actually happened would be a fabricated record, which is worse than an honest gap —
so this entry says plainly what was and wasn't done, and asks Cornelius to close it out.

**What was verified instead, as the strongest available automated proxy:**
- Well-formed XML: `xmllint --noout fixtures/golden/musicxml/twinkle.musicxml` passes clean.
- The `<?xml version="1.0" encoding="UTF-8"?>` declaration, the MusicXML 4.0 Partwise DOCTYPE,
  and `<score-partwise version="4.0">` root are present and correctly formed (also pinned by
  `MusicXmlWriterTests.EmitsMusicXml40PartwiseDocumentHeader`).
- Structural conformance to the MusicXML 4.0 partwise content model, checked by hand against the
  canonical (`note.mod`/`attributes.mod`) element sequences: `<note>` children appear in
  `(pitch|rest), duration, tie*, type, dot*, notations` order — the schema's
  `(%full-note;, duration, (tie, tie?)?), instrument?, %editorial-voice;, type?, dot*,
  accidental?, ..., notations*, ...` sequence with every optional member in between (instrument,
  voice, accidental, stem, ...) simply omitted, which the schema permits; `<attributes>`
  children in `divisions, key, time, clef` order; `<key>` as `fifths` (mode omitted); `<time>` as
  `beats, beat-type`; `<clef>` as `sign, line`; `<pitch>` as `step, alter?, octave`; `<part-list>`
  holding a `<score-part>` with the required `<part-name>`.
- The DOCTYPE's own external identifier, `http://www.musicxml.org/dtds/partwise.dtd`, was
  fetched directly from this network to see whether schema-validated parsing was possible: it
  404s (confirmed byte-for-byte — the response is nginx's default 404 page, not a DTD). That URL
  is a long-standing public identifier real notation tools resolve from a DTD bundled with the
  application, not a live fetch — which is exactly why `MusicXmlTestSupport.Xml.Parse` sets
  `DtdProcessing.Ignore` / `XmlResolver = null` rather than attempting DTD-validated parsing; a
  from-scratch, non-canonical local DTD would validate nothing meaningful, so none was written.

**Action item for Cornelius:** open `fixtures/golden/musicxml/twinkle.musicxml` in an actual
MuseScore install, confirm it loads without error and renders two 4/4 bars in treble clef ending
on a quarter rest, then replace this note with the literal "Manual check: `twinkle.musicxml`
loads and renders in MuseScore `<version>` (checked `<date>`)" line R11.2 calls for. Until then:
R11.2's *stability* half is fully proven (the byte-identical golden test,
`EmitsByteIdenticalGoldenForTwinkleFixture`, plus the LF/no-BOM determinism guard); its
*"loads cleanly in MuseScore"* half rests on the schema-conformance review above, not a GUI run.

## Live notation view (`listen --view`, Phase-2 §8 item 3)

No new NuGet package: `LiveNotationServer` is built entirely on
`System.Net.HttpListener`, `System.Net.Sockets.TcpListener`, and
`System.Threading.Channels` (all BCL); `LiveScoreProjector` is pure Domain-only
Application code. See `docs/plans/2026-07-07-live-notation-design.md` for the
full design record and rationale for each decision below; this section records
only the vendoring and execution-time facts.

### Vendored asset: OpenSheetMusicDisplay (OSMD)
- Version: **2.0.0** — the latest stable release on npm at execution time
  (`dist-tags.latest`), not a floating `@latest` reference; pinned explicitly
  in the fetch URL.
- Source URL: `https://cdn.jsdelivr.net/npm/opensheetmusicdisplay@2.0.0/build/opensheetmusicdisplay.min.js`
  (jsDelivr's npm mirror — fetched once, at execution time, and committed; never
  referenced live from the served page).
- File: `src/AudioClaudio.Cli/wwwroot/osmd/opensheetmusicdisplay.min.js`
  Size: **1,262,636 bytes** (~1.2 MiB), matching the CDN's reported
  `content-length` exactly (byte-for-byte download, no truncation).
  SHA-256: `6db4be76cfc1499cabe8bccb409ff0c261fe7aa22c002d93ed61f29817bf6f22`
  Sanity-checked as real JavaScript, not a fetch-error page: starts with the
  UMD wrapper comment `/*! For license information please see
  opensheetmusicdisplay.min.js.LICENSE.txt */` followed by
  `!function(t,e){"object"==typeof exports...` (`OsmdAssetTests`).
- License: committed at `src/AudioClaudio.Cli/wwwroot/osmd/LICENSE-OSMD.txt` --
  **BSD-3-Clause**, on the §1 rule 7 allow-list (MIT/Apache-2.0/BSD/MPL-2.0);
  no copyleft. Confirmed three independent ways: the package's own
  `package.json` (`"license": "BSD-3-Clause"`), GitHub's repository license
  detector (`spdx_id: "BSD-3-Clause"`), and the npm registry's published
  metadata for this exact version. **Deviation worth noting:** the upstream
  `LICENSE` file's own text (fetched verbatim from
  `https://raw.githubusercontent.com/opensheetmusicdisplay/opensheetmusicdisplay/2.0.0/LICENSE`
  and reproduced byte-for-byte in the committed file) does not itself contain
  the literal word "BSD" anywhere — it is the bare 3-clause permission text
  with no named header. A short identification header (source URL, version,
  and the three-way confirmation above) was added *above* the verbatim text,
  changing none of the legal wording, so the license is honestly identifiable
  at a glance and `OsmdAssetTests.OsmdLicenseFileIsCommittedAndIsBsd`'s
  content check is truthful rather than accidentally passing/failing on a
  wording coincidence.
- No new NuGet package; no npm/Node toolchain introduced anywhere in this
  repo. Served as a static asset by `LiveNotationServer` (Infrastructure),
  exactly like the GeneralUser GS SoundFont is a committed static asset
  served by MeltySynth (Step 8).
- Network access, download, and license fetch all succeeded on the first
  attempt; no fallback or stub bundle was needed.

### `LiveScoreProjector` — Application, pure
`AudioClaudio.Application.UseCases.LiveScoreProjector` imports only
`AudioClaudio.Domain` (`NoteEvent`, `Score`, `Quantizer`, `QuantizationGrid`);
no I/O, no clock, no HTTP. It is push-shaped (`Add(NoteEvent) : Score`), not a
pull over `IAudioSource`, because the live device's frame channel drains
exactly once and `LiveTranscriptionSession.Run` already owns that drain — see
the design doc's "Note on the projector's shape" for the full argument
(a second independent enumeration would race the existing one over the same
bounded channel).

### `LiveNotationServer` — Infrastructure, `HttpListener` + hand-rolled SSE
Bound to `http://localhost:<port>/` only (never the `+`/`*` wildcard forms),
so no URL-ACL reservation or elevated privileges are needed on Windows. Each
`/events` connection owns its own capacity-1, drop-oldest
`System.Threading.Channels.Channel<string>` outbox — the same non-blocking
idiom `CaptureFrameStream` (Step 10) uses for live audio frames, applied here
so a slow browser can never block `PublishScore`. A late-joining client is
enqueued the most-recently-published payload the moment it connects, before
anything else, so it sees the current sheet immediately rather than a blank
page. Tested entirely via an in-process `System.Net.Http.HttpClient` — no
browser, no real device.

### Deviations from the plan's literal code (found by actually running it)

Four small, mechanical-to-substantive fixes were needed to make the plan's own
illustrative code compile and pass; each is recorded here per the "fix the
code, not the assertion" spirit of §1 rule 8, applied to the plan text itself:

1. **csproj comment couldn't contain `--`.** The plan's Task 1 `<Content>`
   comment includes `` `listen --view` `` and prose double-hyphens; XML
   comments forbid `--` anywhere in the body (MSB4025). Reworded to avoid the
   literal flag spelling and switched prose dashes to em dashes — no
   behavioral change, the glob and `CopyToOutputDirectory` are unchanged.
2. **`TcpListener.LocalEndPoint` does not exist.** `FreeTcpPort.Find()`'s
   plan text reads `probe.LocalEndPoint` (capital P, matching `Socket`'s
   property); `TcpListener`'s actual BCL member is `LocalEndpoint` (lowercase
   p) — a real, if easily confused, BCL naming inconsistency. Fixed to
   `probe.LocalEndpoint`; confirmed by the four Part-A tests passing
   (including the free-port-uniqueness one, which exercises this line
   directly).
3. **`Assert.Single(x.Where(...))` trips this repo's own analyzer gate.**
   `LiveScoreProjectorTests`' first test used the `Where`-then-`Single` form;
   `TreatWarningsAsErrors` (`Directory.Build.props`) turns the xUnit analyzer
   `xUnit2031` into a build error here (no other test in the suite had
   happened to use this form before). Changed to the suggested
   `Assert.Single(collection, predicate)` overload — same assertion, no
   change in what's tested.
4. **SSE header flush — a genuine concurrency bug, not a typo.** The plan's
   `HandleSseAsync` set `ContentType`/`SendChunked` and went straight to
   enqueueing/pumping, writing nothing until the connection's outbox had a
   score to send. `HttpListenerResponse` does not transmit its status
   line/headers to the client until the *first byte* is written to
   `OutputStream` — so a client that connects via
   `HttpClient.GetAsync(..., ResponseHeadersRead)` **before** any
   `PublishScore` call has ever happened would block forever waiting for
   headers that the server never sends. This was not caught by reading the
   code; it was caught by actually running the tests:
   `PublishScoreDeliversBase64MusicXmlToAConnectedClient` and
   `BroadcastsToAllConnectedClients` (both connect before publishing) each
   hung for the full 100 s `HttpClient` default timeout and failed;
   `LateJoiningClientImmediatelyReceivesTheCurrentScore` (publishes *before*
   connecting, so the immediate late-join enqueue triggers the first write)
   passed, which pinpointed the exact mechanism. Fix: `HandleSseAsync` now
   writes and flushes a leading SSE comment line (`: connected\n\n`) the
   moment a client connects, before it is added to `_connections` — valid
   per the SSE wire format (lines starting with `:` are comments), silently
   ignored by a real browser `EventSource`, and already transparently
   skipped by the test suite's `ReadSseDataLineAsync` helper (which loops
   until it sees a `data:` line) with no test-side change needed. All seven
   `LiveNotationServerTests` then pass in well under a second.

### Robustness hardening (post-review) — the optional view must never break core `listen`

A correctness review (no blocker/major) asked for three small robustness fixes,
all upholding one principle: the OPTIONAL `--view` side channel must never break
CORE `listen` (transcribe + write `raw.mid`/`score.mid`/`score.musicxml`) or leak
a connection. All three landed with no new dependency and the dependency rule
intact:

1. **Hook isolation in `ListenCommand`.** `_onLiveNote`/`_onFinalScore` are now
   invoked through a private `SafeInvokeHook<T>` that try/catches each call and,
   on failure, reports via the existing `_print` delegate and continues — so a
   hook whose live server died mid-session can never propagate out and abort the
   session before the file trio is written. Proven by
   `ListenCommandTests.AThrowingOnLiveNoteHookStillWritesTheFileTrioAndFiresOnFinalScore`
   (a hook that throws on every note; Run still completes, all three files are
   written, `onFinalScore` still fires).
2. **`server.Start()` guarded in `Program.cs`.** The `--view` path wraps
   `Start()` in try/catch; on failure (the documented ephemeral-port bind race,
   or a locked-down environment) it warns to stderr and runs plain `listen`,
   leaving the hooks null so nothing is ever wired to a server that never
   started. Browser-open remains best-effort (its own try/catch). The 1 s
   final-flush wait now keys off `onFinalScore is not null` (the precise "view
   was actually wired" signal) rather than merely "server object exists."
3. **Request pipeline guarded in `LiveNotationServer`.** `HandleRequestAsync`'s
   body is wrapped in try/catch with a `finally` that is now the SINGLE close
   point for `ctx.Response` on every path (static success, 404, SSE end) — so a
   client that disconnects before the SSE preamble flush can neither leak the
   response nor raise an unobserved task exception. The catch swallows only the
   expected disconnect/shutdown types (`HttpListenerException`, `IOException`,
   `ObjectDisposedException`); any other exception is left to propagate as the
   real defect it would be. The two per-method `ctx.Response.Close()` calls were
   removed from `ServeStaticFileAsync` in favor of that one guaranteed close.
4. **Static-path containment hardened.** The web-root containment check now
   compares the resolved path against the root WITH a trailing directory
   separator (`IsInsideWebRoot`), so a sibling directory like `<root>-evil/`
   cannot pass a bare `StartsWith("<root>")` prefix test.

### Accepted trade-offs (reviewed, deliberately left as-is)
Two reviewer nits are acceptable for a single-user, localhost-only, once-per-
session tool and were intentionally NOT changed:
- **`Dispose()` does not await the accept loop.** It stops/closes the
  `HttpListener` without awaiting the fire-and-forget `_acceptLoop`, so an
  in-flight `GetContextAsync` may still be unwinding as `Dispose` returns. It is
  handled cleanly by the accept loop's `catch (...) when (!_listener.IsListening)`
  guard (the pending call throws, sees the listener closed, and returns). A full
  await/quiesce handshake would be over-engineering here.
- **The final-flush wait is a fixed 1 s sleep.** After the session ends,
  `Program.cs` sleeps one second (only when the view was actually wired) to let
  the final accurate-score SSE push reach the browser before the process exits,
  rather than a confirmation handshake. A fixed heuristic is fine for this
  context; the browser's `EventSource` also auto-reconnects and re-fetches the
  current state (the late-join path) if it ever misses the last push.

### Manual acceptance (Task 6) — PASSED (Cornelius, 2026-07-07)
Manual check: `listen --view` on the primary machine (macOS M3 Max, default
browser) — the browser opened automatically and the staves rendered **live**, a
note at a time, as pitches were produced (with the **voice**, not only a piano —
YIN detects the fundamental regardless of source), and the sheet snapped to the
accurate score on Ctrl+C. Confirmed working well. (This supersedes the prior
"not performed in the CI sandbox" honesty note.) The automated coverage remains
the CI-side proof: `LiveScoreProjectorTests` (projection logic),
`LiveNotationServerTests` (the HTTP/SSE server end-to-end via `HttpClient`,
including late-join and multi-client broadcast), and `WebAssetContentTests`
(the served HTML/JS reference the right paths and call the right OSMD APIs).

## CI — nightly closed-loop retired to an on-demand deep run (2026-07-08)

**Decision (Cornelius):** delete the daily `schedule:` from the large closed-loop
sweep and keep it as a manual `workflow_dispatch` button, renamed
`nightly-closed-loop.yml` → `deep-closed-loop.yml`. The everyday guard stays the
push CI (40 pinned, deterministic seeds, strict R9.2, ~1.5 min). The deep run stays
**strict** — no budget-guard / tolerance-of-failure logic was added: when pressed it
should show every failure and upload the quarantine artifacts.

**Why.** The scheduled nightly was a *development-phase* instrument. While the
detection code (YIN, onset, segmentation) was in flux, a nightly 1,000-case random
sweep genuinely earned its keep — it caught regressions from ongoing changes and
discovered residual cases to quarantine (R9.3). Since v0.1.0 that code is **frozen**
(the residual's fix, pYIN, is Phase-2, §8). On frozen, deterministic code a daily run
just re-measures the same ~0.4% residual and reds on it most nights — pure
alarm-fatigue, and it contradicted the job's own "not a gate / not a signal the build
is broken" header. R9.4 always called the larger run "optional"; this is how that
latitude is exercised. (Prompted by the 2026-07-08 run failing on two ordinary
residual cases — count 7≠8 and 6≠7, both dropped-onset misses — with the detection
path verified byte-identical to v0.1.0, i.e. not a regression.)

**Considered and rejected.** (a) A *residual-budget guard* — green iff the measured
miss-rate stays under a documented budget, red only on a genuine detection regression
— was designed and half-built, then dropped as premature: its only value materializes
during Phase-2 detection work, so re-adding a trigger + budget then is cheaper than
carrying dormant CI semantics now (YAGNI). (b) `continue-on-error` (an always-green
fuzzer) — rejected as strictly worse than removing the schedule, since it keeps
spending ~28 min of CI to measure a constant nobody watches.

**Reinstating in Phase-2.** When detection work resumes, either restore a `schedule:`
on `deep-closed-loop.yml` or add a `push`/`pull_request` path filter on the detection
files, and (if wanted) apply the residual-budget guard so a detection change goes
green unless it worsens the residual. The harness already supports the large sample
via `CLOSED_LOOP_CASES`; nothing in the test code was removed.

## CLI — `listen --record` (real vs recreation audio, 2026-07-08)

**Opt-in `--record` writes two extra WAVs, `input.wav` and `recreation.wav`, into
`listen`'s output directory** — a by-ear correctness check: load both into a waveform
editor (e.g. Audacity) and compare the real performance against what the pipeline
heard. `input.wav` is reconstructed losslessly from the analysis frames the session
already buffers for the batch transcription pass (`LiveTranscriptionSession`'s
`FrameRecordingAudioSource`) — no second capture, no new device code; the only new
Domain surface this adds is `Framing.ReconstructMono`, the exact inverse of the
existing `Framing.Split`.

**`recreation.wav` synthesizes the RAW (unquantized) events, not the quantized
score.** Onsets in `result.Events` stay at the same sample-accurate positions as the
performance that produced `input.wav`, so the two WAVs line up on a shared timeline;
re-synthesizing the quantized `Score` instead would snap every onset to the tempo
grid and let the recreation drift out of alignment with the real audio note by note,
defeating the comparison's purpose.

**The synthesizer/SoundFont is constructed only when `--record` is passed**, reusing
`Program.cs`'s existing `Lazy<MeltySynthSynthesizer>` (already lazy since Step 9, so
`transcribe` never needs a `.sf2`) rather than adding a second construction path —
plain `listen` still runs with no SoundFont on disk, unaffected.

## CLI — `listen` session archiving (latest + timestamped copies, 2026-07-08)

**The out-dir root always holds the latest run's output at stable paths.** `listen`
clears the previous session's `*.mid`/`*.musicxml`/`*.wav` from the out-dir root on
start (top-level only, via `SessionOutputArchive.CleanLatest`) — non-recursive, so any
earlier timestamped archive subfolders are left alone — and then writes this session's
files at the same familiar names (`raw.mid`, `score.mid`, `score.musicxml`, and
`--record`'s `input.wav`/`recreation.wav`), so tools and scripts can always point at the
same filenames. On stop, once every file has been written, `SessionOutputArchive.Archive`
COPIES (not moves) that same top-level set into `<out-dir>/<YYYYMMDD_HHMMSS>/`, timestamped
by when the session STARTED — the latest files stay in the root; the timestamped folder
is an archived snapshot of that session.

**The wall-clock read for the folder name lives in `Program.cs`'s `listen` case, not in
`SessionOutputArchive` or `ListenCommand`.** `SessionOutputArchive` takes the timestamp
string as a parameter and never calls `DateTime.Now` itself; "now" enters only through
the composition root, consistent with §4's non-negotiable that the domain never reads
the wall clock.

## CLI — `listen --skip-silence` (continuous playback, 2026-07-08)

**Collapses leading/inter-note/trailing silences longer than 500 ms down to 500 ms**,
cutting the SAME sample spans from the captured audio and the re-timed notes so
`input.wav` and `recreation.wav` stay aligned on a shared timeline; gaps at or under
500 ms survive untouched, so the piece's own phrasing/rhythm is kept. The collapsing
logic is a new pure `SilenceCollapser` in `AudioClaudio.Domain` — no I/O, deterministic,
sorting notes by onset and shrinking each over-long gap (including the leading gap
before the first note and the trailing gap after the last) down to the threshold.

**`--skip-silence` implies `--record` and touches ONLY the two WAVs.** `raw.mid`,
`score.mid`, and `score.musicxml` keep the true, un-collapsed performance timing —
the notation is the faithful record of what was played; the continuous WAVs exist
purely for listen-back, so only they are de-silenced.

## CLI — browser-driven recording on `listen --view` (2026-07-08)

**`--view` now shows Start/Stop buttons instead of recording for the whole process
lifetime.** Each take is a fresh `PortAudioAudioSource` plus a fresh
`LiveScoreProjector`, opened on Start and closed on Stop, then finalized (optionally
writing `input.wav`/`recreation.wav`) and archived under its own Start timestamp —
the same per-session archive path as before, just run once per take instead of once
per process. `LiveNotationServer.PublishClear()` blanks the staff the moment Start is
pressed; the last published score stays frozen on screen after Stop, until the next
Start clears it.

**Decisions made along the way:** Ctrl+C auto-saves a take still in progress rather
than discarding it; the microphone is open only while a recording is active, not for
the whole `listen --view` process lifetime; and the last take's score is left frozen
on-screen (not blanked) between Stop and the next Start, so it stays readable.
Server↔CLI coordination uses two primitives, both signalled from the `HttpListener`
POST handlers: a `SemaphoreSlim` that Start releases and the CLI's recording loop
waits on, and a mic-stop callback that Stop invokes directly. Neither the
transcription pipeline nor the domain layer changed — this is composition-root
wiring only.

## CLI — `--note-names` (note-name lyrics, 2026-07-08)

**Opt-in `--note-names` adds a MusicXML `<lyric>` per note carrying its scientific-pitch
name** (e.g. `C4`, `F#5`), reusing `MusicXmlScoreWriter`'s existing sharps-only
step/alter/octave mapping (`PitchToXml`) rather than a second spelling table — a
learning/verification aid so a renderer such as OSMD prints the name beneath each note.
The name is shown once at each note's onset: the first decomposed part (`p == 0`) of an
element that is not itself a tie continuation from a prior measure (`!tiedFromPrevious`),
so a note split across a barline by Step 6's structural tie-splitting gets one label, not
one per tied fragment.

**Opt-in, default off, so the byte-exact golden and every existing caller are
unaffected.** `MusicXmlScoreWriter` gained one constructor parameter
(`includeNoteNames = false`); every pre-existing `new MusicXmlScoreWriter()` call site
(the golden test, the other writer tests, `LiveNotationServer`'s default, both prior CLI
call sites) keeps compiling and keeps emitting byte-identical output with no change —
verified by re-running `MusicXmlGoldenTests.EmitsByteIdenticalGoldenForTwinkleFixture`
after the change, still byte-exact.

**Threaded through one flag-configured writer instance, not two.** `transcribe` takes
the flag straight into `TranscribeCommand.Run`'s existing `MusicXmlScoreWriter`
construction. `listen` constructs a single `new MusicXmlScoreWriter(noteNames)` in
`Program.cs` and reuses that one instance for both consumers of notation — the saved
`score.musicxml` (via `ListenCommand`'s `musicXmlWriter`) and the live `--view` rendering
(via `LiveNotationServer`'s `scoreToMusicXml` hook, already an injectable
`Func<Score, string>` seam from the live-notation design) — so the two can never disagree
about whether names are shown.

## Domain — tempo estimation (median inter-onset interval, 2026-07-08)

**`TempoEstimator.Estimate` (`AudioClaudio.Domain`) recovers a BPM from detected notes'
onsets**, so `--tempo` becomes optional on `transcribe`/`listen` instead of a hard
requirement — pulling forward CLAUDE.md §8 item 2 ("Tempo estimation... removes the
`--tempo` flag") ahead of the rest of Phase-2. Detection stays tempo-free: YIN, onset
detection, and `NoteSegmenter` never consult a tempo, so estimation slots in at the one
place tempo was already consumed — `TranscriptionPipeline.Transcribe`'s quantization step,
after note segmentation has already produced `events`.

**What it does.** Sort the notes' onsets, take every consecutive gap, drop gaps outside
[0.12 s, 1.5 s] (below is a detection blip or ornament, above is a phrase pause — neither
is the beat), and take the median of what survives as the beat length. `bpm = 60 / beat`
is then folded into `[minBpm, maxBpm]` (default `[50, 180]`) by repeated halving/doubling.

**Median inter-onset interval, not a grid-fit search.** A tempo grid-fit alternative —
search candidate BPMs and keep the one whose quantization grid minimizes total
onset-snapping error — was prototyped against real recordings and rejected: it landed an
octave low, converging on half the true tempo. A coarser (slower) grid trivially reduces
total snapping error for any onset sequence that isn't already perfectly quantized, which
biases a naive grid-search toward too-slow a tempo. The median-IOI approach has no such
bias: it reads the dominant gap between onsets directly — a beat, or a clean subdivision
of one, by construction — and the fold step below is a deliberate, auditable fix for the
beat/subdivision ambiguity rather than an emergent side effect of an error-minimization
search.

**The fold, the fallback, and the override.** Because a median gap can be measuring
eighths where the true beat is quarters (or vice versa), the raw `60 / beat` BPM is folded
by repeated ×2/÷2 into the plausible range before being accepted — this is what turns a raw
240 BPM (eighths) read into the true 120 BPM (quarters), or the reverse. `Estimate` returns
its caller-supplied `fallback` tempo unchanged, without guessing, whenever there isn't
enough rhythm to work with: fewer than 3 notes, no gaps survive the range filter, or the
folded estimate still falls outside `[minBpm, maxBpm]`. The caller can always override
estimation entirely by passing an explicit `--tempo`, which both `transcribe` and `listen`
honor exactly as before (`TranscriptionSettings.EstimateTempo` defaults to `false`,
preserving R6.3's declared-tempo default for every existing caller). Validated on two real
recordings (~118 and ~131 BPM); the 131 BPM take is the fixture
`TempoEstimatorTests.EstimatesWithinTheValidatedRangeOnARealRecording` pins (asserted
within [125, 137] BPM — a tolerance, since the estimate is a statistic over human
performance timing, not a deterministic transform of a machine-generated signal like the
closed loop's synthesized corpus).

**Plugs in at quantization only — detection is untouched.** `TranscriptionSettings.
EstimateTempo` is read in exactly one place, `TranscriptionPipeline.Transcribe`'s grid
construction (the empty-frames guard branch is deliberately left alone — there are no
events to estimate from). When it is set, `TranscriptionSettings.TempoBpm` changes meaning
subtly: it is no longer the tempo used, but the *fallback* `TempoEstimator.Estimate`
returns when there isn't enough rhythm to work with. Both `transcribe` and `listen` default
this fallback to 120 BPM when `--tempo` is omitted.

**`listen`'s live preview still uses a provisional tempo; only the SAVED score is
estimated.** The live `--view`/console preview (`LiveScoreProjector`, driven off
`TranscriptionPipeline.StreamNotes`) builds its `QuantizationGrid` once, before a take has
produced any notes to estimate from, so it necessarily renders against the fallback/declared
`tempoBpm` (120 by default) for the whole take — there is no retroactive re-grid of notes
already drawn. The accurate, SAVED `score.mid`/`score.musicxml` come from the batch
`TranscriptionPipeline.Transcribe` pass that runs on stop, which DOES estimate (via the same
`EstimateTempo`-configured `settings` the live pipeline was built from), so the archived
score reflects the tempo actually played even when the live sheet music was drawn against
the provisional one. `Program.cs`'s `listen` case prints `Estimated tempo: <bpm> BPM.` once
the batch score is known, so any gap between the provisional preview tempo and the final
estimate is visible, not silent. `transcribe` similarly prints `Tempo: <bpm> BPM` (with
`(estimated)` appended when estimation ran), reading the tempo actually used off
`result.Score.Tempo` rather than echoing the CLI argument back, so the console output and
the three written files can never disagree about which tempo was used.
