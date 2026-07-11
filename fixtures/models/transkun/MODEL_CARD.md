---
license: mit
library_name: onnxruntime
tags:
  - audio
  - music
  - piano-transcription
  - amt
  - onnx
  - transkun
  - semi-crf
pipeline_tag: audio-to-audio
---

# Transkun — transformer-only ONNX export + decode spec

A **self-contained ONNX export of [Transkun](https://github.com/Yujia-Yan/Skipping-The-Frame-Level)**
(Yujia Yan's Neural Semi-CRF piano transcriber, 0.984 MAESTRO note F1) that runs the full model **in-process
with no Python/PyTorch at runtime**. This is **not** a drop-in `.onnx` transcriber: `torch.fft.rfft` and the
custom semi-CRF backtracking are not ONNX-exportable, so the mel front end and the Viterbi decode are provided
as a **documented decode spec** + a **reference decoder**.

> **Attribution.** The model, weights and architecture are the work of **Yujia Yan, Frank Cwitkowitz and
> Zhiyao Duan** (*"Skipping the Frame-Level: Event-Based Piano Transcription with Neural Semi-CRFs"*, NeurIPS
> 2021). This is an independent export + decode spec, not affiliated with or endorsed by the authors. Upstream:
> <https://github.com/Yujia-Yan/Skipping-The-Frame-Level>. License: **MIT** (© 2021 Yujia Yan).

## What's in the package

| File | Role |
|---|---|
| `transkun.onnx` (~53 MB, opset 17) | `featuresBatch [1,T,229,6] → (S [T,T,90], ctx [90,T,256])` — the transformer + semi-CRF scorer + backbone features |
| `transkun-heads.onnx` (~3.4 MB) | `attr [N,768] → (velLogits [N,128], ofRaw [N,4])` — velocity + sub-frame onset/offset heads |
| `freq2mels.f32 [2049,229]`, `windows.f32 [6,4096]`, `symbols.i32 [90]`, `params.json` | frozen front-end constants |
| `LICENSE.transkun` | upstream MIT license |
| `export_transkun.py`, `export_transkun_heads.py` | regeneration scripts (need the `transkun` PyTorch package) |

The **decode spec** (`README.md` in this repo) documents the mel front end, the `S` layout, the 90-track
symbol map (`[-64, -67, 21..108]` = sustain/soft pedal + MIDI 21–108), the semi-CRF `viterbiBackward`, the
16 s/8 s segment stitching, and the attribute heads (velocity = argmax; `ofValue` = ContinuousBernoulli mean).

## Reference decoder + validation

The reference decoder is the C# implementation in **[audio-claudio](https://github.com/TuesdayCrowd/audio-claudio)**
(mel front end, `SemiCrfViterbi`, `TranskunTranscriber`). It is validated **note-identical to the native
`transkun` CLI (PyTorch)**: on the test clips it reaches **100% note-level F1 at ±25 ms** with **exact
velocity** on every note — the export + decode spec reproduce the reference implementation, not merely
approximate it.

## Pipeline (how to run)

```
audio (mono, 44.1 kHz)
  → mel front end (framing 4096/1024, 6 windows, rfft ortho, freq2mels, log-norm) → featuresBatch
  → transkun.onnx → (S, ctx)
  → semi-CRF viterbiBackward(S) → per-track note intervals, over 16 s/8 s stitched segments
  → gather ctx at interval endpoints → transkun-heads.onnx → velocity + sub-frame onset/offset
  → notes (+ sustain/soft pedal from tracks 0/1)
```

See the repo's decode spec and `TranskunTranscriber` for the exact arithmetic (segment padding, `forcedStartPos`
carry, merge, `resolveOverlapping`).
