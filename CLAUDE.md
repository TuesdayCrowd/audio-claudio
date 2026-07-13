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

**The 0.2.x development cycle is in progress (started 2026-07-10).** The plan is
[`docs/plans/2026-07-10-v2-release-workplan.md`](docs/plans/2026-07-10-v2-release-workplan.md);
its job is to realize the Phase-2 vision (§8) with v1's discipline — every new capability *proven* on a
**general** synthetic corpus, no more single-piece chasing. **Stages 0–4 shipped in the 0.2.x line** (2026-07-11;
**Stage 4 = the self-contained Transkun engine via ONNX**: `transcribe --model transkun`, mel front end +
semi-CRF Viterbi decode ported to C#, the transformer/scorer/heads in a committed 53 MB ONNX —
**note-IDENTICAL to native PyTorch: F1 = 100 % @ ±25 ms + exact velocity**, gated in CI against committed
native-reference MIDIs; the third guarantee tier is now earned). **Stage 5 (robustness/packaging) + 4f (the
HuggingFace publish) are deferred to later 0.2.x work** (Cornelius, 2026-07-11 — "publish this as v2, save
Stage 5 for v2.1"); the Transkun artifact is committed + publish-ready + repo-only for now. See DECISIONS
"Stage 4"/"Stage 3". Reported
numbers now come only from the committed corpus in [`docs/CORPUS.md`](docs/CORPUS.md) (mono = bit-exact
closed-loop recovery; poly = **closed-loop-proven**, note-level F1 ≥ 0.75 @ ±50 ms, measured 79.6% on the
seed-4242 corpus, 451 notes, gated in CI); the polyphonic default's "preview" label is **lifted** now that
its closed-loop gate passes; and the old *Death*-recording chroma chase is retired as a goal
(`evaluate-audio` stays a general tool). **Stage 2 (core detection quality):** mono velocity-from-energy
(real dynamics in `raw.mid`/`score.mid`), opt-in `--legato` + `--coarse-rhythm`, and a pYIN-lite
octave-correction seam built + unit-tested but **NOT wired** — a *causal* correction can't be fed safely
(it regresses real octave leaps and homogenizes the pitch track; the residual needs an HMM that breaks the
live path), so plain YIN stays the proven default (see DECISIONS "pYIN-lite"). **Stage 3 (notation
quality, measured on a ground-truth harness that isolates notation from engine noise):** auto **key
detection** (Krumhansl-Schmuckler `KeyDetector`, default; `--key` overrides + is validated) 10%→**92.5%**;
**temporal treble/bass hand-split** (`HandSplitter`, replaces the fixed middle-C cut; reproduces it on
non-crossing input so goldens hold) 89%→**100%** on continuous crossings; opt-in **`--triplets`**
(`Subdivision.Twelfth` + `<time-modification>`/`<tuplet>`; mono grid provably bit-exact; off by default so
straight music gets no spurious triplets) note-value 76.5%→**100%**. Open human gate **R11.2** (MuseScore
load) scheduled for Cornelius (see DECISIONS "Stage 3"). The
guarantee hierarchy is ranked, never flattened (mono bit-exact / poly statistical F1 / Transkun-via-ONNX
statistical + ≥99 % PyTorch parity). See `DECISIONS.md` "0.2.x re-baseline", "Stage 1", "Stage 3". The v0.1/v0.2 history below stands.

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
bar-split** (`ScoreElement.TiedToNext`). MIDI I/O (Step 7) — `IScoreWriter`/`INoteEventWriter` ports +
`DryWetMidiWriter`/`MidiFileReader` in Infrastructure (DryWetMIDI 8.0.3 MIT). Build/test green (208 tests).

Synthesis + playback (Step 8) — `ISynthesizer` + `MeltySynthSynthesizer` (MeltySynth MIT) + `WavFileWriter`
+ `PortAudioPlayer` (PortAudioSharp2 Apache-2.0) + `render`/`play` CLI; GeneralUser GS SoundFont committed
under `fixtures/soundfont/`. Build/test green (225 tests). Recorded decisions also include: SoundFont =
**GeneralUser GS** committed; render golden = **tolerance + spectral** (not byte-exact — MeltySynth SIMD
mixdown isn't bit-identical across ARM64/x64).

The closed loop (Step 9) — `TranscriptionPipeline : ITranscriber` (Application, injected `IFourierTransform`)
composing Steps 3–6; the `transcribe` CLI (raw.mid + score.mid; lazy SoundFont); and the strict-R9.2
property suite. Recorded decision: the closed-loop corpus is **constrained to physically-audible note
durations** (a piano's high notes decay before a long declared duration elapses — `synthesize` is lossy
for duration there). On that corpus, count/pitch/onset/duration all hold strictly; count/pitch/onset are
separately proven across the full MIDI 33–96 range. Known documented limitation: a rare (~0.4%) YIN
octave-error / onset-miss residual on real piano audio (the on-demand deep closed-loop run discovers + quarantines it, R9.3) —
the Phase-2 pYIN upgrade (§8) is its fix, not an MVP task. Build/test green (248 tests).

Live capture (Step 10) — `PortAudioAudioSource : IAudioSource, IDisposable` (device callback → bounded
`Channel<Frame>` bridge, same frame contract as WAV), a genuinely INCREMENTAL live feed
(`TranscriptionPipeline.StreamNotes` is a lazy causal onset→pitch iterator, ~41 ms latency), and the
`listen` command (prints notes live, writes raw+quantized MIDI on stop; MusicXML seam null until Step 11).
The real device path is manual acceptance (no audio device in CI). Build/test green (264 tests).

MusicXML emission (Step 11) — hand-rolled `MusicXmlScoreWriter : IScoreWriter` (Infrastructure.MusicXml, no
new dependency), a byte-exact golden, the bar-conservation property; `transcribe` and `listen` now both emit
`score.musicxml` (the §7 trio + R10.3 complete). Build/test green (287 tests). **Open human task:** the
MuseScore GUI load check (R11.2) is deferred to Cornelius (MuseScore isn't installable in CI) — the writer is
validated by the byte-exact golden + `xmllint` + a MusicXML-4.0 structural check; DECISIONS.md has the action item.

README + ship (Step 12) — the root `README.md` (problem, pipeline, non-negotiables, the closed-loop claim
stated honestly + limitations), doc-lint tests pinning its content. **All 13 build-spec steps (0–12) are
complete and on `main`; the MVP is tagged `v0.1.0`.** Build/test green (311 tests).

Live incremental notation — `listen --view` (Phase-2 §8 item 3, shipped post-MVP as **v0.1.1**) —
`LiveScoreProjector` (Application; re-quantizes the growing note list on each onset) + a BCL-only
`LiveNotationServer` (`HttpListener` + SSE, base64-MusicXML events over a capacity-1 drop-oldest outbox) +
a vendored OpenSheetMusicDisplay bundle (`wwwroot/`, OSMD 2.0.0 BSD-3-Clause) that renders the staff in a
browser tab as the piece is played. The optional `--view` flag cannot break plain `listen` (hook isolation
+ guarded server start). Manual acceptance PASSED (Cornelius, 2026-07-07 — staves built live from voice
input, snapped correct on stop). Build/test green (331 tests).

Recording + notation tooling — `v0.1.2`, a suite of `listen`/`transcribe` enhancements on top of the live
view. `--record` writes `input.wav` (the captured mic audio, losslessly reconstructed via
`Framing.ReconstructMono`) + `recreation.wav` (the transcription re-synthesized) for A/B comparison;
`--skip-silence` collapses long inter-note pauses out of both, aligned, with a de-click tail fade
(`SilenceCollapser`); `--note-names` prints scientific-pitch names (C4, F♯5) as MusicXML `<lyric>`s under
each note; tempo is **auto-estimated** when `--tempo` is omitted (median inter-onset interval —
`TempoEstimator`, Domain). Each session's files sit at stable paths in the out-dir root and are archived to
`<out-dir>/<yyyyMMdd_HHmmss>/` with a `log.txt` of the run's console output. The `listen --view` page now
drives **multiple browser-controlled recordings** (Start/Stop buttons) with per-take controls (Title,
Record, Skip-silence, Note-names). Manual acceptance PASSED (Cornelius, 2026-07-08). Build/test green
(367 tests). CI: node24 actions; the scheduled nightly closed-loop retired to an on-demand
`deep-closed-loop` workflow.

Polyphonic transcription — `v0.2.0`, the **default** `transcribe` engine (Phase-2 §8 item 1; `--mono`
selects the monophonic YIN path, whose closed-loop guarantee is completely untouched). A second
`ITranscriber` behind the same port, built in four stages. The measurement harness (Stage 1) —
`TranscriptionEvaluator`/`NoteSetEvaluation`/`NoteMatchOptions` (Domain.Evaluation) score a candidate
against a reference note-set (exact pitch, onset-tolerant one-to-one matching) via `claudio evaluate
<candidate.mid> <reference.mid> [--onset-tolerance-ms] [--align|--warp]`. The engine (Stage 2) —
`BasicPitchModel` + `BasicPitchTranscriber : ITranscriber` (Infrastructure.Transcription) run Spotify's
Basic Pitch neural model via Microsoft.ML.OnnxRuntime (model + its Apache-2.0 license committed under
`fixtures/models/`) over resampled audio (`AudioResampler`, band-limited Lanczos) and decode via
`BasicPitchNoteDecoder`; `raw.mid` is the honest, un-quantized many-note output.

Grand-staff score building (Stage 3) — new parallel Domain.Polyphony types (`Staff`, `ChordElement`,
`GrandStaffMeasure`, `GrandStaffScore`; the monophonic `Score`/`Quantizer`/`MusicXmlScoreWriter` are left
untouched) plus `ChordGrouper`/`StaffSplitter`/`PolyphonicQuantizer`/`GrandStaffFlattener` and
`GrandStaffMusicXmlWriter` (Infrastructure.MusicXml) turn the raw events into a real two-staff
`score.mid`/`score.musicxml` — chords in both hands, treble/bass split at middle C, per-staff bar
conservation. Accuracy iteration (Stage 4) — `OnsetAlignment.GlobalScale`/`DtwWarp` (`evaluate
--align`/`--warp`) cancel global tempo drift and local rubato (time-only, never consulting pitch, so F1
stays an honest pitch-recovery signal); `--onset-threshold`/`--frame-threshold`/`--min-note-len`
(`PolyDecoderOptions`) tune the decoder's note density; `PitchSpeller.Spell` (line-of-fifths,
nearest-to-centre) gives key-aware enharmonic spelling via a **declared** `--key <fifths>` (like tempo —
declared, not estimated). **Accuracy (Stage 1 closed-loop gate, see `docs/CORPUS.md`):** the engine is
closed-loop-proven at note-level F1 ≥ 0.75 @ ±50 ms on the seed-4242 corpus (32 cases, 451 notes), measured
79.6% (81.4/82.0% at ±100/150 ms). On one real copyrighted piece (audio + reference kept outside the repo, never committed) it
measures ~15–22% by onset tolerance — but that gap is performance rubato + the OMR reference's own error,
**not** the engine's pitch/onset fidelity, so ~15–22% is *not* an engine ceiling. The three decoder
thresholds are a notation-cleanliness knob (tuned values cut the note count ~a third toward the reference's
density at ~zero F1 cost), not an accuracy lever. Build/test green (431 tests).

**The 0.2.x line (2026-07-12) ships two things off the `live-polyphony` branch: live-view polish (Area D) and a
polyphonic live-capture prototype for `listen` — the latter is the headline, and its honesty matters as
much as its existence.** Area D is pure polish on the already-accepted `listen --view` browser page: the
score now renders on a white "paper" panel so it stays legible in dark mode, a VU meter + resolved
input-device name track the mic live, the finished take plays back in-page (`<audio>`) with one-click
downloads of `raw.mid`/`score.mid`/`score.musicxml`/`recreation.wav`, and the sheet auto-scrolls to the
newest system as notes stream in. Separately — **`listen` now DEFAULTS to a polyphonic engine**, mirroring
`transcribe`'s poly-default/`--mono` pattern: it re-transcribes the whole captured mic buffer every ~1.6 s
with the existing batch Basic Pitch model and streams the resulting grand-staff score to the browser as you
play, saving the take on Stop exactly as a batch `transcribe` run would. **This earns no new guarantee — it
is prototype-quality, shipped as the default at Cornelius's explicit direction, not because it clears this
project's bar.** It is non-scaling (every tick re-transcribes the ENTIRE buffer from scratch — a deliberate
shortcut, not the incrementally-inferring architecture the design sketched, so cost grows with take length;
short takes only), homophonic-per-staff (two notes struck together with different lengths still collapse to
one displayed duration — the requested A-half/C-whole example still cannot be shown; multi-voice notation
was not built), near-real-time rather than instant (~2 s, refreshing roughly every 1.6 s), and carries only
Basic Pitch's already-known ~80% F1 (`docs/CORPUS.md`) — it presents the existing polyphonic engine live and
sits strictly **below** the ranked guarantee hierarchy (monophonic bit-exact / polyphonic-batch statistical
F1 / Transkun ≥99% parity), never flattened into "proven." `--mono` is completely unaffected and remains the
only `listen` path with a proven accuracy guarantee (the exact-recovery closed loop, ~41 ms latency). Mic
input was chosen over MIDI for this feature despite a four-lens adversarial review showing MIDI would make
polyphony, per-note durations, and multi-voice notation all native and exact — Cornelius's call, in service
of the project's "the microphone is just an adapter" premise. Incremental inference (so it scales) and
multi-voice notation (so the worked example can display) are the named open next steps, not silently
dropped. Full design + the adversarial review + a measured feasibility spike (window inference ~9 ms, 183×
under the 1.64 s budget) are in
[`docs/plans/2026-07-12-live-polyphony-design.md`](docs/plans/2026-07-12-live-polyphony-design.md); see
`DECISIONS.md` "Area D" and "Live polyphonic capture".

- **The v0.1.0 MVP is shipped; `v0.1.1` added live incremental notation; `v0.1.2` added the recording +
  notation tooling above; `v0.2.0` makes polyphonic transcription (previously opt-in) the default
  `transcribe` engine (above; `--mono` keeps the proven monophonic path); subsequent 0.2.x development adds
  live-view polish and a polyphonic live-capture PROTOTYPE for `listen` (above; again `--mono` keeps the
  proven path), culminating in the current `v0.2.1`.** The lone open human follow-up is the MuseScore GUI
  load check for the MusicXML golden (R11.2 — see `DECISIONS.md`), corroborated by OSMD rendering the same
  `MusicXmlScoreWriter` output. Remaining Phase-2 items (§8): pYIN pitch-hardening for the monophonic
  detector (the rare ~0.4% octave residual, and the live-frame ≈ MIDI 42–93 range limits on real mic audio);
  velocity-from-energy and a treble/bass split for the *monophonic* path specifically (both already true of
  the polyphonic path — Basic Pitch amplitude, `StaffSplitter` — but not ported back to
  YIN/`MusicXmlScoreWriter`);
  and a legato / coarser-grid note-off for cleaner rhythm from uneven beginner playing. The step plans and
  the authoritative API reference (`docs/plans/CONTRACTS.md`) live in `docs/plans/`; keep this note honest if
  work resumes.
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
claudio transcribe <in.wav> [--tempo 120] [--out-dir out] [--note-names] [--mono] [--model <path>] [--key <fifths>] [--onset-threshold <v>] [--frame-threshold <v>] [--min-note-len <frames>]   # → raw.mid, score.mid, score.musicxml; POLYPHONIC (Basic Pitch, grand staff) by default, --mono for monophonic YIN (which auto-estimates tempo when --tempo is omitted); --note-names adds a scientific-pitch-name lyric under each note; --key declares the key signature for enharmonic spelling; the three thresholds tune note density; --out-dir defaults to `out` (also true of `notate`)
claudio listen [--tempo 100] [--out-dir out] [--view] [--record] [--note-names] [--time-signature 4/4] [--mono] [--soundfont <path>]  # live; as of 0.2.1 POLYPHONIC (Basic Pitch) by default — a near-real-time PROTOTYPE (see "Where the project is right now"): re-transcribes the whole mic buffer ~every 1.6s and streams the score to --view; fixed 120 BPM unless --tempo is given (no auto-estimate); --mono selects the proven ~41ms monophonic path instead (auto-estimates tempo if --tempo omitted); writes the same trio on Stop/Ctrl+C; --record also writes input.wav + recreation.wav (each session archived under <out-dir>/<YYYYMMDD_HHMMSS>/); --note-names shows each note's name (e.g. C4) beneath it in --view and score.musicxml; --time-signature sets the saved score's meter (a --view take uses its own browser selector instead); --skip-silence was REMOVED in 0.2.1
claudio play <file.mid>                                 # MeltySynth playback
claudio render <file.mid> <out.wav>                     # deterministic render
claudio evaluate <candidate.mid> <reference.mid> [--onset-tolerance-ms 50] [--align|--warp]  # note-level precision/recall/F1 of a transcription vs a reference; --align cancels a global tempo ratio, --warp (DTW) also removes local rubato and wins if both are given
```

The CLI is the only place adapters are constructed and wired to ports.

---

## 8. Phase 2 (only after v0.1.0 ships)

Recorded so the MVP can decline scope without losing the ideas. Rough order of
value:

1. **Polyphony — the default `transcribe` engine (`v0.2.0`), closed-loop-proven in Stage 1**
   (note-level F1 ≥ 0.75 @ ±50 ms on the seed-4242 corpus, gated in CI). (see
   "Where the project is right now" above; `--mono` keeps the monophonic path).
   Spotify Basic Pitch's ONNX serialization via Microsoft.ML.OnnxRuntime, a
   second `ITranscriber` adapter behind the same port, exactly as anticipated
   (precedent: NeuralNote embeds the same model outside Python). **One correction
   to the plan as written:** the closed-loop suite was *not* widened to chords — a
   synthesize→transcribe oracle has no honest ground truth for a real, copyrighted
   performance. Accuracy is instead measured by a new `evaluate` harness (Stage 1)
   scoring against a real reference recording's note-set; that is how the
   ~15–22% real-world F1 above was established. The polyphonic closed-loop **gate**
   (`PolyphonicClosedLoopTests`, Stage 1) measures the engine's *intrinsic*
   fidelity at **F1 79.6%** @±50 ms (82.0% @±150 ms, seed-4242, 451 notes) on clean
   synthesized chords and requires F1 ≥ 0.75 — showing that ~15–22% real-world figure is dominated by
   rubato + OMR-reference error, not the engine (see `DECISIONS.md` and `docs/CORPUS.md`). It is a
   statistical gate, ranked below (never flattened into) the monophonic loop's exact recovery.
2. **Tempo estimation — DONE (shipped in `v0.1.2`).** Not the histogram/
   autocorrelation approach sketched here: `TempoEstimator` uses the median
   inter-onset interval (a grid-fit search was tried first and rejected — it
   converged on half the true tempo). `--tempo` was not removed; it is now
   optional, an explicit override of the auto-estimate, on both `transcribe` and
   `listen`.
3. **Live incremental notation — DONE (shipped in `v0.1.1` as `listen --view`).**
   Not VexFlow/Manufaktura: a vendored OpenSheetMusicDisplay (OSMD) bundle rendered
   in a browser tab, fed live over SSE by `LiveNotationServer`.
4. **Still open — status per sub-item, not one bundle:**
   - **pYIN upgrade** to the monophonic detector — open; would fix the rare ~0.4%
     YIN octave-error/onset-miss residual and the live-frame ≈ MIDI 42–93 range
     limit.
   - **Velocity from onset energy** — open for the monophonic path
     (`TranscriptionPipeline` still emits a constant `NoteEvent.DefaultVelocity`).
     The polyphonic path already carries real velocity (Basic Pitch amplitude) —
     a different code path, not this item.
   - **Treble/bass staff split** — open for the *monophonic* default
     (`MusicXmlScoreWriter` is still one staff). Already done for the polyphonic
     path (`StaffSplitter`, Stage 3b); not yet ported back to the monophonic writer.

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
