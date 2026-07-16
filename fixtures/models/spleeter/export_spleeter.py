#!/usr/bin/env python3
"""
Export Deezer Spleeter 5-stem model (TF1 checkpoint) to 5 per-branch ONNX models,
each emitting the RAW PRE-SOFTMAX LOGIT for its stem (no sigmoid, no mask x magnitude).
The cross-branch softmax, mask x magnitude, and ratio-mask reconstruction are done
downstream (in C#), not here.

=== Architecture, established by direct TF graph tracing on the 5-stem checkpoint ===
(NOT copied blindly from the k2-fsa/sherpa-onnx 2-stem reference unet.py, which uses a
DIFFERENT activation function (LeakyReLU) and decoder layer order than this checkpoint
actually computes -- see the "BN_5 / activation" finding in the module docstring below
and the accompanying report.)

The checkpoint has 5 regular branches (vocals, piano, drums, bass, other; confirmed by
tracing the `stack` op that feeds the joint softmax, back to conv2d_6, conv2d_13,
conv2d_20, conv2d_27, conv2d_34 in that exact order -- matching 5stems.json). Branch k
(k=0..4) uses:
  conv2d_{7k .. 7k+6}            (7 conv2d: 6 "encoder"/bottleneck convs + 1 final logit conv)
  conv2d_transpose_{6k .. 6k+5}  (6 deconvs: up1..up6)
  batch_normalization_{12k .. 12k+11}  (12 BNs: 5 encoder + 1 DEAD bottleneck + 6 decoder)

Per branch, the compute graph (verified by forward/backward-tracing ops on the restored
checkpoint, not assumed):
  Encoder step j=0..4:
    skip_j = conv2d_{7k+j}(SAME pad, stride2, kernel5)(prev)      # raw pre-BN output saved as skip
    h      = ELU(BN_{12k+j}(skip_j))
  Bottleneck (j=5):
    bottleneck = conv2d_{7k+5}(SAME pad, stride2, kernel5)(h)     # NO BN, NO activation.
      batch_normalization_{12k+5} (512ch) IS computed in the graph (its cond/Merge feeds an
      Elu op), but that Elu has ZERO consumers -- it is dead code, never reaching any
      `{stem}_spectrogram/mul` output. Empirically confirmed on branch 0: including vs.
      excluding it is the difference between the exported LOGIT UNet matching the TF
      logit to ~5e-4 abs (float32 noise on values up to ~1000) vs. being wrong by ~230 abs.
  Decoder step j=0..4 (up1..up5):
    d = conv2d_transpose_{6k+j}(oversized VALID transpose, stride2, kernel5)(prev)
    d = d[:, :, 1:-2, 1:-2]                # crop back down to the SAME-equivalent size
    d = ELU(d)                             # activation BEFORE BN (opposite of the sherpa ref!)
    d = BN_{12k+6+j}(d)                    # (a Dropout follows in training; no-op in eval)
    prev = concat([skip_{4-j}, d], channel_dim)   # encoder skip FIRST, decoder BN output SECOND
  Decoder step j=5 (up6):
    d = conv2d_transpose_{6k+5}(...)(prev)[:, :, 1:-2, 1:-2]
    d = ELU(d)
    d = BN_{12k+11}(d)                     # no concat after this -- straight to the final conv
  Final logit conv (dilation=2, kernel4, padding=3, in=1 out=2):
    logit = conv2d_{7k+6}(d)               # <-- THE ONNX OUTPUT. No sigmoid. No * magnitude.

Stems order confirmed by tracing the `stack` (Pack) op feeding `softmax/Softmax`, whose
5 inputs are, in order: conv2d_6/BiasAdd, conv2d_13/BiasAdd, conv2d_20/BiasAdd,
conv2d_27/BiasAdd, conv2d_34/BiasAdd -- i.e. branch k's logit conv IS conv2d_{7k+6}, and
branch order is [vocals, piano, drums, bass, other] (matching 5stems.json), sliced back out
via strided_slice_4..8 into vocals_spectrogram/mul, piano_spectrogram/mul, etc.
"""
import json
import os

import numpy as np
import onnx
import onnxruntime as ort
import tensorflow as tf
import torch

tf.compat.v1.disable_eager_execution()

# --- Reproducibility recipe. Run OFFLINE only; never on the build/test/run path ---
# (mirrors fixtures/models/transkun/export_transkun.py). Python is NOT a project dependency;
# it exists solely to regenerate the committed *.onnx. Native Apple Silicon works — no Docker:
#   uv venv --python 3.11 venv
#   uv pip install --python venv/bin/python "tensorflow-macos==2.12.0" "numpy<2" torch onnx onnxruntime
#   curl -fL -o 5stems.tar.gz https://github.com/deezer/spleeter/releases/download/v1.4.0/5stems.tar.gz
#   mkdir -p 5stems && tar -xzf 5stems.tar.gz -C 5stems
#   venv/bin/python export_spleeter.py    # writes {vocals,piano,drums,bass,other}.onnx beside this script
# Override the defaults with SPLEETER_CKPT_DIR / SPLEETER_OUT_DIR if your layout differs.
_HERE = os.path.dirname(os.path.abspath(__file__))
CKPT_DIR = os.environ.get("SPLEETER_CKPT_DIR", os.path.join(_HERE, "5stems"))
OUT_DIR = os.environ.get("SPLEETER_OUT_DIR", _HERE)
PARITY_DIR = os.path.join(OUT_DIR, "parity")

STEMS = ["vocals", "piano", "drums", "bass", "other"]  # branch k=0..4, graph-confirmed order


class LogitUNet(torch.nn.Module):
    """Per-branch UNet emitting the raw pre-softmax logit. See module docstring for the
    graph-verified layer order (ELU activation, deconv->ELU->BN decoder order, dead
    bottleneck BN skipped)."""

    def __init__(self):
        super().__init__()
        self.conv = torch.nn.Conv2d(2, 16, kernel_size=5, stride=(2, 2), padding=0)
        self.bn = torch.nn.BatchNorm2d(16, eps=1e-3)

        self.conv1 = torch.nn.Conv2d(16, 32, kernel_size=5, stride=(2, 2), padding=0)
        self.bn1 = torch.nn.BatchNorm2d(32, eps=1e-3)

        self.conv2 = torch.nn.Conv2d(32, 64, kernel_size=5, stride=(2, 2), padding=0)
        self.bn2 = torch.nn.BatchNorm2d(64, eps=1e-3)

        self.conv3 = torch.nn.Conv2d(64, 128, kernel_size=5, stride=(2, 2), padding=0)
        self.bn3 = torch.nn.BatchNorm2d(128, eps=1e-3)

        self.conv4 = torch.nn.Conv2d(128, 256, kernel_size=5, stride=(2, 2), padding=0)
        self.bn4 = torch.nn.BatchNorm2d(256, eps=1e-3)

        self.conv5 = torch.nn.Conv2d(256, 512, kernel_size=5, stride=(2, 2), padding=0)
        # bottleneck: no BN -- confirmed dead in the TF graph (see module docstring)

        self.up1 = torch.nn.ConvTranspose2d(512, 256, kernel_size=5, stride=2)
        self.bn5 = torch.nn.BatchNorm2d(256, eps=1e-3)

        self.up2 = torch.nn.ConvTranspose2d(512, 128, kernel_size=5, stride=2)
        self.bn6 = torch.nn.BatchNorm2d(128, eps=1e-3)

        self.up3 = torch.nn.ConvTranspose2d(256, 64, kernel_size=5, stride=2)
        self.bn7 = torch.nn.BatchNorm2d(64, eps=1e-3)

        self.up4 = torch.nn.ConvTranspose2d(128, 32, kernel_size=5, stride=2)
        self.bn8 = torch.nn.BatchNorm2d(32, eps=1e-3)

        self.up5 = torch.nn.ConvTranspose2d(64, 16, kernel_size=5, stride=2)
        self.bn9 = torch.nn.BatchNorm2d(16, eps=1e-3)

        self.up6 = torch.nn.ConvTranspose2d(32, 1, kernel_size=5, stride=2)
        self.bn10 = torch.nn.BatchNorm2d(1, eps=1e-3)

        self.up7 = torch.nn.Conv2d(1, 2, kernel_size=4, dilation=2, padding=3)

    def forward(self, x):
        """x: (num_audio_channels=2, num_splits, 512, 1024) -> raw logit, same convention."""
        x = x.permute(1, 0, 2, 3)  # (num_splits, 2, 512, 1024)

        xp = torch.nn.functional.pad(x, (1, 2, 1, 2))
        conv1 = self.conv(xp)
        e1 = torch.nn.functional.elu(self.bn(conv1))

        xp = torch.nn.functional.pad(e1, (1, 2, 1, 2))
        conv2 = self.conv1(xp)
        e2 = torch.nn.functional.elu(self.bn1(conv2))

        xp = torch.nn.functional.pad(e2, (1, 2, 1, 2))
        conv3 = self.conv2(xp)
        e3 = torch.nn.functional.elu(self.bn2(conv3))

        xp = torch.nn.functional.pad(e3, (1, 2, 1, 2))
        conv4 = self.conv3(xp)
        e4 = torch.nn.functional.elu(self.bn3(conv4))

        xp = torch.nn.functional.pad(e4, (1, 2, 1, 2))
        conv5 = self.conv4(xp)
        e5 = torch.nn.functional.elu(self.bn4(conv5))

        xp = torch.nn.functional.pad(e5, (1, 2, 1, 2))
        bottleneck = self.conv5(xp)  # 512ch, no BN/activation

        up1 = self.up1(bottleneck)[:, :, 1:-2, 1:-2]
        d1 = self.bn5(torch.nn.functional.elu(up1))
        m1 = torch.cat([conv5, d1], dim=1)

        up2 = self.up2(m1)[:, :, 1:-2, 1:-2]
        d2 = self.bn6(torch.nn.functional.elu(up2))
        m2 = torch.cat([conv4, d2], dim=1)

        up3 = self.up3(m2)[:, :, 1:-2, 1:-2]
        d3 = self.bn7(torch.nn.functional.elu(up3))
        m3 = torch.cat([conv3, d3], dim=1)

        up4 = self.up4(m3)[:, :, 1:-2, 1:-2]
        d4 = self.bn8(torch.nn.functional.elu(up4))
        m4 = torch.cat([conv2, d4], dim=1)

        up5 = self.up5(m4)[:, :, 1:-2, 1:-2]
        d5 = self.bn9(torch.nn.functional.elu(up5))
        m5 = torch.cat([conv1, d5], dim=1)

        up6 = self.up6(m5)[:, :, 1:-2, 1:-2]
        d6 = self.bn10(torch.nn.functional.elu(up6))

        logit = self.up7(d6)  # RAW LOGIT, 2ch, no sigmoid, no *magnitude
        return logit.permute(1, 0, 2, 3)  # (2, num_splits, 512, 1024)


def tfname(prefix, idx):
    return prefix if idx == 0 else f"{prefix}_{idx}"


def load_branch_weights(reader, k):
    """Map checkpoint variables for branch k (k=0..4) into a LogitUNet state dict."""

    def get(name):
        return torch.from_numpy(reader.get_tensor(name))

    conv_off = 7 * k
    bn_off = 12 * k
    tr_off = 6 * k

    sd = {}
    conv_attr = ["conv", "conv1", "conv2", "conv3", "conv4", "conv5"]
    for j, attr in enumerate(conv_attr):
        name = tfname("conv2d", conv_off + j)
        sd[f"{attr}.weight"] = get(f"{name}/kernel").permute(3, 2, 0, 1)
        sd[f"{attr}.bias"] = get(f"{name}/bias")

    bn_attr = ["bn", "bn1", "bn2", "bn3", "bn4"]
    for j, attr in enumerate(bn_attr):
        name = tfname("batch_normalization", bn_off + j)
        sd[f"{attr}.weight"] = get(f"{name}/gamma")
        sd[f"{attr}.bias"] = get(f"{name}/beta")
        sd[f"{attr}.running_mean"] = get(f"{name}/moving_mean")
        sd[f"{attr}.running_var"] = get(f"{name}/moving_variance")
    # bn index 5 (bn_off+5, bottleneck, 512ch) intentionally SKIPPED -- dead in the graph.

    up_attr = ["up1", "up2", "up3", "up4", "up5", "up6"]
    dec_bn_attr = ["bn5", "bn6", "bn7", "bn8", "bn9", "bn10"]
    for j in range(6):
        name = tfname("conv2d_transpose", tr_off + j)
        sd[f"{up_attr[j]}.weight"] = get(f"{name}/kernel").permute(3, 2, 0, 1)
        sd[f"{up_attr[j]}.bias"] = get(f"{name}/bias")

        bn_name = f"batch_normalization_{bn_off + 6 + j}"
        sd[f"{dec_bn_attr[j]}.weight"] = get(f"{bn_name}/gamma")
        sd[f"{dec_bn_attr[j]}.bias"] = get(f"{bn_name}/beta")
        sd[f"{dec_bn_attr[j]}.running_mean"] = get(f"{bn_name}/moving_mean")
        sd[f"{dec_bn_attr[j]}.running_var"] = get(f"{bn_name}/moving_variance")

    final_conv_name = tfname("conv2d", conv_off + 6)
    sd["up7.weight"] = get(f"{final_conv_name}/kernel").permute(3, 2, 0, 1)
    sd["up7.bias"] = get(f"{final_conv_name}/bias")

    return sd


def generate_waveform():
    rng = np.random.RandomState(20230821)
    waveform = rng.rand(60 * 44100).astype(np.float32)
    return waveform.reshape(-1, 2)


def restore_tf_and_run(stems):
    """Run the restored TF graph once, returning the shared input magnitude, each
    branch's raw logit (NHWC), and each branch's final softmax*mag mul output (NHWC)."""
    with tf.compat.v1.Session(graph=tf.Graph()) as sess:
        saver = tf.compat.v1.train.import_meta_graph(f"{CKPT_DIR}/model.meta")
        saver.restore(sess, f"{CKPT_DIR}/model")
        graph = sess.graph

        x_ph = graph.get_tensor_by_name("waveform:0")
        mag_t = graph.get_tensor_by_name("strided_slice_3:0")

        fetches = {"mag": mag_t}
        for k, stem in enumerate(stems):
            logit_name = tfname("conv2d", 7 * k + 6) + "/BiasAdd:0"
            fetches[f"logit_{stem}"] = graph.get_tensor_by_name(logit_name)
            fetches[f"mul_{stem}"] = graph.get_tensor_by_name(f"{stem}_spectrogram/mul:0")

        out = sess.run(fetches, feed_dict={x_ph: generate_waveform()})
    return out


def build_and_load_branch(reader, k):
    model = LogitUNet()
    model.eval()
    sd = model.state_dict()
    sd.update(load_branch_weights(reader, k))
    model.load_state_dict(sd)
    return model


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    os.makedirs(PARITY_DIR, exist_ok=True)

    print("Loading checkpoint reader...")
    reader = tf.train.load_checkpoint(CKPT_DIR)

    print("Restoring TF graph and running forward pass for ground truth...")
    tf_out = restore_tf_and_run(STEMS)
    mag_np = tf_out["mag"]  # (num_splits, 512, 1024, 2) NHWC
    print("mag shape:", mag_np.shape)

    x_torch = torch.from_numpy(mag_np).permute(3, 0, 1, 2)  # (2, num_splits, 512, 1024)

    torch_logits = {}
    per_branch_errors = {}

    print()
    print("=== Per-branch torch-vs-TF LOGIT parity ===")
    for k, stem in enumerate(STEMS):
        model = build_and_load_branch(reader, k)
        with torch.no_grad():
            torch_logit = model(x_torch).numpy()  # (2, num_splits, 512, 1024)
        torch_logits[stem] = torch_logit

        tf_logit_nhwc = tf_out[f"logit_{stem}"]
        tf_logit_permuted = np.transpose(tf_logit_nhwc, (3, 0, 1, 2))

        err = np.abs(torch_logit - tf_logit_permuted)
        max_err = float(err.max())
        mean_err = float(err.mean())
        val_range = (float(tf_logit_nhwc.min()), float(tf_logit_nhwc.max()))
        per_branch_errors[stem] = {"max_abs_err": max_err, "mean_abs_err": mean_err, "tf_value_range": val_range}
        print(f"  {stem:8s}: max_abs_err={max_err:.6g}  mean_abs_err={mean_err:.6g}  tf_range={val_range}")

        # Save the model for ONNX export re-use
        torch.save(model.state_dict(), os.path.join(OUT_DIR, f"{stem}.pt"))

    print()
    print("=== Exporting to ONNX (fp32, opset 13) ===")
    onnx_paths = {}
    for k, stem in enumerate(STEMS):
        model = LogitUNet()
        model.load_state_dict(torch.load(os.path.join(OUT_DIR, f"{stem}.pt"), map_location="cpu"))
        model.eval()

        num_splits = 3  # matches our validation input; dynamic_axes makes this arbitrary at runtime
        x_dummy = torch.rand(2, num_splits, 512, 1024, dtype=torch.float32)
        onnx_path = os.path.join(OUT_DIR, f"{stem}.onnx")
        torch.onnx.export(
            model,
            x_dummy,
            onnx_path,
            input_names=["x"],
            output_names=["y"],
            dynamic_axes={"x": {1: "num_splits"}, "y": {1: "num_splits"}},
            opset_version=13,
            dynamo=False,  # torch 2.13 defaults to the dynamo/onnxscript exporter; use the
                           # legacy TorchScript-based exporter instead (no onnxscript in this venv,
                           # and it's the exporter the task's reference scripts assume).
        )
        onnx_paths[stem] = onnx_path
        size_mb = os.path.getsize(onnx_path) / (1024 * 1024)
        print(f"  {stem:8s}: {onnx_path} ({size_mb:.1f} MB)")

    print()
    print("=== ONNX-vs-torch parity (same input) ===")
    onnx_logits = {}
    onnx_errors = {}
    for stem in STEMS:
        sess_opts = ort.SessionOptions()
        ort_session = ort.InferenceSession(onnx_paths[stem], sess_options=sess_opts, providers=["CPUExecutionProvider"])
        x_np = x_torch.numpy().astype(np.float32)
        onnx_out = ort_session.run(["y"], {"x": x_np})[0]
        onnx_logits[stem] = onnx_out

        err = np.abs(onnx_out - torch_logits[stem])
        max_err = float(err.max())
        mean_err = float(err.mean())
        onnx_errors[stem] = {"max_abs_err": max_err, "mean_abs_err": mean_err}
        print(f"  {stem:8s}: max_abs_err={max_err:.6g}  mean_abs_err={mean_err:.6g}")

    print()
    print("=== FULL-PATH validation: stack ONNX logits -> softmax across stems -> * magnitude, vs TF mul ===")
    # Stack in stem order (matches TF's Pack order): (5, 2, num_splits, 512, 1024)
    stacked = np.stack([onnx_logits[s] for s in STEMS], axis=0)
    stacked_t = torch.from_numpy(stacked)
    softmaxed = torch.softmax(stacked_t, dim=0).numpy()  # softmax across the 5-stem axis

    mag_permuted = np.transpose(mag_np, (3, 0, 1, 2))  # (2, num_splits, 512, 1024)

    full_path_errors = {}
    for i, stem in enumerate(STEMS):
        recon = softmaxed[i] * mag_permuted  # (2, num_splits, 512, 1024)
        tf_mul_nhwc = tf_out[f"mul_{stem}"]  # (num_splits, 512, 1024, 2)
        tf_mul_permuted = np.transpose(tf_mul_nhwc, (3, 0, 1, 2))

        err = np.abs(recon - tf_mul_permuted)
        max_err = float(err.max())
        mean_err = float(err.mean())
        full_path_errors[stem] = {"max_abs_err": max_err, "mean_abs_err": mean_err}
        print(f"  {stem:8s}: max_abs_err={max_err:.6g}  mean_abs_err={mean_err:.6g}")

    print()
    print("=== Saving parity fixtures for C# tests ===")
    # magnitude input, NHWC as produced by TF (num_splits, 512, 1024, 2) - save as-is, C-order float32
    mag_np.astype(np.float32).tofile(os.path.join(PARITY_DIR, "input_magnitude_nhwc.f32"))
    for stem in STEMS:
        tf_out[f"logit_{stem}"].astype(np.float32).tofile(
            os.path.join(PARITY_DIR, f"{stem}_logit_nhwc.f32")
        )

    manifest = {
        "seed": 20230821,
        "waveform_generator": "np.random.RandomState(20230821).rand(60*44100).astype(np.float32).reshape(-1, 2)",
        "input_magnitude_shape_nhwc": list(mag_np.shape),
        "input_magnitude_file": "input_magnitude_nhwc.f32",
        "stems": STEMS,
        "logit_shape_nhwc": list(tf_out[f"logit_{STEMS[0]}"].shape),
        "logit_files": {s: f"{s}_logit_nhwc.f32" for s in STEMS},
        "dtype": "float32",
        "order": "C",
        "per_branch_torch_vs_tf_logit_error": per_branch_errors,
        "onnx_vs_torch_logit_error": onnx_errors,
        "full_path_softmax_mag_vs_tf_mul_error": full_path_errors,
        "notes": [
            "logit = raw pre-softmax UNet output per branch (NHWC, channels=2 audio channels).",
            "To reconstruct Spleeter's mask*mag output in C#: stack the 5 branch logits on a new "
            "stem axis, softmax across that axis, multiply elementwise by input_magnitude.",
            "Architecture note: this checkpoint uses ELU activation throughout (not LeakyReLU/ReLU "
            "as in the k2-fsa/sherpa-onnx 2-stem reference unet.py), and the decoder order is "
            "deconv -> ELU -> BN (not deconv -> crop -> ReLU -> BN). The bottleneck BN "
            "(batch_normalization_{12k+5}) is confirmed dead code in the TF graph and is not used.",
        ],
    }
    with open(os.path.join(PARITY_DIR, "manifest.json"), "w") as f:
        json.dump(manifest, f, indent=2)

    print("Wrote parity fixtures to", PARITY_DIR)
    print()
    print("=== DONE ===")


if __name__ == "__main__":
    main()
