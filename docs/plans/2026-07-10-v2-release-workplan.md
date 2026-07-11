# audio-claudio v2 — release workplan

## Why this exists (the refocus)

v0.2.0 drifted into replicating **one** piece — the *Death* piano recording — judged by **one**
metric: chroma similarity to that real recording. Both were dead ends by construction. Chroma is
timbre-limited (a SoundFont render compared to a real piano — or to buzzy chiptune square waves — caps
around ~78% *regardless of note accuracy*), and optimizing a single out-of-distribution recording is not
a quality signal for the tool. The clearest proof: a clean chiptune source scored the *same* ~78% as the
messy piano, and a SOTA piano model (Transkun) did *not* beat the general engine on that audio.

Worse, polyphony shipped as "a capable preview" with **no correctness guarantee** — a regression from the
one thing that defined v1: a correctness claim *earned* by the closed loop, not asserted.

**v2's job:** realize the Phase-2 vision (`CLAUDE.md` §8) with v1's discipline — every new capability
*proven* on a general synthetic corpus, with the honest claim as the headline, and no more single-piece
chasing.

**One capability is added to that vision (Cornelius, 2026-07-10):** a **self-contained Transkun engine
via ONNX** (Stage 4). Its value is *notation fidelity*, not raw accuracy — and it earns its place under
the same rules as everything else here (measured on the general corpus; not the default until proven).
See Stage 4 and the reconciled "out of scope" note for why the earlier "skip Transkun" call was revised.

## Principles (v1, re-affirmed)

1. **Earned, not asserted.** Every capability is backed by a closed-loop / property suite over generated
   ground truth — the synthesizer is the oracle, so no hand-labelled data is needed.
2. **Measure generally.** A corpus of varied *generated* cases plus a few varied real clips — never one
   piece.
3. **The non-negotiables hold.** Integer sample time; no wall-clock in the Domain; determinism; pitch
   decisions in cents/MIDI.
4. **Honest defaults and claims.** The *proven* path is what a new user hits; limitations stated plainly.

---

## Stage 0 — Re-baseline & positioning (small, do first)

**Goal:** stop the drift and set a target that isn't one recording.
- Stand up a **general corpus**: the monophonic closed-loop generator + the new polyphonic generator
  (Stage 1), plus ~6–10 short *varied* real/synthetic clips (different registers, tempos, textures) as a
  smoke set — explicitly **not** Death.
- Recommend reverting the poly-default: the **monophonic (proven) engine is the default** until polyphony
  is earned (Stage 1); revisit the default afterward. (A "preview" default contradicts the honest identity.)
- Reset README/DECISIONS to describe v2's target honestly.
**Success:** a green per-engine baseline number tied to the corpus, not to Death.

## Stage 1 — Earn polyphony (the headline deliverable)

**Goal:** convert polyphony from *preview* to an **earned** claim — the single most release-defining item.
- Promote the existing `PolyphonicClosedLoop` diagnostic into a real **property suite**: generate a random
  *constrained* polyphonic score (chords/voices, bounded range, ≥ eighth notes, rests between onsets),
  synthesize it (the oracle), transcribe, and require **count + pitch + onset** back within a stated
  tolerance — the Step-9 discipline extended to chords. Persist/quarantine failures; run in CI.
- State the ceiling from the **synthetic** corpus (the ~80% F1 already measured is the honest starting
  point), never from a single real recording.
- **This harness is also how Stage 4 (Transkun) proves its accuracy** — one general oracle, every engine
  measured against it.
**Why it improves v1:** it restores the defining property — a polyphonic transcriber whose correctness is
*proven*. Only after this can polyphony be defaulted without dishonesty.
**Success:** a passing polyphonic closed-loop suite in CI with an honest tolerance.

## Stage 2 — Core detection quality (§8 upgrades, closed-loop-validated)

**Goal:** raise the floor of both engines.
- **pYIN** monophonic upgrade (probabilistic YIN): eliminate the rare ~0.4% octave/onset residual, add
  real confidence, widen the reliable range. Behind the detector seam; the mono closed loop must stay green.
- **Velocity from onset energy** (the mono path emits a constant today): real per-note dynamics that feed
  the notation.
- **Onset / segmentation:** legato handling and a coarser-grid note-off so uneven playing yields cleaner
  rhythm.
**Success:** mono closed loop green at a tighter residual; velocity is non-constant and validated.

## Stage 3 — Notation quality (general, corpus-measured)

**Goal:** a score you would actually read — measured on generated scores whose true rhythm/key are known,
not eyeballed on one piece.
- **Dynamics** — DONE (`DynamicMarks` + grand-staff writer). Generalize and test across the corpus.
- **Sustain-pedal marks** from CC64 — DONE (`GrandStaffMusicXmlWriter` emits `<pedal line="yes">`;
  `MidiFileReader.PedalChanges`). Generalize and test.
- **Auto-tempo in `notate`** — DONE (`TempoEstimator`, median IOI). Fold into corpus rhythm tests.
- **Rhythm / note-values:** cleaner quantization (rests, dotted values, basic tuplets), each judged
  against generated ground truth.
- **Voicing:** proper *temporal hand-tracking* treble/bass split (not the fixed middle-C cut). (The
  fixed cut is adequate for wide-register material but wrong for crossing hands — a general fix, tested.)
- **Key detection:** auto key signature from pitch content.
- **Close R11.2:** the outstanding human MuseScore-load verification of the MusicXML.
**Success:** measured note-value + key accuracy on generated scores, improving; a recorded MuseScore pass.

## Stage 4 — Transkun engine, self-contained via ONNX (the notation-fidelity option)

**Goal:** a third `ITranscriber` — Yujia-Yan's **Transkun** (Neural Semi-CRF, 0.984 MAESTRO, MIT) — running
**entirely in-process, no Python/torch at runtime**, behind the existing port. Its win is *notation
fidelity*: real key-press **durations**, native **velocity**, and native **sustain/soft pedal** (all three
decoded natively) → visibly cleaner engravings than Basic Pitch for any piano input. Held to this plan's
discipline: **accuracy measured on the Stage-1 general corpus**, and **selectable, not the default, until
earned**. Transkun's decode is a custom semi-CRF (not ONNX-exportable), so the transformer is exported and
the mel front end + Viterbi decode are reimplemented in C# — a boundary the gate below has now proven.

- **4a — ONNX export gate. ✅ DONE (2026-07-10).** Boundary = `featuresBatch → S` (the mel front end uses
  `torch.fft.rfft`, unexportable → moves to C#). Inlined the scorer to replace the one unexportable op
  (`diag_embed`) with a bit-exact `eye`-multiply (maxΔ=0 vs original). Result: a **55.5 MB opset-17 ONNX**
  running in the same `Microsoft.ML.OnnxRuntime` we already ship, `corr=1.0000`, relErr ~2e-5 (float drift
  only), 99.99% argmax-pitch agreement. **Frozen artifacts extracted** (mel buffers `freq2mels` [2049×229]
  + 6 `windows` [6×4096]; the 90-symbol map; params) and TDD reference fixtures for 4b/4c. **Key finds:**
  `S` [nStep,nStep,90] (diagonal=activation, off-diag[e,b]=note e→b), `S_skip`≡0 ("dummy eps"), and
  `targetMIDIPitch[:2] = [-64,-67]` = **sustain + soft pedal decoded as two tracks → pedal for free**,
  feeding the Stage-3 pedal marks directly. Spike + artifacts: `$CLAUDE_JOB_DIR/tmp/tk_export_spike.py`,
  `.../tk_export_artifacts.py`.
- **4b — Mel front end in C#** (Infrastructure): framing (4096 window / 1024 hop) → per-segment
  mean/std normalize → 6 windows → `Radix2Fft` (ortho) → |·|² → `freq2mels` → log-norm `((m+eps).log()−
  log eps)/(−log eps)`. **Verify** numerically against the `ref3b` fixture (frames→features). TDD.
- **4c — Semi-CRF Viterbi decode in C#** (Domain, BCL-only): port `viterbiBackward` (~90 lines) — the
  inference path only; the forward-backward/logZ/autograd machinery is training-only and skipped.
  **Verify** intervals exactly against the `ref3c` fixture (score→intervals). TDD.
- **4d — `TranskunTranscriber : ITranscriber`** (Infrastructure): mel → ONNX (`S`) → Viterbi → intervals →
  `NoteEvent`s (frame-resolution timing ~23 ms) + pedal tracks → `SustainPedal.Change`; **segment
  stitching** (the 16 s / 8 s overlap merge from `transcribe`). Wire `transcribe --model transkun` (or
  `--transkun`). **Measure on the Stage-1 corpus + `evaluate`** — an honest accuracy number, not a Death
  number. Manual: re-`notate` a real piece and confirm the engraving is cleaner.
- **4e — Fidelity (decision, Cornelius): velocity + sub-frame onset/offset.** Core-first (4d, real
  durations + pedal, frame-resolution) vs full-fidelity (add the `velocityPredictor` + `refinedOFPredictor`
  MLP heads as two small ONNX models; C# gathers ctx at interval endpoints and runs them). Recommend
  **core-first for the first landing**, add the heads as a measured increment. Deferred until 4d lands.
- **4f — Publish the Transkun-ONNX artifact (the "Transkun using ONNX" release).** Commit the ONNX + the
  frozen buffers + the MIT license + the 90-symbol map under `fixtures/models/transkun/` (precedent: the
  committed Basic Pitch model). Then a **HuggingFace release** — but as a *complete package*, never a bare
  `.onnx` (which is unusable without the front end + decode): the ONNX + buffers + a documented decode spec
  + a reference decoder (our C#, or a small numpy port). Credit Yujia-Yan, link the upstream repo, label it
  clearly as a *transformer-only export + decode spec* (a derivative, not the whole model). Best done once
  4c has validated the boundary end-to-end. **Outward-facing → confirm with Cornelius before pushing.**
**Success:** `transcribe --model transkun` produces a self-contained transcription (no Python) with a
corpus-measured accuracy number and demonstrably cleaner notation; the ONNX artifact committed (and,
on approval, published) with license + attribution + a usable decode spec.

## Stage 5 — Robustness, UX, packaging

**Goal:** a tool, not a `dotnet run` demo.
- Ship a packaged **`claudio`** executable (self-contained per platform).
- Live-view polish; clearer CLI help and error messages; determinism + cross-platform (macOS + Windows)
  validation.
**Success:** `claudio <cmd>` runs standalone on macOS and Windows.

## Stage 6 — Ship v2.0.0 honestly

**Goal:** an earned release.
- Headline claims backed by the **general-corpus** numbers and the polyphonic closed loop — not Death.
- **Engines stated honestly:** monophonic (proven, closed-loop), polyphonic Basic Pitch (earned in
  Stage 1), and Transkun-via-ONNX (self-contained, notation-fidelity option, corpus-measured) — each with
  its role and limits; the default is the proven path.
- README / DECISIONS / CONTRACTS reconciled; retire the transient plan docs; tag **v2.0.0**.

---

## Explicitly out of scope (the tangent, closed)

- **Chasing chroma similarity to the *Death* recording.** Timbre-limited; not a quality signal. Keep
  `evaluate-audio` as a general tool, but do not optimize toward one clip's number.
- ~~Integrating a heavier piano-specific model (Transkun).~~ **Revised 2026-07-10 → now Stage 4.** The
  original objection had three parts; two are now obsolete. *"No ONNX"* — false: the transformer exports
  cleanly at opset 17 (gate 4a passed). *"Custom semi-CRF"* — scoped: the inference decode is ~90 lines of
  Viterbi, ported to Domain. *"Doesn't beat the general engine on real audio"* — still true for **raw pitch
  accuracy** (they tie ~78% chroma on Death), which is exactly why Transkun enters as a **notation-fidelity**
  engine (durations/velocity/pedal), measured on the general corpus, not defaulted until earned — not as an
  accuracy silver bullet. The discipline is preserved, not bypassed.

## What carries over from the recent work (keep)

Pedal-honoring `render`/`play`; `evaluate` + `evaluate-audio` + the closed-loop/chroma harnesses; dynamics
and **sustain-pedal marks** in the grand-staff writer; **auto-tempo** and the **`notate`** command; key-aware
enharmonic spelling; and the `MidiReadResult.PedalChanges` seam. These are the durable, general wins —
retained and folded into the stages above (the notation items into Stage 3, pedal-from-decode into Stage 4).
