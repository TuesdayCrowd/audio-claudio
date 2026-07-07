# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

***Audio Claudio*** — *audiō Claudiō*, "I hear, by means of Claude" (ablative
of instrument, as is proper). A piano transcriber built in open collaboration
with Claude Code; the name is the method.

A real-time piano transcriber in **C# / .NET 10 (LTS)**: audio comes in from a
microphone (or a file), note events come out, notation is emitted, and the
transcribed piece can be played back through a synthesized piano. Public domain
(`UNLICENSE`), hosted under the `TuesdayCrowd` GitHub organization.

This file is both the **project constitution** and the **build specification**.
Claude Code: read all of it before touching anything.

---

## Where the project is right now (read this first)

**Steps 0–3 are complete and on `main`.** Step 0 scaffold + guards + CI + `DECISIONS.md`;
the Domain primitives `Pitch`, `PitchMath.CentsBetween`, `SampleRate`,
`SamplePosition`/`SampleDuration`, `NoteEvent` (Step 1); the `IAudioSource` **pull** port +
hand-rolled WAV adapter + signal generator with `Frame`/`FrameParameters`/`Framing`
(Step 2); the spectral front end — Hann window, hand-rolled radix-2 FFT (`Radix2Fft`),
`MagnitudeSpectrum`, `SpectralFrontEnd` (Step 3); and YIN monophonic pitch detection —
`PitchEstimate`, `YinOptions`, `YinPitchDetector` (Step 4, ±10¢ over MIDI 33–96, zero octave
errors); and onset detection + note segmentation — `SpectralFlux`, `OnsetDetector`,
`FrameObservation`, `NoteSegmenter` (Step 5, with a real-chain integration test). `dotnet build`/
`dotnet test` green (157 tests; 4 `Slow` suites); and grid quantization to `Score` — `Tempo`,
`TimeSignature`, `Subdivision`, `QuantizationGrid`, `ScoreElement`/`Measure`/`Score`, `Quantizer`
(Step 6, idempotent + bar-conserving). `dotnet build`/`dotnet test` green (201 tests). Recorded
decisions: frame delivery = **pull**; FFT = **hand-rolled radix-2**; ties = **keep structural
bar-split** (`ScoreElement.TiedToNext`).

- **Next is §6 Step 7 — MIDI export via DryWetMIDI** (the first Infrastructure adapter; also ships
  the MIDI reader Steps 8–9 consume). No design decision, but it adds the DryWetMIDI NuGet package —
  record its MIT license in `DECISIONS.md` (§1 rule 7). Work the steps in order (§1 rule 3). The
  plans and the authoritative API reference (`docs/plans/CONTRACTS.md`) live in `docs/plans/`; keep
  this note and `docs/plans/README.md` honest as steps land.
- **The step-by-step plans live in `docs/plans/`.** `docs/plans/README.md` is the
  index and status tracker; `docs/plans/CONTRACTS.md` is the authoritative
  cross-step API reference (exact type names, signatures, namespaces) — follow it
  when a step consumes another step's types. Toolchain: **.NET 10 SDK (LTS)**.
- **Keep this section honest.** As steps land, update this note (and the
  `docs/plans/README.md` status table) so the next Claude Code instance knows where
  to pick up without re-deriving it from the tree.

---

## 1. How to collaborate in this repo

This is a spec-driven, open collaboration between Cornelius and Claude Code.
The division of labor and the working rules:

1. **The requirements below are the contract.** Each is a numbered, testable
   obligation (`R<step>.<n>`). "SHALL" is hard; "SHOULD" is a strong default
   that may be overridden with a stated reason in the commit message.
2. **Cornelius owns design decisions.** Where a step lists a *Design decision*,
   do not silently pick a side. Present the trade-off in prose, wait for the
   call, then implement it. If a decision has already been recorded in
   `DECISIONS.md`, follow it.
3. **One step at a time.** A step is not started until the previous step's
   *Verify* section is green and committed. No skipping ahead, no speculative
   scaffolding for future steps.
4. **Prose over code dumps in discussion.** When explaining an approach,
   explain the algorithm and the mathematics in prose first. Code appears in
   the working tree, not pasted wholesale into chat.
5. **Small, conventional commits.** One step per commit minimum; finer-grained
   is fine. Suggested commit messages are given per step.
6. **Never violate the dependency rule** (§3). If an implementation seems to
   need an inward-pointing import broken, stop and raise it — the design is
   wrong somewhere, and that conversation happens before the code does.
7. **License discipline.** The repo is `UNLICENSE`. Every dependency SHALL be
   permissively licensed (MIT, Apache-2.0, BSD, MPL-2.0 at the outermost).
   Check the license of every NuGet package before adding it; note it in
   `DECISIONS.md`. No copyleft in the dependency graph.
8. **When a test fails, the test is presumed right.** The invariants in §5 are
   the ground truth of this project. Fix the code, not the assertion, unless a
   prose argument is made and accepted that the invariant itself is misstated.

### Commands

```bash
dotnet build                          # build everything
dotnet test                           # full suite: unit + property + golden
dotnet test --filter Category=Fast    # skip the slow closed-loop properties
dotnet format                         # before every commit
dotnet run --project src/AudioClaudio.Cli -- <command>
```

---

## 2. What this project is, and what it proves

The pipeline, end to end:

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

Two things distinguish this from a toy:

- **The microphone is just an adapter.** The entire pipeline is developed and
  tested against files first. Live capture is bolted on late (Step 10) behind
  the same port, and by then the domain is already provably right.
- **The synthesizer is the test oracle.** Because the project contains both a
  transcriber (audio → notes) and a synthesizer (notes → audio), the two can be
  composed into a closed loop: generate a random valid score, render it to
  audio, transcribe the audio, and demand the original score back within stated
  tolerances. This is the project's trial balance — the single invariant that
  exercises everything at once (§5, Step 9).

**MVP scope: monophonic.** One note at a time, hand-rolled pitch detection,
fixed tempo. Polyphony arrives in Phase 2 (§8) via a neural model behind the
same port — the architecture anticipates it, the MVP does not attempt it.

---

## 3. Architecture: ports and adapters

Hexagonal, same discipline as the Tabulārium projects. Four layers, and the
dependency rule is physical (project references), not aspirational:

| Layer | Project | May depend on | Contains |
|---|---|---|---|
| Domain | `AudioClaudio.Domain` | nothing (BCL only) | pitch math, frames, note events, detection & quantization algorithms |
| Application | `AudioClaudio.Application` | Domain | use cases; the ports (`IAudioSource`, `ITranscriber`, `ISynthesizer`, `IScoreWriter`, `IClock`) |
| Infrastructure | `AudioClaudio.Infrastructure` | Application, Domain | WAV reader, PortAudio capture, DryWetMIDI export, MeltySynth adapter, MusicXML writer, ONNX runner (Phase 2) |
| Entry point | `AudioClaudio.Cli` | everything | composition root; command parsing |

The mechanical test of the boundary: *can `AudioClaudio.Domain` import an audio
device type, a MIDI library, or `DateTime.Now`?* If yes, the boundary is not
real, and the build is wrong regardless of whether it compiles.

### Pinned stack

Recorded here so it is decided once. Substitutions require a `DECISIONS.md`
entry.

| Concern | Choice | License | Note |
|---|---|---|---|
| Runtime | .NET 10 (LTS) | MIT | current through Nov 2028 |
| Live capture | PortAudioSharp2 | MIT | cross-platform (macOS M3 Max is the primary dev machine; Windows XINDE secondary) |
| MIDI | DryWetMIDI (melanchall) | MIT | the definitive .NET MIDI library |
| Synthesis | MeltySynth | MIT | pure C# SoundFont synth, no native deps |
| SoundFont | a freely-licensed GM piano (e.g. GeneralUser GS) | free | committed under `fixtures/soundfont/` with its license text |
| Notation | emit MusicXML; render externally for MVP | — | native rendering is Phase 2 |
| Property tests | CsCheck | MIT | same tool as the ledger |
| Unit tests | xUnit | Apache-2.0 | |
| Polyphony (Phase 2) | Spotify Basic Pitch ONNX via Microsoft.ML.OnnxRuntime | Apache-2.0 / MIT | behind the same `ITranscriber` port |

*Deliberately absent:* NAudio (capture is Windows-only; useless on the primary
machine). A DSP dependency for the MVP is a Step 3 design decision.

---

## 4. The domain in plain English

The mathematics the whole project rests on, stated once.

**Pitch and frequency.** Equal temperament maps MIDI note number $n$ to
frequency $f(n) = 440 \cdot 2^{(n-69)/12}$ Hz, and inversely
$n(f) = 69 + 12\log_2(f/440)$. The perceptual unit of pitch error is the
**cent**: 1/100 of a semitone, so the distance between frequencies $f_1, f_2$
is $1200\log_2(f_2/f_1)$ cents. Detection tolerances are always stated in
cents, never in Hz — a 5 Hz error is nothing at A4 and a wrong note at A0. The
88-key piano spans MIDI 21 (A0, 27.5 Hz) to 108 (C8, 4186 Hz).

**Frames and hops.** Audio is processed in overlapping windows: a **frame** of
$N$ samples advanced by a **hop** of $H$ samples. Frequency resolution and time
resolution trade off through $N$; the low end of the piano is what forces $N$
large (distinguishing A0 from B♭0 requires resolving a ~1.6 Hz gap, which is
why detection operates in the lag/cents domain rather than on raw FFT bins).

**Onsets.** A piano note is an attack transient followed by exponential decay.
Note segmentation is therefore two separable problems: *when* did a note start
(onset detection — a spectral energy novelty problem) and *what* pitch is
sounding (pitch detection). Keeping them separate is a design commitment, not
an accident.

**Quantization.** Detected events live in continuous time (sample positions).
A score lives on a grid: a tempo (BPM), a subdivision (e.g. sixteenths), and
durations snapped to the grid. Quantization is the lossy, opinionated step —
it is where "what was played" becomes "what was meant."

### The non-negotiables

The analog of "money is never a float" in the ledger. These hold everywhere,
forever, and reviewers should reject any diff that violates them:

1. **Time is integer samples.** Every position and duration in the domain is
   an integer count of samples at a declared sample rate, carried together
   (`SamplePosition`, `SampleRate` — a position without its rate is a bug).
   Seconds are a *display* conversion at the edge. Never accumulate floating
   time.
2. **The domain never reads the wall clock.** "Now" enters only through the
   `IClock` port, and only in Application/Infrastructure. Live capture
   timestamps are sample counts from the stream, not `DateTime` reads.
3. **Determinism.** The same WAV file SHALL produce the identical sequence of
   note events on every run, on every machine, bit-for-bit. No randomness in
   the domain; any tie-break in an algorithm is defined, not incidental.
4. **Pitch decisions are made in cents/MIDI space,** not raw Hz, so that
   tolerance means the same thing across the whole keyboard.

---

## 5. Testing strategy

Three layers, in ascending order of signal:

- **Unit tests** for the pitch math, windowing, and conversions.
- **Property-based tests (CsCheck)** for the invariants: conversion
  round-trips, detection accuracy over generated signals, quantization
  idempotence (each named in its step).
- **The closed-loop suite (Step 9)** — generate a random constrained score,
  synthesize it with MeltySynth, transcribe the audio, and assert the score
  comes back within tolerance. Synthesis is the oracle; no hand-labeled ground
  truth is ever needed. This suite is the project's headline and gets a
  prominent README section.

**Fixture policy.** Test signals come from two generators, both deterministic:
a *signal generator* in test utilities (pure sines, harmonic stacks with piano-
like partial decay — available from Step 2, before the synth exists) and
*MeltySynth renders* of committed MIDI files (from Step 8). Golden outputs are
checked in under `fixtures/`; a golden test failing means behavior changed, and
the diff is reviewed, never blindly regenerated.

---

## 6. Build specification

Work one step at a time. For each: read the requirements, implement, get
*Verify* green, commit, stop.

### Step 0 — Scaffold and the dependency rule

**Objective.** The four-project solution with references encoding §3, plus the
repo hygiene, compiling empty.

- **R0.1** Solution SHALL contain the four `src/` projects and
  `tests/AudioClaudio.Tests`, with project references exactly as §3.
- **R0.2** `AudioClaudio.Domain` SHALL reference nothing beyond the BCL.
- **R0.3** From the first commit: `UNLICENSE`, `.gitignore`
  (`dotnet new gitignore`), placeholder `README.md`, empty `DECISIONS.md`, and
  this `CLAUDE.md` at the root.
- **R0.4** CI (GitHub Actions) SHALL build and test on every push from the
  first commit — not deferred to the ship step.

**Done when** the empty skeleton builds in CI.
Commit: `chore: scaffold solution, dependency rule, CI`.

### Step 1 — Domain primitives: pitch math and sample time

**Objective.** The value types everything else speaks in. This is the Money
step.

- **R1.1** A `Pitch` type SHALL represent a MIDI note number (21–108 for the
  MVP) and convert to/from frequency per §4. Conversions SHALL be exact
  round-trips at the type's stated precision.
- **R1.2** A cents-distance function SHALL compute $1200\log_2(f_2/f_1)$
  between any two positive frequencies.
- **R1.3** `SamplePosition` and `SampleDuration` SHALL be integer types
  carrying (or scoped to) a `SampleRate`. Arithmetic across differing sample
  rates SHALL be rejected, never coerced (the currency-mismatch rule).
- **R1.4** A `NoteEvent` SHALL carry: pitch, onset (`SamplePosition`), duration
  (`SampleDuration`), and a velocity (0–127; the MVP MAY emit a constant).
- **R1.5** None of these types SHALL perform I/O or read a clock.

**Contract.**

```
Pitch { midiNumber : int }                      // 21..108
fn Pitch.frequency() -> hertz                    // 440 · 2^((n−69)/12)
fn Pitch.fromFrequency(f: hertz) -> Pitch        // nearest note
fn centsBetween(f1: hertz, f2: hertz) -> cents

SamplePosition { samples : long, rate : SampleRate }
NoteEvent { pitch : Pitch, onset : SamplePosition,
            duration : SampleDuration, velocity : int }
```

**Verify.** *Examples:* A4 = MIDI 69 = 440 Hz; A0 = 27.5 Hz; C8 = 4186 Hz
(±0.5 Hz). *Properties:* `fromFrequency(p.frequency()) == p` for all 88
pitches; `centsBetween(f, f) == 0`; cents distance is antisymmetric; the
nearest-note mapping is correct for any frequency within ±49 cents of a piano
pitch. Mixed-sample-rate arithmetic is rejected.

Commit: `feat(domain): pitch math and integer sample time`.

### Step 2 — The audio source port and the WAV adapter

**Objective.** Make "the microphone is just an adapter" real from day one. The
port yields frames; the first adapter reads WAV files; the mic waits until
Step 10.

- **R2.1** An `IAudioSource` port SHALL yield successive frames of PCM samples
  (mono, floating-point in $[-1, 1]$) at a declared sample rate, with each
  frame's starting `SamplePosition`.
- **R2.2** A WAV-file adapter SHALL implement the port for 16/24-bit PCM WAV,
  downmixing multichannel input to mono. (Hand-roll the RIFF parsing — it is
  small, and this repo takes no dependency for it.)
- **R2.3** A deterministic **signal generator** SHALL live in test utilities:
  pure sine at a given pitch, and a harmonic stack (fundamental plus $k$
  partials with piano-like amplitude decay $1/k^p$), each rendered to frames
  and to WAV.
- **R2.4** Frame size and hop SHALL be parameters of the pipeline, not
  constants scattered through code.

**Design decision.** Frame delivery model: pull (`IEnumerable<Frame>` — clean
for files, natural for tests) vs. push (callback/channel — natural for live
capture later). Recommend pull now with a bounded-channel bridge for the mic in
Step 10; Cornelius decides.

**Verify.** A generated 1 s sine WAV read back through the adapter equals the
generator's frames exactly. Downmix of a stereo file is the sample mean.
Frames tile the input with the declared hop and no gaps.

Commit: `feat: IAudioSource port, WAV adapter, deterministic signal generator`.

### Step 3 — The spectral front end

**Objective.** Windowed transforms over frames — the substrate for both pitch
and onset detection.

- **R3.1** A windowing function (Hann) SHALL be applied at exactly one place in
  the pipeline.
- **R3.2** An FFT SHALL be available to the domain, producing magnitude spectra
  per frame.
- **R3.3** The front end SHALL be pure: frames in, spectra out, no state beyond
  the declared overlap.

**Design decision — the fork of this step.** Hand-rolled radix-2 FFT vs.
NWaves (MIT). Hand-rolling is ~60 lines, dependency-free, and a legitimate
craft exercise consistent with this repo's character; NWaves is battle-tested
and brings resampling for free (which Phase 2 wants for the model's 22.05 kHz
input). Either satisfies the requirements. Record the call in `DECISIONS.md`.

**Verify.** *Property:* Parseval's theorem holds within numerical tolerance for
generated signals (energy in time domain equals energy in frequency domain —
the FFT's trial balance). *Example:* the peak bin of a windowed sine at $f$
lies at the bin nearest $f$.

Commit: `feat(domain): windowed spectral front end`.

### Step 4 — Monophonic pitch detection

**Objective.** The heart of the MVP: frame in, fundamental frequency (or
silence) out. **Algorithm: YIN** (autocorrelation-family; the cumulative-mean-
normalized difference function with parabolic interpolation). Chosen over
naive FFT-peak because piano partials routinely beat the fundamental in
magnitude, and over pYIN to keep the MVP probability-free; pYIN is a recorded
possible upgrade.

- **R4.1** Given a frame, the detector SHALL return either a fundamental
  frequency estimate with a confidence, or *unvoiced* (silence/noise). The
  threshold separating them SHALL be a named parameter.
- **R4.2** Detection SHALL be accurate within **±10 cents** on clean generated
  signals across MIDI 33–96 (A1–C7; extend to the full 88 as tuning permits —
  the extremes are legitimately hard and their status is documented, not
  hidden).
- **R4.3** The detector SHALL be robust to the harmonic stack (R2.3), not just
  pure sines — piano-like partials SHALL NOT pull the estimate to an overtone
  (the octave-error trap).
- **R4.4** Pure function; per-frame; no I/O.

**Pseudocode** (reference shape; deviate freely):

```
# YIN, per frame
d(τ)  <- Σ (x[t] − x[t+τ])²                     for τ in 1..τ_max
d'(τ) <- d(τ) / [ (1/τ) Σ_{j≤τ} d(j) ]          # cumulative-mean normalization
τ*    <- first τ where d'(τ) < threshold, refined by parabolic interpolation
f0    <- sampleRate / τ*         (or unvoiced if no τ qualifies)
```

**Verify.** *Property (the headline of this step):* for any generated pitch in
range and any harmonic-stack profile, the detected $f_0$ is within 10 cents of
the true fundamental — thousands of generated cases, not a handful. *Property:*
octave errors are zero across the generated corpus. *Example:* a silent frame
and a white-noise frame both return unvoiced.

Commit: `feat(domain): YIN pitch detection with cents-accuracy properties`.

### Step 5 — Onset detection and note segmentation

**Objective.** Turn per-frame pitch estimates into discrete `NoteEvent`s:
find attacks, bound durations, reject spurious flickers.

- **R5.1** An onset function SHALL compute spectral flux (half-wave-rectified
  frame-to-frame magnitude increase) and pick peaks above an adaptive
  threshold; each peak is a candidate note start at a `SamplePosition`.
- **R5.2** Segmentation SHALL combine onsets with the pitch track: a note
  begins at an onset with a stable voiced pitch, and ends at the next onset,
  at a transition to unvoiced, or at a decay-below-floor — whichever comes
  first.
- **R5.3** A minimum note duration (named parameter, default ~50 ms expressed
  in samples) SHALL suppress flicker.
- **R5.4** Repeated same-pitch notes SHALL be separated by their onsets — two
  C4 quarter notes are two events, never one long one. (This is the case that
  breaks pitch-track-only segmenters; it is why onsets are separate, per §4.)

**Verify.** *Golden:* a generated sequence of five known notes with silences
yields exactly five events with onsets within ±1 hop of truth. *Property:* for
generated note sequences with inter-onset gaps above the minimum duration, the
event count equals the true note count (no merges, no splits). *Example:*
repeated same-pitch notes come back as distinct events.

Commit: `feat(domain): onset detection and note segmentation`.

### Step 6 — Quantization

**Objective.** From continuous note events to a score on a grid.

- **R6.1** Given a tempo (BPM) and a subdivision, quantization SHALL snap each
  onset to the nearest grid line and each duration to the nearest standard
  value (whole through sixteenth, with dotted values; ties beyond MVP).
- **R6.2** Quantization SHALL be a pure function `[NoteEvent] -> Score` — the
  raw events are never mutated (the append-only instinct: the performance is
  the record; the score is a derived view).
- **R6.3** MVP tempo is **declared by the user**, not estimated. Tempo
  estimation is a recorded Phase 2 item; hiding an unreliable estimator inside
  the MVP would poison the closed-loop suite.
- **R6.4** A `Score` SHALL carry tempo, time signature (4/4 MVP), and measures
  of quantized notes and rests.

**Verify.** *Property (idempotence):* quantizing an already-quantized score
changes nothing. *Property:* for events generated exactly on a grid, the score
reproduces them exactly. *Example:* an onset 40 ms late at 120 BPM sixteenths
snaps to its intended grid line.

Commit: `feat(domain): grid quantization to Score`.

### Step 7 — MIDI export

**Objective.** The lingua franca out. First Infrastructure adapter over the
domain's output.

- **R7.1** An adapter using **DryWetMIDI** SHALL write both a `Score` and a raw
  `[NoteEvent]` list to standard MIDI files (the raw form preserves the
  unquantized performance; the score form is the clean version).
- **R7.2** Conversion SHALL be lossless with respect to the source's own
  precision: re-reading the file yields the same pitches, onsets (within MIDI
  tick resolution, which SHALL be chosen to make grid positions exact), and
  durations.

**Verify.** *Property (round-trip):* write → read → compare, for generated
scores and event lists.

Commit: `feat(infra): MIDI export via DryWetMIDI`.

### Step 8 — Synthesis and playback

**Objective.** Notes back to sound — the demo feature and, more importantly,
the test oracle for Step 9.

- **R8.1** An `ISynthesizer` port SHALL render `[NoteEvent]` (or a MIDI file)
  to PCM sample buffers; the **MeltySynth** adapter implements it with the
  committed SoundFont.
- **R8.2** Rendering SHALL be deterministic: same input, same samples.
- **R8.3** The CLI SHALL play a transcription aloud (`play` command) and render
  it to a WAV (`render` command). Audible playback goes through PortAudio
  output — the small first contact with the audio device layer, ahead of
  capture.

**Verify.** *Golden:* a committed two-bar MIDI renders to a WAV whose SHA-256
matches the checked-in hash (determinism made tangible). Playback is verified
by ear once; the hash carries it thereafter.

Commit: `feat(infra): MeltySynth rendering and playback`.

### Step 9 — The closed loop

**Objective.** Compose Steps 2–8 into the invariant that sells the project.

- **R9.1** A property suite SHALL: generate a random constrained score
  (monophonic, MIDI 33–96, notes ≥ eighth at 60–140 BPM, ≥ 1 grid rest between
  notes), render it via the synthesizer, transcribe the audio through the full
  pipeline with the known tempo, and compare.
- **R9.2** Pass criteria, all SHALL hold: note count matches; every pitch
  matches exactly; every onset within ±1 grid subdivision; every duration
  within ±1 subdivision.
- **R9.3** Failures SHALL persist their generated MIDI + rendered WAV to a
  quarantine directory and be added to `fixtures/regressions/` once fixed —
  the suite only ever gets harder.
- **R9.4** The suite runs in CI (a modest case count on push; a larger nightly
  count is optional).

This is `transcribe ∘ synthesize ≈ id` on the constrained corpus — the adjoint
property. When it holds over thousands of random scores, the MVP's correctness
claim is earned, not asserted.

Commit: `test: closed-loop synthesize→transcribe property suite`.

### Step 10 — Live microphone capture

**Objective.** Only now does the mic arrive — as one more `IAudioSource`
adapter over PortAudioSharp2, feeding the already-proven pipeline.

- **R10.1** The capture adapter SHALL deliver the same frame contract as the
  WAV adapter (R2.1), bridging the device callback into the pipeline's
  delivery model without drops at MVP frame rates.
- **R10.2** Latency from key-strike to emitted `NoteEvent` SHOULD be under
  ~150 ms with default parameters; the actual figure is measured and documented
  rather than promised.
- **R10.3** The CLI `listen` command SHALL print detected notes as they occur
  and, on stop, write the session's raw MIDI, quantized MIDI, and MusicXML.
- **R10.4** Capture code SHALL contain no transcription logic — if live
  behavior differs from file behavior on the same audio, the bug is in the
  adapter by definition.

**Verify.** Loopback test where feasible (play a fixture WAV through speakers,
capture, transcribe, compare loosely); otherwise a manual acceptance script
documented in the README. The automated correctness burden stays on Step 9 —
live capture adds an adapter, not new domain claims.

Commit: `feat(infra): live capture adapter and listen command`.

### Step 11 — MusicXML emission

**Objective.** The score as notation the world can render.

- **R11.1** A writer SHALL emit valid MusicXML 4.0 from a `Score`: single
  treble/bass-split part is out of scope — one staff, clef chosen by range,
  4/4, correct note types and rests, measures barred by the time signature.
- **R11.2** Output SHALL load cleanly in MuseScore (manual check, documented)
  and SHALL be stable — the golden file for a fixture score is checked in.
- **R11.3** Hand-roll the XML (it is a serializer, not a dependency problem).

**Verify.** *Golden:* fixture score → committed MusicXML, byte-identical.
*Property:* every measure's note+rest durations sum exactly to the time
signature (the bar-level trial balance).

Commit: `feat(infra): MusicXML writer with bar-conservation property`.

### Step 12 — README, polish, ship v0.1.0

- **R12.1** `README.md` SHALL state: the problem in plain English; the pipeline
  diagram; the non-negotiables (§4); the closed-loop suite and what it proves;
  how to run `listen`, `transcribe`, `play`, `render`; and honest limitations
  (monophonic, declared tempo, single staff).
- **R12.2** CI green on a fresh clone; `UNLICENSE` at root; tag `v0.1.0`;
  **stop.**

Commit: `docs: README; v0.1.0`.

---

## 7. CLI summary (composition root)

```
claudio transcribe <in.wav> --tempo 120 [--out-dir .]   # → raw.mid, score.mid, score.musicxml
claudio listen --tempo 100                              # live; writes the same trio on stop
claudio play <file.mid>                                 # MeltySynth playback
claudio render <file.mid> <out.wav>                     # deterministic render
```

The CLI is the only place adapters are constructed and wired to ports.

---

## 8. Phase 2 (only after v0.1.0 ships)

Recorded so the MVP can decline scope without losing the ideas. Rough order of
value:

1. **Polyphony** — Spotify Basic Pitch's ONNX serialization via
   Microsoft.ML.OnnxRuntime, as a second `ITranscriber` adapter behind the
   same port (precedent: NeuralNote embeds the same model outside Python).
   The closed-loop suite then simply widens its generator to chords — the
   oracle already exists.
2. **Tempo estimation** — inter-onset interval histogram / autocorrelation;
   removes the `--tempo` flag.
3. **Live incremental notation** — VexFlow in a webview or Manufaktura,
   rendering the score as it grows.
4. **pYIN upgrade** to the detector; velocity from onset energy; treble/bass
   staff split by pitch register.

---

## 9. Glossary

- **Fundamental / $f_0$** — the lowest partial of a note; its perceived pitch.
- **Partial / overtone** — integer-multiple frequency components above $f_0$;
  the octave-error trap for naive detectors.
- **Cent** — 1/100 semitone; the unit all tolerances are stated in.
- **Frame / hop** — the analysis window and its advance, in samples.
- **YIN** — autocorrelation-family $f_0$ estimator via a cumulative-mean-
  normalized difference function.
- **Spectral flux** — frame-to-frame increase in spectral magnitude; the onset
  signal.
- **Quantization** — snapping performed timing to a tempo grid.
- **Closed loop** — the `transcribe ∘ synthesize ≈ id` property suite; this
  project's trial balance.
