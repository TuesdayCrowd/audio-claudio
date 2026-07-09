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
**Status**: DONE (landed) — `TranscriptionEvaluator`/`NoteSetEvaluation`/
`NoteMatchOptions` (Domain.Evaluation, 7 tests) + `claudio evaluate <cand.mid>
<ref.mid> [--onset-tolerance-ms]` (1 test). Reference MIDI derived from the OMR
MusicXML lives OUTSIDE the repo (`…/Death-transcription/Death.reference.mid`) —
the piece is copyrighted, so it is never committed.

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

## Stage 3: Polyphonic score building  ← CURRENT
**Goal**: turn overlapping `NoteEvent`s into readable grand-staff notation.

**Design decision — parallel types, monophonic path untouched.** The monophonic
`Score`/`Measure`/`ScoreElement`/`Quantizer`/`MusicXmlScoreWriter` are single-voice
by construction: `Measure` holds ONE ordered `ScoreElement` list, `ScoreElement`
has ONE `Pitch?`, and `Quantizer` step 3 literally *drops* simultaneous notes
("clip each note to the next onset; a note with no room is dropped"). Rather than
risk their goldens + the closed-loop suite, add **parallel grand-staff types**;
leave the monophonic ones exactly as-is.

**Model = homophonic-per-staff** (a chord is a set of pitches sharing a quantized
onset+duration; each staff is a sequence of chords). True independent inner voices
(a held note under a moving line in the same hand) are deferred — this already
captures the piece's two-hands-of-chords texture and maps cleanly to MusicXML.

New Domain types (namespace `AudioClaudio.Domain.Polyphony`):
- `Staff { Treble, Bass }`
- `ChordElement(ElementKind Kind, IReadOnlyList<Pitch> Pitches, int Velocity, int LengthTicks, bool TiedToNext)`
- `GrandStaffMeasure(IReadOnlyList<ChordElement> Treble, IReadOnlyList<ChordElement> Bass)`
- `GrandStaffScore(Tempo, TimeSignature, Subdivision, IReadOnlyList<GrandStaffMeasure> Measures)`

Sub-steps (each TDD + landed):
- **3a** `ChordGrouper` — group `NoteEvent`s whose onsets fall within a window (~1
  grid subdivision) into chords (shared onset, representative duration).
- **3b** `StaffSplitter` — assign each note to Treble/Bass by pitch (split ~MIDI 60,
  with a chord kept whole on the staff of its lowest/median note).
- **3c** `PolyphonicQuantizer` — per staff: order chords, clip to next onset,
  `grid.NearestStandardValueTicks`, gap-fill rests, bar-split (reuse
  `QuantizationGrid`) → `GrandStaffScore`. Bar conservation holds per staff.
- **3d** `GrandStaffMusicXmlWriter` (Infrastructure.MusicXml) — one part, `<staves>2`,
  `<chord>` for multi-pitch, `<backup>` between the two staves, `<staff>1/2`. Golden +
  per-staff bar-conservation tests. Keeps `MusicXmlScoreWriter` (mono) untouched.
- **3e** Wire `transcribe --poly`: emit polyphonic `score.musicxml` (3d) and a
  polyphonic `score.mid` (flatten the `GrandStaffScore` back to quantized
  `NoteEvent`s and write via the existing `INoteEventWriter`).

**Success**: rendered MusicXML shows a grand staff with chords in both hands; every
staff's measure ticks still sum to a full bar; `render score.mid` is polyphonic;
all existing monophonic tests stay green.
**Status**: IN PROGRESS.

## Stage 4: Accuracy iteration
**Goal**: close the gap to the reference; every change justified by a metric delta.
- **4a** DTW time-alignment in the evaluator (or an `evaluate --align` mode): align
  the candidate to the reference before matching so onset-F1 reflects *pitch
  recovery*, not rubato/tempo drift (today the raw metric is timing-dominated). This
  makes 4b measurable.
- **4b** Decoder threshold tuning (`OnsetThreshold`/`FrameThreshold`/`MinNoteLen`)
  swept against the aligned metric — current path over-generates (1887 vs 1100 ref),
  so precision is the lever.
- **4c** (optional) key-signature-aware enharmonic spelling; velocity/dynamics.
**Status**: Not Started (4a may be pulled forward — it is how Stage-3 output gets scored).

## Stage 5: CLI + docs
**Goal**: ship it.
**Deliverable**: `transcribe` uses the polyphonic path (flag or default);
README + CLAUDE.md updated honestly (what it recovers, what it can't); the
`evaluate` command documented.
**Status**: Not Started

---
Remove this file when all stages are complete and on `main`.
