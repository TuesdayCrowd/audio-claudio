# Audio Claudio — Implementation Plans

This directory holds the step-by-step implementation plans for the Audio Claudio
piano transcriber, one per build-spec step in [`../../CLAUDE.md`](../../CLAUDE.md)
§6. Each plan is a self-contained, test-driven task list written for an executor
with **zero prior context** on this codebase, DSP, or C# audio — study the plan,
work its tasks in order (red → green → refactor → commit), and stop at the step's
*Verify* gate.

> **Execute with the `superpowers:executing-plans` skill**, one step at a time.
> Per `CLAUDE.md` §1 rule 3, a step is not started until the previous step's
> *Verify* section is green **and committed**. No skipping ahead.

## The contract comes first

[`CONTRACTS.md`](CONTRACTS.md) is the **authoritative cross-step API reference** —
every shared type name, method signature, namespace, and file path. The 13 plans
were drafted in parallel and then reconciled to it. **When a plan's code and
`CONTRACTS.md` disagree, `CONTRACTS.md` wins.** Read it before executing any step
that consumes another step's types.

## Steps (execute in order)

| # | Plan | Commit (spec) | Status |
|---|------|---------------|--------|
| 0 | [Scaffold & the dependency rule](2026-07-05-step-00-scaffold-and-dependency-rule.md) | `chore: scaffold solution, dependency rule, CI` | ✅ Done (on `main`, CI green) |
| 1 | [Domain primitives — pitch math & sample time](2026-07-05-step-01-domain-primitives.md) | `feat(domain): pitch math and integer sample time` | ✅ Done (on `main`, 63 tests) |
| 2 | [Audio source port & WAV adapter](2026-07-05-step-02-audio-source-and-wav-adapter.md) | `feat: IAudioSource port, WAV adapter, deterministic signal generator` | ✅ Done (on `main`, 89 tests; delivery = **pull**) |
| 3 | [Spectral front end](2026-07-05-step-03-spectral-front-end.md) | `feat(domain): windowed spectral front end` | ✅ Done (on `main`, 112 tests; FFT = **hand-rolled radix-2**) |
| 4 | [Monophonic pitch detection (YIN)](2026-07-05-step-04-monophonic-pitch-detection.md) | `feat(domain): YIN pitch detection with cents-accuracy properties` | ✅ Done (on `main`, 138 tests; ±10¢ over MIDI 33–96, 0 octave errors) |
| 5 | [Onset detection & note segmentation](2026-07-05-step-05-onset-detection-and-segmentation.md) | `feat(domain): onset detection and note segmentation` | ✅ Done (on `main`, 157 tests; real-chain integration test) |
| 6 | [Quantization to Score](2026-07-05-step-06-quantization.md) | `feat(domain): grid quantization to Score` | ✅ Done (on `main`, 201 tests; idempotent + bar-conserving; ties = **structural split**) |
| 7 | [MIDI export & reader](2026-07-05-step-07-midi-export.md) | `feat(infra): MIDI export via DryWetMIDI` | ✅ Done (on `main`, 208 tests; DryWetMIDI 8.0.3 MIT; round-trip exact/±1 tick) |
| 8 | [Synthesis & playback (MeltySynth)](2026-07-05-step-08-synthesis-and-playback.md) | `feat(infra): MeltySynth rendering and playback` | ✅ Done (on `main`, 225 tests; GeneralUser GS committed; tolerance + spectral golden) |
| 9 | [The closed loop + `transcribe` command](2026-07-05-step-09-closed-loop.md) | `test: closed-loop synthesize→transcribe property suite` | ✅ Done (on `main`, 248 tests; strict R9.2 on audible-duration corpus; count/pitch/onset across MIDI 33–96) |
| 10 | [Live microphone capture](2026-07-05-step-10-live-capture.md) | `feat(infra): live capture adapter and listen command` | ✅ Done (on `main`, 264 tests; incremental live emission ~41 ms; device path manual) |
| 11 | [MusicXML emission (+ trio completion)](2026-07-05-step-11-musicxml-emission.md) | `feat(infra): MusicXML writer with bar-conservation property` | ✅ Done (on `main`, 287 tests; byte-exact golden; transcribe+listen emit musicxml). MuseScore GUI check pending (human). |
| 12 | [README, polish, ship v0.1.0](2026-07-05-step-12-readme-and-ship.md) | `docs: README; v0.1.0` | ✅ Done (on `main`, 311 tests; honest README; tagged **v0.1.0**) |

⚠ = the step carries a **decision gate** for Cornelius (see below).

## Beyond v0.1.0 — Phase 2 (status as it ships)

| Feature | Design | Plan | Status |
|---|---|---|---|
| **Live incremental notation** (`listen --view`) — the growing score rendered in a browser via OpenSheetMusicDisplay as the mic is played (`CLAUDE.md` §8 item 3) | [design](2026-07-07-live-notation-design.md) | [plan](2026-07-07-live-notation-plan.md) | ✅ **Done** (on `main`, 331 tests; OSMD 2.0.0 BSD-3 vendored). Browser render **confirmed** by Cornelius 2026-07-07 (staves built live from voice input, snapped correct on stop). |
| **Recording + notation tooling** (`listen --record`/`--skip-silence`/`--note-names`, auto tempo estimation, session archiving, browser Start/Stop) — `listen`/`transcribe` enhancements on top of the live view; shipped as **v0.1.2** | — | — | ✅ **Done** (on `main`, 367 tests). Manual acceptance **PASSED** (Cornelius, 2026-07-08). |
| **Polyphonic transcription** (`transcribe`, default engine) — a second `ITranscriber` behind the same port: measurement harness, Basic Pitch engine, grand-staff score building, accuracy iteration (`CLAUDE.md` §8 item 1) | [CONTRACTS §12](CONTRACTS.md) | [`DECISIONS.md`](../../DECISIONS.md) | ✅ Stages 1–5 **Done** (on `main`, 431 tests); shipped as **v0.2.0** — polyphonic is the default `transcribe` engine (`--mono` keeps the proven monophonic path). ✅ **Stage 1: closed-loop-proven** — note-level F1 ≥ 0.75 @ ±50 ms on the seed-4242 corpus (measured 79.6%, 451 notes; [`docs/CORPUS.md`](../CORPUS.md)); the ~15–22% on one real recording is rubato + OMR-reference error, not an engine ceiling (see below). |
| **Live-view polish (Area D) + a polyphonic live-capture prototype for `listen`** — `listen --view` gets a paper-background/VU-meter/playback/downloads/auto-scroll pass, and `listen` gains a **polyphonic** default engine (mirroring `transcribe`'s poly-default/`--mono` pattern) | [design](2026-07-12-live-polyphony-design.md) | — | ✅ Area D **Done**, shipped in the 0.2.x line (culminating in `v0.2.1`). ⚠️ Polyphonic live capture **Done as a PROTOTYPE only** — non-scaling (re-transcribes the whole buffer every ~1.6s), homophonic-per-staff (a chord's notes can't have independent durations — the requested A-half/C-whole example still can't be shown), near-real-time (~2s, not instant), and carries only Basic Pitch's existing ~80% F1 — it earns **no new guarantee** and ranks below the hierarchy (mono bit-exact / poly statistical F1 / Transkun parity). `--mono` is unaffected and remains the proven ~41ms path. Hardening (incremental inference so it scales; multi-voice notation) is **open**. See [`DECISIONS.md`](../../DECISIONS.md) "Area D" / "Live polyphonic capture". |

Executed with `superpowers:executing-plans`, same discipline as the numbered steps (its requirement IDs are local `LV1–LV8`, since it's Phase 2, not part of §6). Decisions recorded: mic input (MIDI rejected), browser tab (webview rejected), `HttpListener` + SSE (ASP.NET rejected), OSMD vendored (BSD-3).

**Recording + notation tooling** (v0.1.2) shipped commit-by-commit on top of live-notation, with no separate `docs/plans/` design/plan doc — each enhancement's rationale is in `DECISIONS.md`. `--record` writes `input.wav` (losslessly reconstructed) + `recreation.wav` (the raw events re-synthesized) for a by-ear A/B check; `--skip-silence` (implies `--record`) collapses pauses over 500 ms in both WAVs via the pure `SilenceCollapser`, with a de-click fade — the MIDI/MusicXML keep the true performance timing. Tempo auto-estimates via `TempoEstimator` (median inter-onset interval) when `--tempo` is omitted, pulling `CLAUDE.md` §8 item 2 forward. Each session archives to `<out-dir>/<yyyyMMdd_HHmmss>/` with a `log.txt`; `listen --view` gained browser Start/Stop controls for multiple takes. (**`--skip-silence` was removed from `listen` entirely in `v0.2.1`** — see just below; the `SilenceCollapser` Domain class stays, just unwired.)

**Polyphonic transcription** (Stages 1–5, `CLAUDE.md` §8 item 1) was tracked stage-by-stage in a repo-root `IMPLEMENTATION_PLAN.md` that played both design and plan roles; per `CLAUDE.md`'s "remove when all stages complete" mandate it was deleted at v0.2.0, and its record now lives in [CONTRACTS §12](CONTRACTS.md) (the authoritative API) and [`DECISIONS.md`](../../DECISIONS.md) (the rationale, Stages 3–5). Stage 1 built the yardstick (`TranscriptionEvaluator`/`NoteSetEvaluation` + `claudio evaluate … [--align|--warp]`). Stage 2 built the engine (`BasicPitchModel` runs Spotify's Basic Pitch ONNX model — Apache-2.0, committed — via `Microsoft.ML.OnnxRuntime` 1.27.0 MIT; `BasicPitchNoteDecoder`, `AudioResampler`, `BasicPitchTranscriber : ITranscriber`). Stage 3 built real grand-staff notation (parallel `Domain.Polyphony` types + `GrandStaffMusicXmlWriter`, added alongside — not into — the untouched monophonic `Score`/`Quantizer`/`MusicXmlScoreWriter` and closed-loop suite). Stage 4 (accuracy) added `OnsetAlignment.GlobalScale`/`DtwWarp`, tunable decoder thresholds, and `PitchSpeller` (line-of-fifths enharmonic spelling from a **declared** `--key`). Stage 5 (ship) made the polyphonic engine the CLI default (`--mono` opts out) and did the docs honesty pass. **Closed-loop-proven (Stage 1, [`docs/CORPUS.md`](../CORPUS.md)):** a CI property gate requires note-level F1 ≥ 0.75 @ ±50 ms on the seed-4242 synthetic corpus (32 cases, 451 notes; measured 79.6%, 82.0% @±150 ms). This is a *statistical* gate — ranked below the monophonic loop's exact recovery, never flattened. On the one real (copyrighted, uncommitted) piece F1 is ~15–22% by onset tolerance, confounded by rubato + OMR-reference error (pitch-class content ≈ 87%) — *not* an engine ceiling; the decoder thresholds are a notation-cleanliness knob, not an accuracy lever.

**Live-view polish (Area D) + a polyphonic live-capture prototype for `listen`** shipped in the 0.2.x line
(2026-07-12, culminating in `v0.2.1`), designed in [`2026-07-12-live-polyphony-design.md`](2026-07-12-live-polyphony-design.md)
(including a four-lens adversarial review and a measured feasibility spike). Area D is straightforward UI
polish on `listen --view` — a white paper background for score legibility in dark mode, a VU meter + input
device name, in-page take playback, one-click file downloads, auto-scroll to the newest system — done, with
no open follow-up. The polyphonic live-capture piece is **the one item on this page shipped explicitly as a
prototype, not a finished capability**: `listen` now defaults to the polyphonic engine (mirroring
`transcribe`'s pattern), re-transcribing the whole captured mic buffer every ~1.6 s and streaming the score
live, but it is non-scaling (cost grows with take length), homophonic-per-staff (Cornelius's own worked
example — two notes struck together with different lengths — still cannot be shown independently), and
near-real-time (~2 s) rather than instant. It earns no accuracy guarantee of its own and ranks below the
existing hierarchy (mono bit-exact / poly statistical F1 / Transkun parity); `--mono` keeps the proven ~41 ms
path unchanged. **Open hardening work, named rather than hidden:** genuinely incremental inference (so cost
stops growing with session length) and multi-voice notation (so the worked example can display). See
`DECISIONS.md`'s "Area D" and "Live polyphonic capture" entries for the full decision record, including why
acoustic-microphone input was chosen over MIDI despite the review showing MIDI would sidestep every blocker.

## Decision gates Cornelius owns (`CLAUDE.md` §1 rule 2)

These are **not** silently resolved in the plans. Present the trade-off, get the
call, record it in `DECISIONS.md` **before** implementing that step.

1. **Step 2 — frame delivery model.** Pull (`IEnumerable<Frame> Frames`) vs. push
   (callback / `Channel<Frame>`). Plans default to **pull** (spec recommendation)
   with a bounded-channel bridge for the mic in Step 10; the whole plan set is
   built on pull. Switching to push changes only the `IAudioSource` shape.
2. **Step 3 — FFT implementation.** Hand-rolled radix-2 (`Radix2Fft`, Domain,
   dependency-free) vs. NWaves (`NWavesFourierTransform`, Infrastructure, brings
   resampling for Phase 2). Both sit behind the Domain interface `IFourierTransform`,
   which the Step 9 pipeline receives by injection — so the dependency rule holds
   either way and only Step 3 Task 2 changes.
3. **Step 6 — cross-barline ties.** §R6.1 says "ties beyond MVP." The plan's
   `ScoreElement.TiedToNext` is **structural bar-splitting** (a note crossing a
   barline is cut at the barline, for the bar-conservation invariant and to notate
   barline-crossing notes), which is distinct from the excluded feature (spelling a
   single note's snapped *duration* as a tie-chain). Default: keep the structural
   split. Alternative: constrain the corpus so notes never cross barlines.

## Deliberate additions beyond `CLAUDE.md` §6 (flagged for Cornelius)

The reconciliation surfaced three genuine gaps the spec's step list did not assign
an owner; the plans resolve them as follows:

- **MIDI reader (Step 7).** R7.2's read-back round-trip implies a reader, and
  Steps 8–9 consume one. Step 7 now ships `MidiFileReader.Read(Stream, SampleRate)`
  / `ReadFile(path, SampleRate)` → `MidiReadResult { Events, Tempo }` alongside the
  writer.
- **`transcribe` CLI command.** §7 requires it but no single step owned it. **Step 9**
  wires `TranscribeCommand.Run` (emitting `raw.mid` + `score.mid`); **Step 11**
  extends it to also emit `score.musicxml`, completing the trio.
- **`INoteEventWriter` (Step 7).** A sixth port beyond the constitution's illustrative
  five, justified by R7.1 (write both a `Score` and a raw `[NoteEvent]` list).

## Cross-cutting refinements for executors

Small items to honor while executing (already noted in the relevant plans /
`CONTRACTS.md`):

- **One locator.** `RepoPaths` (TestSupport, Step 0) is the sole repo-root/fixture
  locator — it exposes `Fixture(...)`, `SoundFontPath`, `GoldenDirectory`. When you
  reach Steps 8, 9, and 12, route their fixture/root access through `RepoPaths`
  rather than creating the `TestPaths` / `Fixtures` / `RepositoryRoot` variants those
  drafts sketch.
- **Lazy SoundFont (Step 9).** When adding `transcribe` to `Program.cs`, make the
  `MeltySynthSynthesizer`/SoundFont construction lazy (render/play only) so
  `transcribe` needs no `.sf2`.
- **Step 10 note.** `CaptureFrameStream`'s bounded channel uses the synchronous
  `TryWrite`; `BoundedChannelFullMode.Wait` only governs `WriteAsync`, so it is inert
  there — drop it or comment it so it is not misread as blocking.

## How these plans were built

Drafted by a fan-out of one agent per step against a shared conventions foundation,
then audited by a cross-plan consistency critic + per-step spec-coverage checks, then
reconciled to `CONTRACTS.md` and re-audited. The audit drove 22 cross-plan
compile-breakers down to zero. The process mirrors the project's own closed-loop
discipline: independent generation, adversarial verification, convergence.
