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
| 0 | [Scaffold & the dependency rule](2026-07-05-step-00-scaffold-and-dependency-rule.md) | `chore: scaffold solution, dependency rule, CI` | Not started |
| 1 | [Domain primitives — pitch math & sample time](2026-07-05-step-01-domain-primitives.md) | `feat(domain): pitch math and integer sample time` | Not started |
| 2 | [Audio source port & WAV adapter](2026-07-05-step-02-audio-source-and-wav-adapter.md) ⚠ | `feat: IAudioSource port, WAV adapter, deterministic signal generator` | Not started |
| 3 | [Spectral front end](2026-07-05-step-03-spectral-front-end.md) ⚠ | `feat(domain): windowed spectral front end` | Not started |
| 4 | [Monophonic pitch detection (YIN)](2026-07-05-step-04-monophonic-pitch-detection.md) | `feat(domain): YIN pitch detection with cents-accuracy properties` | Not started |
| 5 | [Onset detection & note segmentation](2026-07-05-step-05-onset-detection-and-segmentation.md) | `feat(domain): onset detection and note segmentation` | Not started |
| 6 | [Quantization to Score](2026-07-05-step-06-quantization.md) ⚠ | `feat(domain): grid quantization to Score` | Not started |
| 7 | [MIDI export & reader](2026-07-05-step-07-midi-export.md) | `feat(infra): MIDI export via DryWetMIDI` | Not started |
| 8 | [Synthesis & playback (MeltySynth)](2026-07-05-step-08-synthesis-and-playback.md) | `feat(infra): MeltySynth rendering and playback` | Not started |
| 9 | [The closed loop + `transcribe` command](2026-07-05-step-09-closed-loop.md) | `test: closed-loop synthesize→transcribe property suite` | Not started |
| 10 | [Live microphone capture](2026-07-05-step-10-live-capture.md) | `feat(infra): live capture adapter and listen command` | Not started |
| 11 | [MusicXML emission (+ trio completion)](2026-07-05-step-11-musicxml-emission.md) | `feat(infra): MusicXML writer with bar-conservation property` | Not started |
| 12 | [README, polish, ship v0.1.0](2026-07-05-step-12-readme-and-ship.md) | `docs: README; v0.1.0` | Not started |

⚠ = the step carries a **decision gate** for Cornelius (see below).

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
