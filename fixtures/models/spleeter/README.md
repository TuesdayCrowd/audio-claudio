# fixtures/models/spleeter

Committed Spleeter 5-stem source-separation model for audio-claudio — **five per-branch
fp32 logit ONNX** (`vocals/piano/drums/bass/other`.onnx), loaded at runtime through
`Microsoft.ML.OnnxRuntime` by `AudioClaudio.Infrastructure.Separation.SpleeterSourceSeparator`.

- **`MODEL_CARD.md`** — the authoritative spec: I/O tensor contract, the C#-side reconstruction
  (softmax + mask + iSTFT), the graph-verified architecture, parity numbers, and the license posture.
- **`LICENSE.spleeter`** — Deezer's MIT license text (weight-grant ambiguity documented in the model card + `DECISIONS.md`).
- **`export_spleeter.py`** — the offline, throwaway recipe that regenerates the `.onnx`. **Python is not a
  project dependency** — nothing on the build/test/run path touches it (same pattern as `../transkun/export_transkun.py`).
- **`parity/`** — a small (num_splits=1) reference (input magnitude + 5 TF logits) the C# adapter's parity test checks against.

fp32 only, no quantization (`DECISIONS.md`); each `.onnx` is < 100 MB so no Git LFS is needed.
