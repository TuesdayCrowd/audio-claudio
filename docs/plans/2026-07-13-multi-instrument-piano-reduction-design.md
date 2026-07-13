# Multi-instrument → solo-piano reduction — design

**Status:** Draft for Cornelius's review. Not started. No code written.
**Author:** Claude Code (autonomous brainstorm, 2026-07-13), grounded by three parallel
research streams (separation-model survey, codebase grounding, arrangement-algorithm
research) and an architecture/honesty roundtable.
**Motivation (Cornelius):** hear a jazz recording, reproduce it as a solo piano piece.

---

## 0. TL;DR

What sounds like one feature — "hear the band, print the piano part" — is **three
different problems stacked**, and they land in completely different places on this
project's honesty ladder:

| # | Problem | Falsifiable? | Guarantee tier |
|---|---------|--------------|----------------|
| 1 | **Source separation** (mix → per-instrument stems) | **Yes** — has a closed-loop SI-SDR oracle | *Statistical*, new CORPUS.md tier |
| 2 | **Per-stem transcription** (stem → notes) | **Yes** — reuses the existing F1 harness | *Statistical*, its own compounded gate |
| 3 | **Piano reduction / arrangement** (N instruments → 2 hands) | **No** — no ground truth, ever | **Unranked, by design** |

The recommendation, which the roundtable reached unanimously, is: **build 1–2–3 (the
falsifiable half) as a real, gated, shippable capability that stops at a multi-track
MIDI; build the arrangement (the unfalsifiable half) only as an explicitly separate,
permanently-unranked artifact behind its own verb — and never silently chain the
unranked step onto the ranked ones so the whole pipeline reads as "proven" when only
two-thirds of it is.**

This document is honest about the one hard truth up front: **errors compound
multiplicatively down this chain.** Separation is lossy → per-stem transcription is
~80 % F1 → arrangement deliberately *drops* notes. The falsifiable two-thirds alone
already lands near F1 ≈ 0.72 on the project's own existing numbers. The output is a
faithful *sketch* of the tune, not a reproduction. That expectation is set here before
a line of code is written.

---

## 1. Where this fits the architecture

The current pipeline is `audio → ITranscriber → notes → score`. This inserts a
separation stage in front and a reduction stage behind, and **reuses the entire
middle unchanged**:

```
mixed jazz.wav
   │
   ▼
┌ ISourceSeparator ┐            ← NEW port + ONNX adapter (Stage 1)
│ piano │ bass │ drums │ other(horns) │ vocals │
   │      │      │         │            │
   ▼      ▼      ✗          ▼            ▼        (drums → dropped / rhythm accent)
   └──── existing ITranscriber per stem ─────┘   ← REUSED verbatim (Stage 2)
   │
   ▼
per-stem NoteEvent sets, each tagged with a source role
   │
   ├───────────────► multi-track MIDI            ← the HONEST SHIP POINT (Stage 3)
   │
   ▼
┌ PianoArranger (Domain.Reduction) ┐            ← NEW, pure, UNRANKED (Stage 4)
   │  merge → fold → salience → cap → hand-assign → constrain → validate
   ▼
merged NoteEvent list → PolyphonicQuantizer.Quantize → GrandStaffScore
   │
   ▼
existing GrandStaffMusicXmlWriter / GrandStaffFlattener   ← REUSED
```

The hexagonal boundaries make this clean, and the grounding pass confirmed every
reuse point exactly:

- **`ISourceSeparator` is a new Application port** beside `ITranscriber`
  (`AudioClaudio.Application.Ports`). Shape: `IReadOnlyList<SeparatedStem> Separate(IAudioSource mix)`
  where `SeparatedStem = (string Name, IAudioSource Audio)`. Because each stem is just
  another `IAudioSource`, the existing `ITranscriber.Transcribe(IAudioSource) ->
  TranscriptionResult(Score, IReadOnlyList<NoteEvent> RawEvents)` runs on it **with no
  change to the transcriber port at all.** Nothing like `ISourceSeparator` exists today
  (confirmed by repo-wide grep).

- **The ONNX adapter follows the exact Basic-Pitch / Transkun template.** A
  `SourceSeparatorModel : IDisposable` owns an `InferenceSession`; a
  `<Name>SourceSeparator : ISourceSeparator, IDisposable` does
  `Framing.ReconstructMono(frames)` → `AudioResampler.Resample(pcm, inRate, modelRate)`
  → run (windowed + stitched if the export requires it) → wrap each stem's PCM back into
  an `IAudioSource`. Model file located by a new `SourceSeparatorModelLocator.Resolve(...)`
  walking up from `AppContext.BaseDirectory` to `fixtures/models/<name>/`, mirroring
  `ModelLocator` / `TranskunModelLocator`.

- **The arranger writes into the existing grand-staff pipeline.** It produces one merged
  `IReadOnlyList<NoteEvent>` and feeds
  `PolyphonicQuantizer.Quantize(events, grid, chordWindow) -> GrandStaffScore` →
  `GrandStaffMusicXmlWriter` / `GrandStaffFlattener` **unchanged**. The real splitter in
  use is `HandSplitter` (EMA-tracked hand centres), not the older fixed-middle-C
  `StaffSplitter`.

- **`NoteEvent` is not touched.** It carries exactly `Pitch, Onset, Duration, Velocity`
  — no channel/instrument/voice field. The arranger's need to remember "which stem did
  this note come from" lives in a wrapper `TaggedNote(NoteEvent Note, SourceRole Role,
  string SourceId)` used only inside `Domain.Reduction`, keeping the domain's "Money
  step" primitive and its whole blast radius of tests/writers/synth untouched.

- **CLI**: new commands register in `AppBuilder.Build` via the hand-rolled kernel
  (`new CliCommand("separate", …).WithArgument(…).WithOption(…)`; `app.Register(cmd,
  (p, stdout, stderr) => …)`), exactly like `transcribe`/`listen`.

---

## 2. The guarantee framing (the crux — read this before the stages)

This is where the feature either respects the project's ethos or quietly violates it.
The roundtable's synthesis, adopted here:

1. **Two halves that must never merge in framing or in code path.**
   - The **falsifiable half** (Stages 1–3: separate → transcribe → multi-track MIDI)
     earns real, CI-gated, ranked guarantees and ships as a genuine new capability.
   - The **unfalsifiable half** (Stage 4: reduction to solo piano) is gated *only* by
     structural playability invariants and content-preservation proxies. It earns **no
     accuracy number, ever.**

2. **Never silently chain the unranked step onto the ranked ones.** The single biggest
   honesty risk here is not that arrangement lacks ground truth — this project has
   shipped honestly-labeled ungated things before (the live-poly prototype). The risk is
   a pipeline that *reads* as gated end-to-end when only its first two-thirds are. The
   mitigations are **structural, not just documentation**:
   - Arrangement gets its **own verb** (`arrange`), never a default and never a flag
     riding along on `transcribe`. (The `--mono` / poly-default precedent argues hard
     against ever defaulting a user into an unranked path.)
   - **Separate CI gates per lossy stage.** Separation SI-SDR is one gate; the compounded
     end-to-end F1 is a *different* suite from the existing clean-input
     `PolyphonicClosedLoopTests`, so neither number contaminates the other.
   - **Every intermediate artifact is persisted** — the separated stems, the per-stem
     MIDI, and the merged multi-track MIDI — so a user who dislikes the reduction can
     fall back to the guaranteed artifact and arrange it themselves.

3. **Arrangement's human gate is permanent — it never retires.** Every other human gate
   this project has (e.g. R11.2, the MuseScore load check) is a *temporary* stand-in that
   is satisfied once and frozen as a golden. Arrangement quality is subjective *per
   output, forever*; there is no golden to converge to. This must be stated explicitly
   wherever arrangement ships, or a future maintainer will mistake it for "R11.2-style
   deferred work" rather than "permanently outside the falsifiability game by design."

4. **Arrangement lives in its own doc, not `CORPUS.md`.** `docs/CORPUS.md` is reserved
   for falsifiable, CI-gated numbers. Arrangement gets `docs/ARRANGEMENT.md`, which states
   plainly that there is no accuracy number here, by design, and why. Any figure the
   arranger ever shows a user is labeled a **structural-validity** score, never a
   probability of musical correctness.

---

## 3. The staged plan

Five stages, in strict dependency order — each proven before the next builds on it, the
same discipline as the numbered build steps. **Stage 3 is a complete, honest,
independently-useful ship point;** Stages 4–5 are optional and explicitly unranked.

### Stage 1 — Source separation + its own closed-loop oracle

**Goal.** An `ISourceSeparator` port and one ONNX adapter that splits a mix into labeled
stems, plus a `separate <mix.wav>` command that writes the stems as WAVs.

**Separator model — a genuine, verified tension (the survey's load-bearing finding).**
The project's two hardest constraints — *permissively-licensed weights we can commit* and
*an explicit piano stem* — are in direct conflict, and the survey verified the licenses at
the source (the maintainers' own statements, via the GitHub API):

- **Demucs `htdemucs_6s` is a non-starter, despite being the only model with a real piano
  stem.** Its code is MIT but its **weights are research-only** — the maintainer states
  verbatim that the model weights "are not covered by the MIT license, and are provided
  only for scientific purposes" (facebookresearch/demucs #327), because every checkpoint
  is MUSDB18-trained and MUSDB18 is academic-use-only (CC-BY-NC-SA components). Third-party
  HuggingFace mirrors that tag these weights "mit" contradict the author and are wrong.
  This kills the obvious choice — the one thing that would have made "reproduce as piano"
  most natural.
- **Primary recommendation: Spleeter 5-stem (Deezer).** It is the *only* license-clean
  option that emits an explicit **piano** and **bass** stem — code **and pretrained
  weights are MIT** (Deezer's JOSS paper states the pretrained models are MIT, and they
  were trained on Deezer's *private* catalog, so they carry none of the MUSDB
  restriction). Its stems `vocals/drums/bass/piano/other` map straight onto the goal:
  piano → both hands, bass → left hand, horns live in "other", drop drums — and a jazz
  piano trio maps especially cleanly. **Honest costs, accepted eyes-open:** it is an older
  (2019), weaker U-Net; its **piano stem was never benchmarked** (piano isolation is the
  hardest case in every model, and both Deezer and Demucs's authors flag it as the
  weakest); it has an ~11 kHz spectral ceiling; and only the *2-stem* ONNX export exists
  publicly, so **the 4/5-stem TF→ONNX export is real work we would own** (port the
  Apache-2.0 sherpa-onnx recipe — *not* the GPL-3.0 `spleeter-pytorch-mnn`).
- **Fallbacks if the piano stem proves unusable** (both MIT code + MIT weights, cleaner to
  integrate, but **no piano stem** — piano folds into "other" with the horns): **Open-Unmix
  UMX-HQ** — its BiLSTM core exports to ONNX with the STFT *outside* the graph, the exact
  split this repo already ships for Transkun; commit just the reliable *bass* + *other*
  stems; modest ~5–6 dB quality. Or **SCNet** — MIT official checkpoints, ~40 MB,
  **8.8–10.1 dB SDR (Demucs-class, cleanly licensed)**, but 4-stem and a `.ckpt`→ONNX
  conversion we would do (its real-valued backbone exports far more easily than Demucs).

Two facts de-risk the ONNX concern: the "keep STFT/iSTFT outside the exported graph"
pattern that makes these models exportable is **exactly** what this repo already does for
Transkun, and CPU-only inference on a few-minute track runs faster-than- to
slightly-over real-time (a non-issue for an offline command). Two caveats go into
`DECISIONS.md` per §1.7: **no surveyed model is validated on jazz** (all published SDR is
MUSDB18 rock/pop), and every "permissive weights" claim rests on the rights-holder's own
grant — standard practice but not court-tested; Spleeter's is the strongest (an explicit
published statement covering the pretrained models). Record the exact license citation
beside whichever model is chosen.

**The oracle (this is the on-brand part).** `MeltySynthSynthesizer` takes a GM program
per instance, so the closed-loop separation test needs **zero synth changes**: render
several committed solo MIDIs each on a *different* GM program (e.g. bass = program 32,
tenor sax = 66, piano = 0), sum the per-instrument PCM buffers into a mix with
**known ground-truth stems**, separate it, and score the recovery by **SI-SDR**. This is
the exact `synthesize→…≈id` trick the polyphonic gate already uses, aimed at the
separator — no hand-labeled data, no copyrighted audio, fully deterministic. It becomes a
new `SeparationClosedLoop` / `SeparationClosedLoopGen` pair mirroring the polyphonic ones,
and earns its own `CORPUS.md` tier.

**Verify.** SI-SDR gate in CI on the synthetic-mix corpus. **Human gate (one-time,
R11.2-style):** does a separated piano stem from a real, rights-cleared mixed recording
sound structurally intact on playback?

### Stage 2 — Per-stem transcription + the compounded closed-loop suite

**Goal.** Route each pitched stem through the existing `ITranscriber` (Basic Pitch or
Transkun), tag each note set with its source role, and measure the **real compounded**
accuracy.

**Verify.** A NEW end-to-end closed-loop suite — synthesize a known multi-instrument
score → render per-instrument → sum → separate → transcribe each recovered stem →
`TranscriptionEvaluator.Evaluate` against the known per-stem note-sets — gated as its own
CI suite, **kept separate** from the clean-input polyphonic gate. This reports the honest
compounded number (the ~0.72 chain estimate is illustrative; the gate reports whatever is
actually measured). Drums are handled by a separate percussion path or dropped; that
choice is a decision gate (§5).

### Stage 3 — Multi-track MIDI merge — **the honest ship point**

**Goal.** Combine the gated per-stem transcriptions into one **multi-track MIDI** (one
track per stem, each track inheriting Stage 2's guarantee). Fully falsifiable, and
independently useful: openable in any DAW or notation editor.

**Why stop here is a real option.** A recovered multi-track MIDI already lets a user (or
an external tool) own the reduction step themselves. If arrangement never ships, this is
still a complete, honest feature: "recover a rough multi-instrument transcription from a
mixed recording." Building elaborate reduction heuristics tuned to *today's* noisy
per-stem transcriptions also forecloses cheaply swapping in a better separation model
later — so stopping here maximizes optionality.

**Verify.** Each track inherits Stage 2's gate. **Human gate:** a musician recognizes the
tune's parts on playback through the existing `play`/`render` path.

### Stage 4 — Arrangement prototype — explicitly unranked, its own verb

**Goal.** A deterministic, pure-`Domain.Reduction` reduction of the merged tagged notes
into a playable solo-piano `GrandStaffScore`, behind an `arrange` verb (explicit opt-in,
never default). Full algorithm in §4.

**Verify.** Structural playability invariants + content-preservation proxies only (§4.3)
— property tests in the same "the suite only gets harder" culture as the closed-loop and
bar-conservation suites. **No accuracy number.** **Human gate:** subjective, ongoing, and
— uniquely in this project — **never retires** (§2.3). Documented in `docs/ARRANGEMENT.md`.

### Stage 5 — (Optional, deferred) Multiple arrangement styles

Turn the unfalsifiability into a *stated feature*: offer several user-selectable reduction
styles (e.g. lead-sheet-sparse vs. full-voicing, difficulty-tiered) rather than one
"best" output. Only after Stage 4 exists and there's appetite. Named, not silently
dropped.

---

## 4. The arrangement algorithm (Stage 4 detail)

Grounded in the reduction literature. The lineage to follow is the **rule/DP-based**
systems (Chiu & Chen 2009; the entropy-salience work; the shortest-path melody-reduction
paper, arXiv:2508.01571) — all hand-rollable and deterministic. The statistical/HMM
lineage (Nakamura & Sagayama 2015; Nakamura & Yoshii, arXiv:1808.05006) contributes its
DP *skeleton* and its two most-cited concrete constants — **≤ 5 notes/hand** and **span
< 14 semitones/hand** — which its own authors flag as *necessary but not sufficient* for
playability (a caveat we carry forward honestly). Everything downstream of a trained net
(Pop2Piano, PiCoGen, AccoMontage's style-transfer, the 2025 BERT paper) is out of scope
for a project that prizes hand-rolled deterministic domain code.

### 4.1 Types

```csharp
namespace AudioClaudio.Domain.Reduction;

enum SourceRole { Melody, Bass, Harmony, Percussion }
readonly record struct TaggedNote(NoteEvent Note, SourceRole Role, string SourceId);
```

Pure functions only — no I/O, no wall clock, no randomness (same non-negotiables as the
rest of the domain).

### 4.2 Pipeline (each stage an independently-testable pure function)

0. **Merge & tag** — flatten all sources into one stream in a fixed total order
   `(onset, −pitch, sourceId)`, so every later "in order" pass is reproducible bit-for-bit.

1. **Octave-fold first** *(moved ahead of salience deliberately)* — fold every pitch into
   the 88-key range (±12 until `21 ≤ midi ≤ 108`). Rationale: salience and hand-assignment
   both reason about *register* ("is this the top sounding note?"), so an un-folded piccolo
   or sub-bass would poison every later register decision. Also compute a piecewise
   minimum-adjacent-gap (wider in the bass — encodes the orchestration rule that low
   close intervals turn to mud) for the Stage-6 anti-mud check.

2. **Salience scoring** — the heart of it. `double Salience(TaggedNote, SimultaneityContext)`
   from independently-testable terms: **role weight** (melody > bass > harmony >
   percussion≈0); **outer-voice bonus** (+ for the highest or lowest sounding pitch — the
   universal "keep melody + bass + the defining third, drop doublings and the fifth"
   rule); **metric weight** (downbeats score higher, against the *declared* grid — no
   estimation); **duration/velocity**; and a **duplicate-pitch-class penalty** so a tutti
   chord doesn't survive as "all of them." Ties broken by a fixed lexicographic key so no
   float-equality compare is ever needed.

3. **Simultaneity cluster + cap** — sweep-line over integer sample overlap to form
   simultaneities, then top-K by salience, **floor-protecting the single highest-salience
   melody note and bass note** so the outer voices are never dropped. MVP = greedy
   per-simultaneity top-K; a documented Phase-2 refinement is a DP across adjacent
   simultaneities that penalizes dropping a sustained voice mid-phrase (the shortest-path
   paper's actual shape).

4. **Hand assignment** — reuse `HandSplitter` for the default register split. **Open
   decision (Cornelius owns, §5):** does `HandSplitter`/`StaffSplitter` gain an optional
   `roleHints` parameter so a melodic dip below middle C stays in the right hand, or does
   reduction sit as a separate pre-pass with its own simpler split, leaving the
   golden-covered splitter untouched? The second is lower-risk.

5. **Playability constraints** — pure validators/adjusters: same-hand **unison merge**;
   **span clamp** to ≤ 14 semitones (drop least-salient *inner* notes, never the outer
   voices); **count clamp** to ≤ 5/hand; **minimum-duration floor** (reuse the ~50 ms
   `NoteSegmenter` convention); **bass anti-mud** spacing from step 1.

6. **Validate (assert-only)** — `ReductionReport Validate(GrandStaffScore, source)` checks
   every invariant below and is itself the CI gate.

### 4.3 The testable invariants (falsifiable — no ear required)

1. 88-key range on every output note. 2. ≤ 5 notes/hand per simultaneity. 3. span ≤ 14
semitones/hand *(documented as necessary-not-sufficient)*. 4. no zero/sub-50 ms durations.
5. no same-hand pitch-unison overlap. 6. bass anti-mud gap respected. 7. **melody-note
retention ≥ X %** (via the existing onset-tolerant `TranscriptionEvaluator` matching).
8. **bass-onset retention ≥ Y %**. 9. **harmonic pitch-class coverage** ≥ a fraction of
source chord tones per simultaneity (pure set arithmetic). 10. determinism (same input →
same output, run twice and diff). 11. `Validate` on the pipeline's own output reports zero
violations (idempotence-adjacent). 12. percussion never exceeds its declared accent cap.

Every one is a CsCheck property + CI gate. The retention thresholds (X, Y) are *policy
choices* picked once measured on a seed corpus — exactly how the 79.6 % polyphonic F1 bar
was set — not research facts.

### 4.4 Honest evaluation

The invariants test **"did we obviously mangle the input"** (structural soundness), never
**"is this a good arrangement"** (musical quality). No proxy is invented to paper over the
gap — even the best statistical systems in the literature fall back on professional
arrangers rating outputs on 1–10 scales, because no metric was trusted to stand in for
that judgment. Call the gated tier **structurally sound**, never **musically good**. The
`evaluate` harness may be *extended* with the retention/coverage metrics, but they stay
explicitly labeled fidelity/playability proxies — never rebranded an arrangement-quality
score.

---

## 5. Decision gates Cornelius owns

Per `CLAUDE.md` §1 rule 2, these are presented, not silently resolved:

1. **Scope / stop point.** Ship the falsifiable half (Stages 1–3, multi-track MIDI) and
   stop? Or commit to the arrangement (Stage 4)? *Recommendation: build 1–3 first; treat
   4 as a separate, later, opt-in prototype.*
2. **Separator model.** The survey found a real tension: the only model with a piano stem
   (Demucs `htdemucs_6s`) has **research-only weights** (unusable here); the only
   license-clean piano+bass option is **Spleeter 5-stem** (older/weaker, and we would own
   its 4/5-stem ONNX export); the cleaner-to-integrate or higher-quality options
   (Open-Unmix UMX-HQ, SCNet) have **no piano stem**. So the choice is a genuine trade —
   *piano-stem-but-weak + export work* (Spleeter) vs. *no-piano-but-cleaner/better*
   (UMX-HQ / SCNet). *Leaning Spleeter 5-stem for the piano+bass fit, accepting the export
   effort; UMX-HQ as the low-risk fallback.*
3. **Drums.** Drop entirely, or reduce to left-hand rhythmic accents?
4. **Reduction aggressiveness.** Literal-but-unplayable vs. simplified-but-idiomatic (and
   later, Stage 5's multiple styles).
5. **Hand-assignment API.** Extend `HandSplitter` with role hints, or a separate reduction
   pre-pass that leaves it untouched? *Recommendation: separate pre-pass (lower risk to
   existing goldens).*

---

## 6. Honest limitations & risks (stated up front)

- **Error compounding is real and multiplicative.** Separation (lossy) × per-stem
  transcription (~80 % F1) × arrangement (drops notes by design). The falsifiable
  two-thirds alone ≈ F1 0.72 on the project's own numbers. The result is a **sketch**, not
  a reproduction. Each lossy stage is measured by its own gate so the compounding is
  *visible*, not hidden.
- **Arrangement is permanently unfalsifiable** and its human gate never retires (§2.3).
- **Separator feasibility is a real, now-scoped constraint — not a formality.** The
  best-fit model (the one with a piano stem) is license-blocked; the license-clean piano
  option (Spleeter) is older, weaker, jazz-untested, and needs a 4/5-stem ONNX export we
  would own; the cleanest integrations (UMX-HQ / SCNet) drop the piano stem. This
  strengthens the case to *prove* Stage 1's separation quality (the SI-SDR gate + the
  one-time real-jazz human check) before investing anything downstream. A separation model
  is also a large committed artifact that must clear the weight-license bar with a recorded
  citation.
- **Offline / batch only.** This is a file-first capability (fits the discipline); nothing
  here is a live path.
- **Not single-piece chasing.** All accuracy claims come from synthetic closed-loop
  corpora, never from matching one copyrighted recording (the retired *Death*-chroma-chase
  lesson).

---

## 7. What this deliberately does NOT do (YAGNI)

No trained arrangement model. No fine-grained instrument *recognition* (separation gives
role-labeled stems — that's all the piano goal needs; "which horn is it" is a different,
unneeded classifier). No live/real-time path. No re-training of the separator. No blending
of separation loss into the existing polyphonic F1 number. No default invocation of the
unranked arrangement path.

---

## 8. Next steps

1. Cornelius resolves the §5 decision gates — especially the scope/stop-point and the
   separator-model trade (Spleeter-with-piano vs. UMX-HQ/SCNet-without).
2. On approval, write the Stage-1-only TDD implementation plan (`docs/plans/`) — the
   `ISourceSeparator` port, the ONNX adapter (including the 4/5-stem ONNX export if
   Spleeter is chosen), and the SI-SDR closed-loop oracle — and stop there for review
   before Stage 2, per the one-step-at-a-time rule.
3. Record the chosen model's exact weight-license citation in `DECISIONS.md` (§1.7).
