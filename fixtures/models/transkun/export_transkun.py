#!/usr/bin/env python
"""v2 Stage 4a — re-derive the Transkun ONNX export + frozen buffers + TDD fixtures.

Boundary: featuresBatch -> S (backbone + inner-product CRF scorer). The mel front end (audio ->
featuresBatch) and the Viterbi decode (S -> intervals) are ported to C# (4b/4c); this script also emits
their reference fixtures. All outputs are written raw little-endian + a manifest.json for trivial C# reads.
"""
import json, math, os, sys
import numpy as np
import torch

TK_DIR = "/private/tmp/claude-501/-Users-lawls-Development-TuesdayCrowd-Projects-audio-claudio/37748a9b-31e0-48a1-896e-7a25c1faf008/scratchpad/transkun-env/lib/python3.14/site-packages/transkun"
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__)) + "/artifacts"
os.makedirs(OUT, exist_ok=True)
torch.manual_seed(0)
np.random.seed(0)

manifest = {}
def save(name, arr, dtype):
    a = np.ascontiguousarray(arr).astype(dtype)
    fn = name + (".f32" if dtype == "<f4" else ".i32")
    a.tofile(os.path.join(OUT, fn))
    manifest[name] = {"file": fn, "shape": list(a.shape), "dtype": "f32" if dtype == "<f4" else "i32"}
    print(f"  saved {name:22} shape={list(a.shape)} dtype={manifest[name]['dtype']}")

# ---------------------------------------------------------------- 1. load model from the .conf ----------
print("[1] loading model from 2.0.conf + 2.0.pt")
import moduleconf
conf_path = os.path.join(TK_DIR, "pretrained", "2.0.conf")
pt_path = os.path.join(TK_DIR, "pretrained", "2.0.pt")
confManager = moduleconf.parseFromFile(conf_path)
TransKun = confManager["Model"].module.TransKun
conf = confManager["Model"].config
checkpoint = torch.load(pt_path, map_location="cpu")
model = TransKun(conf=conf)
key = "best_state_dict" if "best_state_dict" in checkpoint else "state_dict"
missing = model.load_state_dict(checkpoint[key], strict=False)
model.eval(); torch.set_grad_enabled(False)
print(f"  loaded ({key}); missing={len(missing.missing_keys)} unexpected={len(missing.unexpected_keys)}")
print(f"  fs={model.fs} windowSize={model.windowSize} hopSize={model.hopSize} "
      f"nSym={len(model.targetMIDIPitch)} params={sum(p.numel() for p in model.parameters())/1e6:.2f}M")

# ---------------------------------------------------------------- 2. export wrapper: featuresBatch -> S -
class ExportWrapper(torch.nn.Module):
    def __init__(self, m):
        super().__init__()
        self.backbone = m.backbone
        self.scorer = m.scorer
        self.register_buffer("outputIndices", torch.tensor(m.targetMIDIPitch))
    def forward(self, featuresBatch):            # [nBatch, T, 229, 6]
        ctx = self.backbone(featuresBatch, outputIndices=self.outputIndices)
        S_batch, _ = self.scorer(ctx)            # S_batch: [T, T, nBatch, 90]; skip is provably 0
        return S_batch.flatten(-2, -1)           # [T, T, nBatch*90]

wrapper = ExportWrapper(model).eval()
onnx_path = os.path.join(OUT, "model.onnx")
T0 = 64
feat_example = torch.randn(1, T0, 229, 6)

# The backbone's axial attention calls SDPA on 5-D q/k/v ([B, T', H, L, D]); the ONNX exporter's SDPA only
# supports 4-D. SDPA treats every dim before the last two as batch, so collapsing the two leading dims to
# one 4-D batch is a mathematical identity (validated by corr below). Patch for the duration of the export.
import torch.nn.functional as F
_orig_sdpa = F.scaled_dot_product_attention
def _sdpa_4d(q, k, v, *a, **kw):
    if q.ndim == 5:
        B, X, H, L, D = q.shape
        rs = lambda t: t.reshape(B * X, H, L, D)
        return _orig_sdpa(rs(q), rs(k), rs(v), *a, **kw).reshape(B, X, H, L, D)
    return _orig_sdpa(q, k, v, *a, **kw)
F.scaled_dot_product_attention = _sdpa_4d
torch.nn.functional.scaled_dot_product_attention = _sdpa_4d

print(f"[2] exporting featuresBatch{list(feat_example.shape)} -> S, opset 17 (SDPA reshaped 5D->4D)")
try:
    torch.onnx.export(
        wrapper, (feat_example,), onnx_path, opset_version=17,
        input_names=["featuresBatch"], output_names=["S"],
        dynamic_axes={"featuresBatch": {1: "T"}, "S": {0: "T", 1: "T"}})
    print(f"  export OK: {os.path.getsize(onnx_path)/1e6:.1f} MB (+ external .data)")
except Exception as e:
    print(f"  STOCK EXPORT FAILED: {type(e).__name__}: {e}\n  (would apply eye-multiply patch)")
    raise

# Consolidate the external-data weights into ONE self-contained .onnx for committing.
import onnx
m_full = onnx.load(onnx_path)  # pulls in model.onnx.data
single_path = os.path.join(OUT, "transkun.onnx")
onnx.save(m_full, single_path, save_as_external_data=False)
os.remove(onnx_path)
if os.path.exists(onnx_path + ".data"):
    os.remove(onnx_path + ".data")
print(f"  consolidated -> transkun.onnx {os.path.getsize(single_path)/1e6:.1f} MB (single file)")

# ---------------------------------------------------------------- 3. validate ONNX == PyTorch -----------
print("[3] validating single-file ONNX vs PyTorch")
import onnxruntime as ort
sess = ort.InferenceSession(single_path, providers=["CPUExecutionProvider"])
def check(feat, tag):
    s_torch = wrapper(feat).numpy()
    s_onnx = sess.run(None, {"featuresBatch": feat.numpy()})[0]
    corr = np.corrcoef(s_torch.ravel(), s_onnx.ravel())[0, 1]
    denom = np.abs(s_torch).max() + 1e-9
    relerr = np.abs(s_torch - s_onnx).max() / denom
    print(f"  {tag:14} shape={list(s_onnx.shape)} corr={corr:.6f} maxRelErr={relerr:.2e}")
    return corr, relerr, s_onnx
check(feat_example, "random T=64")
check(torch.randn(1, 100, 229, 6), "random T=100")   # dynamic-T sanity

# ---------------------------------------------------------------- 4. frozen buffers ---------------------
print("[4] extracting frozen buffers")
fe = model.framewiseFeatureExtractor
freq2mels = fe.freq2mels.numpy()                                     # [2049, 229]
win = fe.spectrogramExtractor.win                                    # [4096] Hann
wins = torch.cat([win.unsqueeze(0), fe.spectrogramExtractor.winGen.get().t()], dim=0).numpy()  # [6, 4096]
save("freq2mels", freq2mels, "<f4")
save("windows", wins, "<f4")
save("symbols", np.array(model.targetMIDIPitch), "<i4")
params = {
    "fs": int(model.fs), "windowSize": int(model.windowSize), "hopSize": int(model.hopSize),
    "nMels": int(fe.outputDim), "nWindows": int(wins.shape[0]), "eps": float(fe.eps),
    "fMin": 30.0, "fMax": 8000.0, "rfftBins": int(model.windowSize // 2 + 1),
    "segmentSizeSeconds": 16.0, "segmentHopSeconds": 8.0, "nSymbols": len(model.targetMIDIPitch),
}
print("  params:", params)

# ---------------------------------------------------------------- 5. ref3b: audio -> featuresBatch ------
print("[5] ref3b (mel front end reference)")
fs, hop, wsz, eps, nmel = model.fs, model.hopSize, model.windowSize, fe.eps, fe.outputDim
# The first 1.5 s of the committed two-bar MeltySynth piano render (44100 Hz mono int16) — a real piano
# signal (attack + timbre) that fires the MAESTRO-trained model, fully reproducible from a committed WAV.
import wave
wf = wave.open("/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/fixtures/golden/two-bar.wav")
assert wf.getframerate() == fs and wf.getnchannels() == 1 and wf.getsampwidth() == 2
nread = int(1.5 * fs)
audio = (np.frombuffer(wf.readframes(nread), dtype="<i2").astype(np.float32) / 32768.0)
wf.close()

def make_frame(x, hopSize, windowSize):  # mirrors Util.makeFrame (leftPaddingHalfFrame=True)
    n = x.shape[-1]
    nFrame = math.ceil(n / hopSize) + 1
    lPad = windowSize // 2
    rPad = (nFrame - 1) * hopSize + windowSize // 2 - n
    xp = torch.nn.functional.pad(torch.tensor(x), (lPad, rPad))
    return xp.unfold(-1, windowSize, hopSize)  # [nFrame, windowSize]

frames = make_frame(audio, hop, wsz).unsqueeze(0).unsqueeze(0)  # [1,1,nFrame,windowSize] (nBatch,nChan,..)
mean = frames.mean(dim=[1, 2, 3], keepdim=True)
std = frames.std(dim=[1, 2, 3], keepdim=True)
framesN = (frames - mean) / (std + 1e-8)
features = fe(framesN).contiguous()                             # [1,1,nFrame,229,6]
features = features.view(1, *features.shape[-3:])               # [1,nFrame,229,6]
save("ref3b_audio", audio, "<f4")
save("ref3b_features", features.squeeze(0).numpy(), "<f4")      # [nFrame,229,6]
print(f"  audio={len(audio)} samples -> features {list(features.shape)}")

# ---------------------------------------------------------------- 6. ref3c: S -> intervals --------------
print("[6] ref3c (Viterbi decode reference)")
from transkun.CRF.NeuralSemiCRFInterval import viterbiBackward
S_real = wrapper(features).squeeze()          # [nFrame,nFrame,90]  (nBatch=1 -> flatten gives 90)
T = S_real.shape[0]
noise = torch.zeros(T - 1, S_real.shape[2])
intervals_real = viterbiBackward(S_real, noise, None)         # per-track list of (begin,end)
save("ref3c_S", S_real.numpy(), "<f4")
json.dump({str(k): v for k, v in enumerate(intervals_real)},
          open(os.path.join(OUT, "ref3c_intervals.json"), "w"))
nnotes = sum(len(v) for v in intervals_real)
print(f"  real S {list(S_real.shape)} -> {nnotes} intervals across 90 tracks")

# Hand-checkable synthetic S, multi-track (score[end, begin, track]): track 5 = interval (1,3) + singleton
# (5,5); track 10 = interval (0,4); track 20 = singleton (2,2). Decoded with default forcedStartPos.
Tsyn, nSym = 6, 90
Ssyn = torch.full((Tsyn, Tsyn, nSym), -5.0)
Ssyn[3, 1, 5] = 4.0
Ssyn[5, 5, 5] = 2.0
Ssyn[4, 0, 10] = 6.0
Ssyn[2, 2, 20] = 3.0
noise_syn = torch.zeros(Tsyn - 1, nSym)
intervals_syn = viterbiBackward(Ssyn, noise_syn, None)
save("ref3c_syn_S", Ssyn.numpy(), "<f4")
json.dump({str(k): v for k, v in enumerate(intervals_syn)},
          open(os.path.join(OUT, "ref3c_syn_intervals.json"), "w"))
print(f"  synthetic S {list(Ssyn.shape)} -> t5={intervals_syn[5]} t10={intervals_syn[10]} t20={intervals_syn[20]}")

# A forcedStartPos case (used by 4d segment stitching): same S, but track 5 forced to start at frame 4,
# so its interval (1,3) is skipped and only the singleton (5,5) survives.
forced = [0] * nSym
forced[5] = 4
intervals_forced = viterbiBackward(Ssyn, noise_syn, forced)
json.dump({"forcedStartPos": forced, "intervals": {str(k): v for k, v in enumerate(intervals_forced)}},
          open(os.path.join(OUT, "ref3c_forced_intervals.json"), "w"))
print(f"  forced(t5->4) -> t5={intervals_forced[5]} (interval (1,3) skipped)")

# ---------------------------------------------------------------- 7. write manifest + params ------------
json.dump(manifest, open(os.path.join(OUT, "manifest.json"), "w"), indent=2)
json.dump(params, open(os.path.join(OUT, "params.json"), "w"), indent=2)
print(f"[7] wrote manifest.json + params.json to {OUT}")
print("DONE")
