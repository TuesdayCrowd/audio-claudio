# Notation quality: a readable MusicXML score of Death (the original goal)

**Goal.** Improve the engraved output (`score.musicxml`) toward a readable, accurate score of the piece —
the project's original aim (a MusicXML that matches the PDF). Chroma (sound) is tied across models and
not the right yardstick; notation quality is about **pitch + rhythm + voicing + readability** of the
*score*, judged against the reference score and by eye.

**Source.** Transkun's transcription is musically far better for notation than Basic Pitch's — real note
durations (key-presses), sustain pedal, velocity, fewer ghosts. Its MIDI is already producible via the
Transkun CLI. Our `MidiFileReader` now honors the pedal, so Transkun's MIDI can flow through the existing
score-building (`PolyphonicQuantizer` → `GrandStaffMusicXmlWriter`) directly. No C# integration of the
model is needed to *evaluate* the notation gain — measure first.

---

## Stage 1 — Notation baseline + how we measure it (measure-first)
**Goal**: produce a grand-staff score from any MIDI and quantify/see its quality.
**Tasks**: add a `notate <in.mid>` CLI command (MIDI → grand-staff `score.musicxml` + `score.mid`, via
the existing quantizer/writer; `--tempo`/`--key`/`--note-names`). Run it on Transkun's Death MIDI and on
Basic Pitch's raw.mid. Metric: note-level F1 (pitch+onset, `--warp`) of the quantized score vs the OMR
reference (automated proxy) **plus** a Verovio render for the visual/readability judgment (the real test
for "matches the PDF"). Record whether Transkun's score is measurably + visibly better.
**Gate**: if Transkun's notation is clearly better, proceed; if not, reassess. **Status**: In Progress.

## Stage 2 — Score-building improvements (each measured)
**Goal**: close the gap between our score and the reference. Levers, judged by render + metric:
- **Tempo** — DONE: `notate` auto-estimates from onsets (`TempoEstimator`) unless `--tempo`.
- **Dynamics** — DONE: `DynamicMarks.From(velocity)` + `GrandStaffMusicXmlWriter` emits a `<dynamics>`
  mark (pp…ff) when the measure's loudness level changes. (Frequent on noisy velocities; hysteresis is a
  possible refinement.)
- **Voicing** — treble/bass split is a fixed middle-C cut; smarter hand assignment. **← NEXT**
- **Pedal marks** — emit `Ped. ___*` from CC64 (Transkun gives it). (After voicing, per Cornelius.)
- Remaining: key/accidental spelling (have it), rhythm/note-value + rest cleanup.
**Status**: In Progress (tempo + dynamics done).

## Stage 3 — Transkun as a first-class source
**Goal**: make Transkun's output reachable from the pipeline. Decision: a Python-subprocess `ITranscriber`
adapter (simple, but adds a torch/Python runtime dependency — weigh against the self-contained ethos) vs
the ONNX-split (self-contained, but the semi-CRF port is a large effort). Chosen only after Stage 1/2
prove the notation is worth it. **Status**: Not Started.

## Stage 4 — Ship
**Goal**: docs (README/CLAUDE/DECISIONS/CONTRACTS), the MuseScore load check, land. **Status**: Not Started.

---
Remove this file when all stages are complete and on `main`.
