# The measurement corpus

> **This is the source of every *headline* accuracy number.** Each engine's baseline
> and ceiling figure — in the README, `DECISIONS.md`, or a release note — is measured on
> the **generated** corpus described here, never on a single recording. (One real
> recording's confounded figures, ~15–22 % F1 and 78.6 % chroma, still appear in those
> docs as explicitly-labelled, non-headline context — the gap explained, never used to
> state a ceiling.) The v2 re-baseline (see the v2 release workplan and `DECISIONS.md`)
> exists precisely to stop reporting one copyrighted piece's chroma similarity as if it
> were a quality signal.

Two principles govern it, both inherited from v1's closed-loop discipline:

1. **The synthesizer is the oracle.** A generated score *is* its own ground truth:
   synthesize it with MeltySynth, transcribe the audio, compare to the score. No
   hand-labelling, no reference error — the comparison is exact by construction.
2. **Measure only as generally as the generator.** So the generator's distribution
   is committed and documented (below), and every number names its **tolerance** and
   its **seed**. A figure with no stated corpus is not a figure.

A separate handful of varied clips is a **qualitative smoke set only — it carries no
F1** (it has no ground truth; that is exactly the trap the re-baseline closes). See
[Qualitative smoke set](#qualitative-smoke-set).

---

## Corpus 1 — monophonic (`ClosedLoopGen`)

Backs the monophonic engine's **bit-exact** closed-loop guarantee (the strongest of
the three engines). One note sounds at a time; the synthesizer must give the exact
score back. Two sub-corpora:

### 1a. Audible-capped `Cases` — proves all four R9.2 criteria

| Dimension | Distribution |
|---|---|
| Polyphony | Monophonic (one note at a time; ≥ 1 subdivision of rest between notes) |
| Pitch range | MIDI 33 (A1) – 96 (C7), **capped per tempo** to pitches that stay audible for their declared duration (see below) |
| Notes per case | 3–8 |
| Tempo | 60–140 BPM |
| Note values | eighth · dotted-eighth · quarter · dotted-quarter · half (`{2,3,4,6,8}` sixteenth subdivisions), each capped to the pitch/tempo's audible maximum |
| Rests between notes | 1–4 sixteenth subdivisions |
| Velocity | constant 100 (R1.4 MVP) |
| Grid / metre | sixteenth-note grid, 4/4 |
| Sample rate | 44 100 Hz (fixed, so MeltySynth renders deterministically) |

**The audible-duration cap** (Cornelius decision, `DECISIONS.md` "Step 9 — closed-loop
corpus constrained to physically-audible note durations"): the offset detector measures
duration from acoustic energy, so a note may only declare a duration it stays *audible*
for. A pitch's audible time `T_audible(p)` is measured once against the committed
GeneralUser GS SoundFont, at the exact release threshold the detector uses; a note is
capped to `0.85 · T_audible(p)`. This is the *only* refinement to R9.1, and it weakens
no R9.2 tolerance — it removes cases the SoundFont physically cannot support, not cases
the transcriber gets wrong. In practice it limits the duration-proving corpus to roughly
MIDI 33–71.

### 1b. Uncapped `FullRangeCases` — proves count/pitch/onset across the whole keyboard

Identical to 1a but with the full MIDI 33–96 range and any standard note value (no
audible cap). Offset refinement only ever changes *durations*, so **count, pitch, and
onset** are recoverable across the entire keyboard even where a pitch cannot sustain an
audible eighth; duration alone is not asserted for this corpus.

### Seeds & case counts

| Run | Cases | Seed | Where |
|---|---|---|---|
| CI push | 40 (each sub-corpus) | pinned PCG `000000000010`, consumed by a direct `PCG.Parse` + `Gen.Generate` loop (CsCheck's threaded `Sample` is **not** seed-reproducible across processes — `DECISIONS.md`) | every push |
| Deep sweep | up to 1 000, unpinned | fresh each run (`CLOSED_LOOP_CASES` env) | on-demand `deep-closed-loop` workflow |

### Measured result (baseline)

`transcribe ∘ synthesize ≈ id` at **strict R9.2** — note **count exact**, **pitch
exact**, **onset ±1 subdivision**, **duration ±1 subdivision** — passes with **zero
failures** over the 40-case capped corpus; **count/pitch/onset** additionally exact over
the 40-case full-range corpus. A rare (~0.4 %) octave-error / missed-onset residual on
real synthesized audio is surfaced and quarantined by the deep sweep, not hidden (README
"The closed loop").

---

## Corpus 2 — polyphonic (`PolyphonicClosedLoopGen`)

Backs the polyphonic engine's **statistical** claim. The neural engine does not recover
a score exactly, so the corpus measures *how much* it recovers on clean audio — isolating
the engine's intrinsic fidelity from the rubato + reference error that depress any
real-world number.

| Dimension | Distribution |
|---|---|
| Polyphony | Chords of 1–4 distinct pitches sharing one onset |
| Chord root | MIDI 40 (E2) – 60 (C4) |
| Intervals above root | `{3,4,5,7,8,9,10,12}` (thirds … octave); top note ≤ MIDI 72 (C5) |
| Chords per case | 5–7 (≈ 5–8 s of audio, mean ≈ 6.5 s on the committed seed-4242 corpus) |
| Tempo | 80–120 BPM |
| Chord durations | mostly quarter (4 subdivisions), some half (8) |
| Rests between chord onsets | 2–4 sixteenth subdivisions |
| Velocity | constant 100 |
| Grid / metre | sixteenth-note grid, 4/4 |
| Sample rate | 44 100 Hz |

The range is capped at C5 for the same physical reason as Corpus 1: above it the
SoundFont's notes decay below the decoder's minimum note length before an attack can
register, which would measure the SoundFont's decay rather than the engine.

### Seed & case count

Seed **4242**, **32 cases** (451 reference notes, 564 transcribed) in the committed gate.
Scored by `TranscriptionEvaluator` (exact pitch; onset-tolerant, one-to-one matching) at
±50, 100, and 150 ms, micro-averaged across cases. A deep run overrides the count via
`POLY_CLOSED_LOOP_CASES` (the polyphonic analogue of `CLOSED_LOOP_CASES`). The @±50 ms F1
is converged at this size (±0.2 pt vs a 24-case draw), so the number is a claim, not a
small-sample artifact.

### Measured result (the committed gate)

Note-level **F1 79.6 %** at ±50 ms (81.4 % / 82.0 % at ±100 / 150 ms; recall ~90 %,
precision ~72 %). The gate (`PolyphonicClosedLoopTests`, v2 Stage 1) requires **F1 ≥ 0.75
at ±50 ms** — the committed threshold (Cornelius, 2026-07-10) — and runs on every CI push
(the ONNX inference is ~4–5 s for the whole corpus). Every real improvement raises the
bar; a drop below it is a regression; the worst offending cases are quarantined (WAV +
reference MIDI) for promotion to `fixtures/regressions/polyphonic/` — a subdirectory the mono
regression scan skips, replayed by `PolyphonicRegressionCorpusTests` — so the suite only gets harder.
This earns the polyphonic engine a guarantee of the same *kind* as the monophonic loop's,
but ordinal (a statistical F1 bar) rather than exact — the two are ranked, never flattened.

---

## Qualitative smoke set

A small set (~6–10) of short, *varied* clips — a chromatic run, a chord progression, an
arpeggio, a two-hand pattern, and the like — for a by-ear "is the tool obviously broken?"
check across input styles. **It carries no F1 and never will:** it has no ground truth,
and attaching a number to one clip is the anti-pattern the re-baseline retired. It is a
sanity check, not a metric.

*Status:* the numeric corpora above are committed and are the sole basis for every
reported number. Assembling the committed qualitative clips is a tracked, non-blocking
Stage 0 follow-up (it gates nothing — no correctness claim rests on it).

---

## The guarantee hierarchy (ranked, never flattened)

The three engines' guarantees are different in *kind* and must not be reported as a
uniform "proven":

| Engine | Guarantee | Corpus |
|---|---|---|
| Monophonic (YIN) | **Bit-exact closed-loop recovery** — exact count/pitch/onset, duration where audible | Corpus 1 |
| Polyphonic (Basic Pitch) | **Statistical** — corpus note-level F1 ≥ 0.75 at ±50 ms (measured 79.6 %), gated in CI (Stage 1) | Corpus 2 |
| Transkun-via-ONNX | **Statistical + a ≥ 99 % PyTorch-parity** gate (Stage 4) | Corpus 2 |

Each carries its own number and its own limits; the default (polyphonic) states its
earned status honestly.
