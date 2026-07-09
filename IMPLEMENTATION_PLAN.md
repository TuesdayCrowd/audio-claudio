# Polyphonic transcription: audio → MusicXML matching an engraved score

**Goal.** Enhance audio-claudio until `transcribe` on a real piano recording
(`…07 Death.wav`) produces MusicXML that recovers the notes of the engraved score
(`…D4P - Death.pdf`) — not a 72-note monophonic sketch.

**Honest ceiling (read first).** A WAV is a *performance*; a PDF is a *score*.
Rubato, *ad lib.* cadenzas, engraving/voicing choices, and enharmonic spelling
(D♭ vs C♯) are not recoverable from audio. "Byte-identical to the PDF" is not a
real target for *any* technology. The real, measurable target: maximize
**note-level F-measure** (pitch + onset within tolerance) of the transcription
against a reference note-set, and emit a polyphonic MusicXML that is
recognizably the piece. Every stage is judged by the Stage-1 metric, not by eye.

**Architecture fit.** `ITranscriber.Transcribe(IAudioSource) → (Score, RawEvents)`
already exchanges `IReadOnlyList<NoteEvent>`, which can carry overlapping notes.
Monophony is assumed only in `TranscriptionPipeline` (YIN front end), `Quantizer`,
and `MusicXmlScoreWriter`. Polyphony is therefore a **new `ITranscriber` adapter**
plus **polyphonic score-building** — the §8 Phase-2 plan, behind the same port.
License discipline holds: Microsoft.ML.OnnxRuntime (MIT) + Basic Pitch model
(Apache-2.0) are both permissive (§1.7, §3 stack table).

---

## Stage 1: Measurement harness — the yardstick
**Goal**: quantify "does the transcription match the reference notes?"
**Deliverable**: `NoteSetEvaluation` (Domain) — precision/recall/F1 between two
`IReadOnlyList<NoteEvent>` with configurable onset tolerance and pitch match
(standard MIR note metric, plus a pitch-only recall that ignores timing for
rubato-tolerant scoring). A `claudio evaluate <candidate.mid> <reference.mid>`
CLI command. A reference MIDI derived from `Death.omr.musicxml`.
**Success**: metric is deterministic, unit-tested (exact-match→F1=1; disjoint→0;
tolerance boundary; octave error counted wrong); an honest baseline number for
today's monophonic pipeline is recorded.
**Status**: Not Started

## Stage 2: Polyphonic engine behind ITranscriber
**Goal**: replace YIN's one-note-per-frame with true polyphony.
**Deliverable**: `BasicPitchTranscriber : ITranscriber` (Infrastructure) running
the Basic Pitch ONNX model via Microsoft.ML.OnnxRuntime, with the note-creation
post-processing (onset+frame posteriorgram → notes) ported to Domain/Application.
Model committed under `fixtures/models/` with its Apache-2.0 license.
**Success**: on `…07 Death.wav`, note-level F-measure vs the Stage-1 reference is
dramatically higher than the monophonic baseline; determinism (same WAV → same
notes) holds; a spike proves ONNX inference runs on this machine before the full
adapter is built.
**Status**: In Progress —
  - 2a DONE (landed): `BasicPitchModel` ONNX runner; .NET ONNX Runtime de-risked
    on osx-arm64; model contract locked by test (440 Hz → A4).
  - 2b DONE (landed): `BasicPitchNoteDecoder` — faithful port of
    `output_to_notes_polyphonic` (infer-onsets, peak-pick, energy-walk, melodia), 5 tests.
  - 2c DONE (landed): `AudioResampler` (band-limited Lanczos), FFT-verified pitch preservation.
  - 2d DONE (landed): `BasicPitchTranscriber : ITranscriber` (resample → Basic Pitch windowing
    → decode → frame→sample), CLI `transcribe --poly`. End-to-end polyphony proven (chord →
    both pitches). **On Death: 49 → 1887 notes; onset-F1 0%→6.7% (±250 ms), tempo-scaled
    0.2%→21.2% (±300 ms); pitch-class content recovery ≈ 87%.** Stage 2 COMPLETE.

## Stage 3: Polyphonic score building
**Goal**: turn overlapping `NoteEvent`s into readable notation.
**Deliverable**: chord grouping (near-simultaneous onsets → one chord),
treble/bass split by pitch register, polyphonic quantization, and a
`MusicXmlScoreWriter` path emitting `<chord>`, two `<staff>`s, and per-staff
voices. Keeps the existing monophonic golden tests green.
**Success**: rendered MusicXML shows a grand staff with chords in both hands;
bar-conservation property still holds per staff.
**Status**: Not Started

## Stage 4: Accuracy iteration
**Goal**: close the gap to the reference.
**Deliverable**: threshold tuning against the Stage-1 metric; key-signature-aware
enharmonic spelling; onset/offset and min-duration tuning; optional regression
fixture. Each change justified by a metric delta, not vibes.
**Status**: Not Started

## Stage 5: CLI + docs
**Goal**: ship it.
**Deliverable**: `transcribe` uses the polyphonic path (flag or default);
README + CLAUDE.md updated honestly (what it recovers, what it can't); the
`evaluate` command documented.
**Status**: Not Started

---
Remove this file when all stages are complete and on `main`.
