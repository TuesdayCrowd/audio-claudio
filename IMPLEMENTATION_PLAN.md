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

## Stage 3: Polyphonic score building
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
**Status**: DONE (landed). 3a–3c domain (chords/staves/quantizer) + 3d
`GrandStaffMusicXmlWriter` + `GrandStaffFlattener` + 3e wiring. On Death: `--poly`
now emits a grand-staff `score.musicxml` (2 staves, 149 bars, 632 chords, xmllint-clean,
renders in Verovio as a real grand staff) and a polyphonic `score.mid` (max 8
simultaneous). Monophonic path untouched (386 fast tests). Remaining polish (deferred to
Phase-2 refinement): true independent inner voices, key signature, timing alignment (Stage 4).

## Stage 4: Accuracy iteration  — COMPLETE
**Goal**: close the gap to the reference; every change justified by a metric delta.

**Order note.** DTW lands *before* the 4b threshold sweep on purpose: tuning is judged by
the metric, and the metric is only trustworthy once alignment has removed tempo drift.
Global-scale (4a) cancels one overall tempo ratio; DTW cancels *local* rubato. So: 4a → DTW
→ 4b (sweep against the DTW metric) → 4c.

- **4a** DONE (landed): `OnsetAlignment.GlobalScale` + `evaluate --align` — rescales the
  candidate's onset span onto the reference's, cancelling the gross tempo difference so the
  metric reflects pitch recovery. On Death poly raw.mid vs reference (±250 ms): F1 6.7% → 18.1%
  (matched 100 → 270), confirming most of the low F1 was timing drift.

- **DTW warp** — `OnsetAlignment.DtwWarp(candidate, reference)` (Domain.Evaluation) + `evaluate
  --warp`. Global-scale is a single linear ratio; rubato is *local* tempo variation it cannot
  fix. DTW builds a monotonic correspondence between the two onset-time sequences (pre-scaled,
  cost = |Δt|, standard match/insert/delete DP + backtrack), reduces the path to strictly
  increasing anchor pairs, and applies a piecewise-linear warp to every candidate onset
  (duration scaled by local slope). Pure/deterministic; candidate never mutated. Time-only by
  design (never consults pitch), so pitch recovery stays judged independently — no circularity in
  the metric. Headline test: an *in-lane* rubato candidate (each note displaced < half its gap)
  that `GlobalScale` cannot recover, `DtwWarp` does — all onsets match at tight tolerance. `--warp`
  implies alignment and wins over `--align` if both are passed.
  **Status**: DONE (landed, e8dd656). On Death poly raw.mid vs reference, DTW beats global-scale
  at every tolerance, more so as it tightens: TP 270→322 / F1 18.1%→21.6% (±250 ms), 175→224 /
  11.7%→15.0% (±150 ms), 135→187 / 9.0%→12.5% (±100 ms). 4 TDD tests.

- **4b** Decoder threshold tuning. `--onset-threshold`/`--frame-threshold`/`--min-note-len` on
  `transcribe --poly` (TDD `PolyDecoderOptions.FromArgs`; defaults = Basic Pitch's stock
  0.5/0.3/11, so behavior is unchanged unless tuned), swept on the real Death audio against the
  DTW metric.
  **Status**: DONE (landed). **Headline finding: thresholds are a *notation-cleanliness* knob,
  not an *accuracy* knob.** F1 is pinned ~14–15% (±150 ms) / ~21–22% (±250 ms) across the whole
  useful range — bounded by onset timing + pitch-exactness, not over-generation, so trimming false
  positives barely moves it (raising thresholds trades recall for precision at ~constant F1;
  pitch-class content recovery is already ~87%). What tuning buys is a readable score:
  `--onset-threshold 0.6 --frame-threshold 0.4 --min-note-len 16` cuts the note count 1887 → 1188
  (≈ the ~1100-note reference density) at essentially zero F1 cost (F1@250 21.6%→21.5%), higher
  precision. Honest-default rule kept: the Domain `NoteDecoderOptions.Default` stays the faithful
  stock port (max recall); the tuned values are *documented* (README) as the recommendation for
  dense polyphonic piano, not baked into a global default overfit to one piece.

- **4c** Key-signature-aware enharmonic spelling. `PitchSpeller.Spell(midi, fifths)`
  (Domain) — line-of-fifths nearest-to-centre method: diatonic notes spell naturally, chromatics
  spell in the key's accidental direction (A♭ major → D♭/E♭/G♭/A♭/B♭, not D#/G#/A#). A declared
  key (`transcribe --poly --key <fifths>`, default 0 = today's behaviour) threads into
  `GrandStaffMusicXmlWriter`: it emits the real `<fifths>` and spells every `<pitch>` + note-name
  lyric through the speller.
  **Status**: DONE (landed). 15 TDD tests (flat-key/sharp-key/C-major spelling cases + an
  all-keys round-trip invariant + the writer emitting `<fifths>` and flats). On the real Death
  audio, `transcribe --poly --key -4` now emits `<fifths>-4</fifths>` and **545 flats / 0 sharps**
  — the whole 4-flat piece spelled correctly, where the pre-4c writer would have engraved every
  chromatic as a sharp. Velocity is already carried in raw.mid/score.mid (Basic Pitch amplitude);
  MusicXML `<dynamics>` marks are lossy and deliberately deferred.

**Status**: COMPLETE — 4a, DTW, 4b, 4c all landed. Every gain is metric-justified; the honest
ceiling holds (F1 is timing/pitch-exactness-bound at ~15–22%, not over-generation-bound). Only
Stage 5 (final docs sweep) remains.

## Stage 5: CLI + docs  ← CURRENT
**Goal**: ship it.
**Deliverable**: `transcribe` uses the polyphonic path (flag or default);
README + CLAUDE.md updated honestly (what it recovers, what it can't); the
`evaluate` command documented.
**Status**: Not Started

---
Remove this file when all stages are complete and on `main`.
