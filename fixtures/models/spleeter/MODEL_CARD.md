# Spleeter 5-stem — per-branch logit ONNX (audio-claudio source separation)

Five per-branch fp32 ONNX models converted from **Deezer Spleeter's 5-stem** TensorFlow-1
checkpoint (`5stems.tar.gz`, release v1.4.0). Each emits its stem's **raw pre-softmax logit**;
the cross-branch softmax, mask×magnitude, and ratio-mask reconstruction are done in C#
(`AudioClaudio.Infrastructure.Separation`), keeping all non-learned DSP outside the graph — the
same "STFT stays outside the ONNX" split the repo already uses for Transkun.

## Files
`vocals.onnx piano.onnx drums.onnx bass.onnx other.onnx` — 37.5 MB each, **fp32, opset 13**
(no quantization — per project policy, `DECISIONS.md`; every file is < 100 MB so no Git LFS).
`export_spleeter.py` — the offline reproducibility recipe (never on the build/test/run path).
`parity/` — a committed num_splits=1 reference (input magnitude + 5 TF logits) for the C# adapter's parity test.

## I/O contract (per model)
- **Input** `x`: fp32, shape `(2, num_splits, 512, 1024)` = (audio channels, time-splits, T=512, F=1024). `num_splits` is a dynamic axis.
- **Output** `y`: fp32, same shape — the branch's **raw logit** (NO sigmoid, NO ×magnitude).
- **Stem order (branch k):** `[vocals, piano, drums, bass, other]` — graph-confirmed from the `stack`→`softmax` op fed by `conv2d_{6,13,20,27,34}/BiasAdd`, matching `5stems.json`.

## Reconstruction the C# side owns (all non-learned)
1. STFT of the mixture: 44100 Hz, n_fft=4096, hop=1024, **Hann periodic**, zero-pad-then-hop (prepend one 4096 frame; NOT centered), crop magnitude to the first **F=1024** bins (~11.025 kHz ceiling — a weight property), pad+partition time to a multiple of **T=512**. Stereo required; mono is upmixed L=R.
2. Run all 5 ONNX → 5 logits. **Softmax across the 5-stem axis** → 5 masks. **× mixture magnitude** → 5 estimated magnitudes.
3. Power-ratio remask (`separation_exponent=2`), zero-extend F 1024→2049, × mixture **complex** STFT (mixture phase reused), iSTFT (Hann periodic, ×2/3 window-compensation), crop the leading pad.

## Architecture (verified by direct TF-graph tracing — not assumed)
5 independent U-Nets. Each: 6 encoder Conv2d (5×5, stride 2, filters 16→512) with `BN→ELU` (except the 512-ch bottleneck conv, which has **no BN/activation** — its `batch_normalization_{12k+5}` is *dead code* in the graph); 6 ConvTranspose2d decoders with **`deconv→crop[1:-2,1:-2]→ELU→BN`** and encoder-skip concatenation; a final `Conv2d(1→2, k=4, dilation=2, pad=3)` = the logit.
**Important:** this differs from the k2-fsa/sherpa-onnx *2-stem* reference (`LeakyReLU`, `deconv→ReLU→BN`); copying that verbatim produced a max error of ~230 (on a ±1000 range). The ELU + decoder-order fix, verified empirically, brought it to ~5e-4.

## Parity (independently re-verified, fresh TF extraction vs committed ONNX)
- **Per-branch logit, ONNX vs TF:** worst **6.3e-4 max-abs / ~5–6e-7 relative** (value range ±~1000). This is the tight, meaningful bound.
- Full-path (softmax×mag vs TF `*_spectrogram/mul`): mean ~1e-6; max up to ~0.05 only at softmax "near-ties" (top-1/top-2 logit gap < 0.01, ~0.37% of pixels), where float32 logit noise is locally amplified — intrinsic to softmax reconstruction, ~5e-5 relative to the magnitude scale. **C# full-path tests should use a relative/percentile tolerance, not a tight max-abs bound.**

## License (see `DECISIONS.md` → "Source separation")
Code MIT (`LICENSE.spleeter`, Deezer SA). Weight grant is **ambiguous** (repo README says "the *code* is MIT"; the JOSS paper, DOI 10.21105/joss.02154, says "source code *and pre-trained models* are… distributed under a MIT license"; a clarification request has been open + unanswered since 2024). Training data is Deezer's **private "Bean" catalog — not MUSDB** — so there is no third-party non-commercial encumbrance; the only doubt is Deezer's own grant wording, which their published paper resolves in our favor. Committed under that grant; this project is non-commercial, so even the pessimistic reading carries no exposure. Not to be re-cited as unconditionally "MIT."

## Honesty ranking
Separation quality is a *statistical* SI-SDR gate on clean synthetic mixes (`docs/CORPUS.md`), ranked **below** the mono bit-exact / poly-F1 / Transkun-parity tiers. Spleeter's piano is its weakest stem; the ~11 kHz ceiling is inherent; no jazz-specific quality is claimed.
