# v2 Stage 3 — Notation quality (general, corpus-measured)

**Plan:** `docs/plans/2026-07-10-v2-release-workplan.md` §Stage 3. Branch `v2-stage0-rebaseline`
(merge held for the v2.0.0 tag). Mono closed loop stays **bit-exact green** and the MusicXML goldens
stay byte-stable throughout. Cornelius's calls (2026-07-11): **key auto-detected as default** (`--key`
overrides), **basic triplets are in scope** this stage.

**Method (measurement-first / honest).** Notation levers are measured on **ground-truth note-sets**
(known rhythm/key/hand/dynamics), fed straight into the notation layer — *not* end-to-end through Basic
Pitch's ~80% F1, so a number reflects the quantizer/splitter/key-detector, not engine noise. Each lever
states **baseline → target**. TDD; adversarial review at each sub-stage boundary; Sonnet produces, Opus
gates.

---

## Stage 3a: Notation-quality harness + baselines

**Goal**: a deterministic generator of scores with known {note-value (incl. triplets), key, per-note
hand, dynamic band} + metric helpers, capturing today's baseline for every lever.
**Success Criteria**: baseline numbers recorded as tests (note-value exact-match %, key-detect %,
hand-split %, dynamic-band %); dynamics/pedal/auto-tempo generalized + tested on the corpus.
**Tests**: `NotationCorpusGen` determinism; metric-helper unit tests; baseline gates (assert-current).
**Status**: Not Started

## Stage 3b: Key detection (Krumhansl-Schmuckler)

**Goal**: `KeyDetector.Detect(pitches) -> fifths` (Domain, BCL-only, pure). Wire as the **default** in
`transcribe`/`notate`; `--key` overrides. Report the detected key.
**Success Criteria**: key accuracy on the tonal corpus ≥ target (baseline → target); `--key` still forces;
default byte-goldens unaffected (writer takes explicit fifths).
**Tests**: K-S profile correlation on known keys; tie determinism; CLI override precedence.
**Status**: Not Started

## Stage 3c: Temporal hand-split

**Goal**: `HandSplitter` — online two-hand assignment (moving per-hand centers + middle-C prior +
no-cross constraint) replacing the fixed middle-C cut in `PolyphonicQuantizer`. Keep `StaffSplitter`
(middle-C) as the documented baseline.
**Success Criteria**: hand-assignment accuracy on the hand-tagged corpus ≥ target (esp. crossing cases) >
the middle-C baseline; smoke-tested on **both** mono and poly engine output (no crash, sane staff balance).
**Tests**: crossing cases recovered; determinism; per-staff bar conservation preserved.
**Status**: Not Started

## Stage 3d: Basic triplets

**Goal**: triplet-capable grid (divisions=12/quarter), quantizer snapping to a triplet-aware value set,
and `GrandStaffMusicXmlWriter` emitting `<time-modification>` + `<tuplet>` start/stop brackets; flattener
round-trips. Mono path untouched (separate types).
**Success Criteria**: triplet note-value recovery on the corpus ≥ target; a new byte-golden for a
triplet score; `xmllint` + MusicXML-4.0 structural check pass; per-staff bar conservation holds.
**Tests**: triplet grouping/bracketing golden; flatten round-trip; straight-time output unchanged.
**Status**: Not Started

## Stage 3e: Close R11.2 + reconcile docs

**Goal**: emit a representative grand-staff score (dynamics + pedal + key + hands + triplets), document +
schedule the **human** MuseScore-load verification (R11.2); reconcile DECISIONS/CLAUDE.md/plan status.
**Success Criteria**: a recorded MuseScore pass action item with a concrete sample file; status tables
updated; suite green.
**Tests**: doc-lint (existing) stays green; sample score validates structurally.
**Status**: Not Started
