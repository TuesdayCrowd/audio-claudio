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

## Principles (v1, re-affirmed — and sharpened for a neural v2)

1. **Earned, not asserted — and honest about the *kind* of proof.** v1's correctness was ordinal-free
   (exact, or not). v2 is inherently ordinal (F1 thresholds, agreement tolerances). So: **exact-recovery
   where achievable** (the mono closed loop), a **stated statistical threshold over a fixed-seed corpus**
   where not (the neural engines). Every reported number **names its tolerance and its seed**; failures
   ratchet into permanent regression fixtures (the suite only gets harder).
2. **Measure generally — and only as generally as the generator.** The *generated* corpus is the oracle,
   so its **distribution is documented and committed** (registers, simultaneity, tempo/dynamics/
   articulation). A handful of varied real clips are **qualitative smoke only — no F1** (they have no
   ground truth; that was the Death trap). Never one piece.
3. **The non-negotiables hold — with the neural exception recorded.** Integer sample time; no wall-clock
   in the Domain; pitch decisions in cents/MIDI. **Determinism** stays bit-exact for the hand-rolled DSP
   Domain, but for the neural engines it follows the existing render-golden precedent: **reproducible
   note-level output on a pinned runtime, not bit-identical tensors across ARM64/x64** (recorded in
   DECISIONS, as the MeltySynth render already is).
4. **Honest defaults and claims.** Limitations stated plainly; the three engines' guarantees are **ranked,
   never flattened** into a uniform "proven" (see Stage 6).

---

## Stage 0 — Re-baseline & positioning (small, do first)

**Goal:** stop the drift and set a target that isn't one recording.
- Stand up a **general corpus**: the monophonic closed-loop generator + the new polyphonic generator
  (Stage 1), with a **committed, documented distribution** (registers, #simultaneous notes, tempo/dynamic/
  articulation ranges) — this is the only source of *numbers*. Plus ~6–10 short *varied* real/synthetic
  clips as a **qualitative** smoke set (no F1), explicitly **not** Death.
- **Default stays polyphonic** (Cornelius, 2026-07-10 — "piano is two hands"): don't revert to mono and
  re-flip later. Carry the honesty in the label — mark the poly engine **"preview — not yet
  closed-loop-proven"** in `--help`/README until Stage 1 earns it, then drop the label. (Resolves the
  honest-identity concern via transparency, not via a default the user doesn't want, and avoids a
  poly→mono→poly churn.)
- Reset README/DECISIONS to describe v2's target honestly.
**Success:** a green per-engine baseline number from the **generated** corpus (tolerance + seed stated),
and the poly default carries an honest "preview" label.

## Stage 1 — Earn polyphony (the headline deliverable)

**Goal:** convert polyphony from *preview* to an **earned** claim — the single most release-defining item.
- Promote the `PolyphonicClosedLoop` diagnostic into a real **property suite**: generate a random
  *constrained* polyphonic score (chords/voices, bounded range, ≥ eighth notes, rests between onsets) from
  a **fixed seed**, synthesize it (the oracle), transcribe, and score with `evaluate`.
- **This is a statistical threshold gate, not exact recovery** (Basic Pitch is ~80% F1 on clean synth
  chords — an exact count+pitch invariant is impossible to pass). **Pass = corpus F1 ≥ a committed
  threshold** at a stated onset tolerance (start ~0.75, below the measured ~0.80 for noise headroom);
  every real improvement **raises** the bar; a drop below = regression. Persist/quarantine failing scores
  as **permanent regression fixtures**; run in CI on the seeded corpus (so it can't flake).
- State the ceiling from the **synthetic** corpus, never a single real recording.
- **This harness is also how Stage 4 (Transkun) proves accuracy** — one general oracle, every engine
  measured against it.
**Why it improves v1:** it restores the defining property — polyphonic correctness that is *measured*, not
asserted. Only after this is the poly default's "preview" label removed.
**Success:** a passing polyphonic closed-loop suite in CI at a committed F1 threshold + onset tolerance
over a seeded corpus; the "preview" label lifted.

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
not eyeballed on one piece. Each lever states a **baseline → target** number, not "improving."
- **Dynamics** — DONE (`DynamicMarks` + grand-staff writer). Generalize and test across the corpus.
- **Sustain-pedal marks** from CC64 — DONE (`GrandStaffMusicXmlWriter` emits `<pedal line="yes">`;
  `MidiFileReader.PedalChanges`). Generalize and test.
- **Auto-tempo in `notate`** — DONE (`TempoEstimator`, median IOI). Fold into corpus rhythm tests.
- **Rhythm / note-values:** cleaner quantization (rests, dotted values, basic tuplets), judged against
  generated ground truth (capture today's baseline; state a target).
- **Voicing:** proper *temporal hand-tracking* treble/bass split (not the fixed middle-C cut). Test
  against **both** engines' output so it isn't tuned to one.
- **Key detection:** auto key signature from pitch content.
- **Close R11.2:** the outstanding **human** MuseScore-load verification (no MuseScore in CI — a human
  gate that can block ship; schedule it).
**Success:** measured note-value + key accuracy on generated scores (baseline → target); a recorded
MuseScore pass.

## Stage 4 — Transkun engine, self-contained via ONNX (the notation-fidelity option)

**Goal:** a third `ITranscriber` — Yujia-Yan's **Transkun** (Neural Semi-CRF, 0.984 MAESTRO, MIT) — running
**entirely in-process, no Python/torch at runtime**, behind the existing port. Its win is *notation
fidelity*: real key-press **durations**, native **velocity**, and native **sustain/soft pedal** → visibly
cleaner engravings than Basic Pitch for any piano input. Held to this plan's discipline: **accuracy measured
on the Stage-1 general corpus**, and **selectable, not the default, until earned**. Transkun's decode is a
custom semi-CRF (not ONNX-exportable), so the transformer is exported and the mel front end + Viterbi decode
are reimplemented in C#.

- **4a — ONNX export gate. ✅ DONE (2026-07-10).** Boundary = `featuresBatch → S` (`torch.fft.rfft`
  unexportable → mel moves to C#). Inlined the scorer to replace the one unexportable op (`diag_embed`)
  with a bit-exact `eye`-multiply (maxΔ=0). Result: a **55.5 MB opset-17 ONNX** in the same
  `Microsoft.ML.OnnxRuntime` we ship, `corr=1.0000`, relErr ~2e-5, 99.99% argmax-pitch agreement. Frozen
  artifacts extracted (`freq2mels` [2049×229] + 6 `windows` [6×4096]; the 90-symbol map; params) + TDD
  fixtures for 4b/4c. Finds: `S` [nStep,nStep,90] (diag=activation, off-diag[e,b]=note e→b), `S_skip`≡0,
  and `targetMIDIPitch[:2] = [-64,-67]` = **sustain + soft pedal as two tracks → pedal for free**. Spike:
  `$CLAUDE_JOB_DIR/tmp/tk_export_spike.py`, `.../tk_export_artifacts.py`.
- **4b — Mel front end in C#** (Infrastructure): framing (4096/1024) → per-segment mean/std normalize → 6
  windows → `Radix2Fft` (ortho) → |·|² → `freq2mels` → log-norm. **Verify vs the `ref3b` fixture**, TDD.
- **4c — Semi-CRF Viterbi decode in C#** (Domain, BCL-only): port `viterbiBackward` (~90 lines, the
  inference path only). **Verify intervals exactly vs the `ref3c` fixture**, TDD.
- **4d — `TranskunTranscriber : ITranscriber`** (Infrastructure): mel → ONNX → Viterbi → `NoteEvent`s
  (frame-resolution ~23 ms) + pedal tracks → `SustainPedal.Change`. **Segment stitching** (the 16 s / 8 s
  overlap merge) gets its **own test: a note spanning a segment boundary is recovered once — not split or
  duplicated.** Wire `transcribe --model transkun`. **Also measure runtime** (`S` is ~172 MB/segment —
  state throughput, don't just assert it works).
- **4d-parity — PyTorch ≡ ONNX gate (Cornelius flagged this as important).** The decisive test that
  **isolates a port bug from model accuracy**: run the native `transkun` CLI (PyTorch) and our C# engine on
  the same clips and require **≥99% note-level agreement (F1)**. Not bit-identity — cross-runtime float
  drift (C# FFT vs `torch`, ORT vs torch) makes that impossible; the honest bar is **note-identical modulo
  rare near-tie argmax flips**, which the 99.99% tensor-argmax agreement says is reachable. Any real
  disagreement is investigated, not tolerated. **This gate must pass before 4d is "done."**
- **4d-offramp — kill criteria.** If parity can't reach the bar, or runtime is unacceptable, **fall back**
  (subprocess adapter, or drop the engine) rather than shipping a subtly-wrong port. Stated up front, like
  4a was a gate.
- **4e — Fidelity (decision, Cornelius): velocity + sub-frame onset/offset.** Core-first (4d: real
  durations + pedal, frame-resolution) vs full-fidelity (add the `velocityPredictor` + `refinedOFPredictor`
  MLP heads as two small ONNX models). Recommend **core-first for the first landing**, add the heads as a
  measured increment. Deferred until 4d + 4d-parity land.
- **4f — Publish the Transkun-ONNX artifact (the "Transkun using ONNX" release).** Commit the ONNX + frozen
  buffers + MIT license + 90-symbol map under `fixtures/models/transkun/` (~57 MB — Basic Pitch precedent;
  note whether Git LFS is warranted). Then a **HuggingFace release** as a *complete package*, never a bare
  `.onnx`: ONNX + buffers + a documented decode spec + a reference decoder (our C#, or a small numpy port).
  Credit Yujia-Yan, link upstream, label it a *transformer-only export + decode spec*. Best done once
  4d-parity has validated the boundary end-to-end. **Outward-facing → confirm with Cornelius before
  pushing.**
**Success:** `transcribe --model transkun` runs self-contained (no Python), **passes the PyTorch≡ONNX
parity gate (≥99%)**, has a corpus-measured accuracy number and a stated runtime, and demonstrably cleaner
notation; the ONNX artifact committed (and, on approval, published) with license + attribution + a usable
decode spec.

## Stage 5 — Robustness, UX, packaging

**Goal:** a tool, not a `dotnet run` demo.
- Ship a packaged **`claudio`** executable (self-contained per platform).
- Live-view polish; clearer CLI help and error messages; determinism + **cross-platform (macOS + Windows)**
  validation — including the two native ONNX/PortAudio deps (macOS is primary; Windows validation is a real
  cost — budget it).
**Success:** `claudio <cmd>` runs standalone on macOS and Windows.

## Stage 6 — Ship v2.0.0 honestly

**Goal:** an earned release.
- Headline claims backed by the **general-corpus** numbers and the polyphonic closed loop — not Death.
- **State the guarantee *hierarchy*, ranked (not flattened):** monophonic = **bit-exact closed-loop
  recovery** (strongest); Basic Pitch poly = **statistical F1 bar** over the seeded corpus (Stage 1);
  Transkun-via-ONNX = **statistical + a ≥99% PyTorch-parity** guarantee. Each with its role, its number,
  and its limits; the default (poly) states its earned status.
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
