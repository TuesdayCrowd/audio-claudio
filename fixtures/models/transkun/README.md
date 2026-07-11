# Transkun — self-contained ONNX export (v2 Stage 4)

> **Published:** <https://huggingface.co/TuesdayCrowd/transkun-onnx> (public, MIT). This directory is the
> source of that release; `MODEL_CARD.md` is its HuggingFace model card.

A transformer-only ONNX export of **Transkun** (Yujia Yan's Neural Semi-CRF piano transcriber, 0.984
MAESTRO F1), plus the frozen front-end buffers and the decode spec needed to run it **in-process with no
Python/torch at runtime** behind `audio-claudio`'s `ITranscriber` port.

- **Upstream:** <https://github.com/Yujia-Yan/Skipping-The-Frame-Level> — Yujia Yan, Frank Cwitkowitz,
  Zhiyao Duan. Package `transkun` 2.0.1, checkpoint `pretrained/2.0.pt`.
- **License:** MIT (© 2021 Yujia Yan) — see [`LICENSE.transkun`](LICENSE.transkun). audio-claudio is UNLICENSE;
  MIT is compatible.
- This is a **transformer-only export + a decode spec**, not a drop-in `.onnx` transcriber: the mel front
  end and the semi-CRF Viterbi decode are reimplemented in C# (Stages 4b/4c), because `torch.fft.rfft` and
  the custom semi-CRF backtracking are not ONNX-exportable.

## What the ONNX computes

`transkun.onnx` maps **`featuresBatch` → (`S`, `ctx`)**:

- input `featuresBatch` `[nBatch, T, 229, 6]` — log-mel features (229 mel bins × 6 windows), `T` dynamic.
- output `S` `[T, T, nBatch*90]` — the semi-CRF pairwise interval scores. `S[e, b, k]` scores a note on
  track `k` spanning frames `b→e` (diagonal `e==b` = a single-frame note). The 90 tracks are
  `symbols = [-64, -67, 21..108]`: index 0 = sustain pedal (CC64), 1 = soft pedal (CC67), 2–89 = MIDI
  21–108. (The `S_skip` "no-event" score is provably 0 and is hardcoded in C#.)
- output `ctx` `[90, T, 256]` (Stage 4e) — the backbone features, gathered at decoded interval endpoints to
  drive the attribute heads. `S` is byte-for-byte the same as the S-only 4a export (corr 1.0).

**`transkun-heads.onnx`** (Stage 4e) maps the gathered interval features **`attr` `[N, 768]`**
(`[ctx_a, ctx_b, ctx_a·ctx_b]`) → **`velLogits` `[N, 128]`** (`velocityPredictor`; velocity = argmax) and
**`ofRaw` `[N, 4]`** (`refinedOFPredictor`: two sub-frame onset/offset value logits → a ContinuousBernoulli
mean in `[-0.5, 0.5]` frames, + two presence logits). This adds real velocity + sub-frame timing on top of
the frame-level decode — validated note-identical to the native CLI (velocity exact, onsets ~1 ms).

Two ops needed care in export (see `export_transkun.py`): the backbone's **5-D `scaled_dot_product_attention`**
is reshaped to 4-D (a mathematical identity — SDPA batches all but the last two dims) because the ONNX
exporter only supports 4-D SDPA. `diag_embed` exported cleanly on this stack (torch 2.13 / onnx 1.22 /
onnxruntime 1.27, opset 17), contrary to an earlier assumption. Validated **`corr = 1.000000`,
maxRelErr ≈ 5e-6** vs PyTorch on random and dynamic-`T` inputs.

## Files

| File | What |
|---|---|
| `transkun.onnx` | the main export `featuresBatch → (S, ctx)` (opset 17, weights inlined, ~53 MB) |
| `transkun-heads.onnx` | the velocity + onset/offset attribute heads `attr → (velLogits, ofRaw)` (~3.4 MB) |
| `export_transkun_heads.py` | Stage-4e regeneration (main graph with `ctx` + the heads) |
| `freq2mels.f32` `[2049, 229]` | mel filterbank (`torchaudio.melscale_fbanks`, 30–8000 Hz) — Stage 4b |
| `windows.f32` `[6, 4096]` | analysis windows (row 0 Hann, rows 1–5 learned Gaussian) — Stage 4b |
| `symbols.i32` `[90]` | the track→symbol map `[-64, -67, 21..108]` |
| `params.json` | fs 44100, windowSize 4096, hopSize 1024, nMels 229, eps 1e-5, segment 16 s / hop 8 s |
| `ref3b_audio.f32`, `ref3b_features.f32` | Stage-4b TDD fixture: 1.5 s of `two-bar.wav` → its `featuresBatch` `[66,229,6]` |
| `ref3c_S.f32`, `ref3c_intervals.json` | Stage-4c TDD fixture: a real model `S` `[66,66,90]` → Viterbi intervals |
| `ref3c_syn_S.f32`, `ref3c_syn_intervals.json` | hand-built multi-track `S` `[6,6,90]` → known intervals |
| `ref3c_forced_intervals.json` | the same synthetic `S` with a `forcedStartPos` (Stage-4d stitching) |
| `manifest.json` | shape/dtype/file for every raw `.f32`/`.i32` array (raw little-endian) |
| `export_transkun.py` | the regeneration script (needs the transkun venv; see below) |

## Regenerating

Not needed for the build (everything above is committed). To reproduce, in a venv with
`transkun==2.0.1`, `torch`, `onnx`, `onnxruntime`, `numpy`:

```
python export_transkun.py <output-dir>
```

It loads the model **from `2.0.conf`** (not class defaults — `baseSize=64`, `nHead=8`), wraps
`backbone + scorer`, exports, validates against PyTorch, extracts the buffers, and regenerates the ref
fixtures. Deterministic (seed 0).
