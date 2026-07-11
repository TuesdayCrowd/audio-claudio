# v2 Stage 4 — Transkun engine, self-contained via ONNX

**Plan:** `docs/plans/2026-07-10-v2-release-workplan.md` §Stage 4. Branch `v2-stage0-rebaseline` (merge held
for the v2.0.0 tag). A third `ITranscriber` (Yujia-Yan **Transkun**, Neural Semi-CRF, MIT) running in-process
via ONNX — its win is **notation fidelity** (real durations, native velocity, native sustain/soft pedal).
**Selectable, not the default, until earned**; accuracy measured on the Stage-1 general corpus. Mono closed
loop stays bit-exact; the poly gate holds.

**Reality (2026-07-11):** the workplan marks 4a "done", but its artifacts (ONNX, frozen buffers, ref3b/ref3c
fixtures, spike scripts) lived in an ephemeral job dir and are **gone**. The transkun venv survives (torch
2.13 / onnx / onnxruntime / transkun + `2.0.pt`). So **4a is re-derived this cycle and its outputs committed
to the repo** (Cornelius, 2026-07-11: commit the ~55 MB ONNX directly to git) so Stage 4 becomes reproducible
from committed code + fixtures like everything else. Batch: **4a→4c** (Cornelius).

## Stage 4a: Regenerate + commit the ONNX export (Python spike)

**Goal**: re-derive the `featuresBatch → S` ONNX export from `2.0.pt`; validate; extract frozen buffers +
TDD fixtures; commit all of it.
**Success Criteria**: opset-17 ONNX runs in `Microsoft.ML.OnnxRuntime`; `corr ≈ 1.0000` / tiny relErr vs
PyTorch on random features; `freq2mels` + `windows` + the 90-symbol map + params extracted; `ref3b` (mel)
and `ref3c` (Viterbi) fixtures generated; ONNX + buffers + MIT license committed under
`fixtures/models/transkun/`.
**Tests**: a Python parity check (PyTorch `S` vs ONNX `S`); the committed artifacts load in C#.
**Status**: Complete — `transkun.onnx` 53 MB single-file, `corr=1.000000` maxRelErr ~5e-6 (random + dynamic
T), loads/runs in onnxruntime from the repo. Buffers (freq2mels/windows/symbols) + params + ref3b/ref3c
fixtures + MIT license + regeneration script committed under `fixtures/models/transkun/`. Finds vs the
workplan: config MUST come from `2.0.conf` (baseSize 64/nHead 8); the export blocker was 5-D **SDPA** (fixed
by a 4-D reshape), not `diag_embed` (which exported cleanly); `S`-only is the core-first boundary (velocity/
sub-frame need `ctx` post-decode → 4e).

## Stage 4b: Mel front end in C# (Infrastructure)

**Goal**: audio → `featuresBatch`, matching transkun's front end: framing (n_fft/hop) → per-segment
normalize → the 6 windows → `Radix2Fft` (ortho) → |·|² → `freq2mels` → log-norm.
**Success Criteria**: bit-close to the `ref3b` fixture (state the tolerance — cross-impl FFT drift, like the
render golden).
**Tests**: TDD vs `ref3b`; Parseval sanity; determinism.
**Status**: Not Started

## Stage 4c: Semi-CRF Viterbi decode in C# (Domain, BCL-only)

**Goal**: port `viterbiBackward` (inference path only) — `S` → note intervals (onset frame, offset frame,
pitch) + the two pedal tracks.
**Success Criteria**: intervals match the `ref3c` fixture exactly.
**Tests**: TDD vs `ref3c`; determinism; a tiny hand-checked `S` decodes to the expected notes.
**Status**: Not Started

## Stage 4d (next batch): TranskunTranscriber + parity gate

`TranskunTranscriber : ITranscriber` (mel → ONNX → Viterbi → `NoteEvent`s + pedal), segment stitching (a
note across a boundary recovered once), `transcribe --model transkun`, runtime measured. **4d-parity: ≥99 %
note-level F1 agreement PyTorch≡ONNX — must pass before 4d is "done"; offramp = subprocess/drop if it can't.**
Then 4e (velocity/sub-frame heads, deferred — core-first) and 4f (HuggingFace publish — outward, confirm
with Cornelius).
**Status**: Not Started (out of this batch)
