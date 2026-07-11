#!/usr/bin/env python
"""v2 Stage 4e — re-export the main graph to ALSO output ctx, and export the two attribute heads
(velocityPredictor, refinedOFPredictor) so the C# engine can add real velocity + sub-frame onset/offset
refinement. Overwrites transkun.onnx (S is byte-for-byte the same; ctx is an added output) and adds
transkun-heads.onnx. Deterministic (seed 0)."""
import json, os, sys
import numpy as np
import torch

TK_DIR = "/private/tmp/claude-501/-Users-lawls-Development-TuesdayCrowd-Projects-audio-claudio/37748a9b-31e0-48a1-896e-7a25c1faf008/scratchpad/transkun-env/lib/python3.14/site-packages/transkun"
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__)) + "/artifacts4e"
os.makedirs(OUT, exist_ok=True)
torch.manual_seed(0); np.random.seed(0)

print("[1] load model from 2.0.conf")
import moduleconf
cm = moduleconf.parseFromFile(os.path.join(TK_DIR, "pretrained", "2.0.conf"))
model = cm["Model"].module.TransKun(conf=cm["Model"].config)
ckpt = torch.load(os.path.join(TK_DIR, "pretrained", "2.0.pt"), map_location="cpu")
model.load_state_dict(ckpt["best_state_dict" if "best_state_dict" in ckpt else "state_dict"], strict=False)
model.eval(); torch.set_grad_enabled(False)

# SDPA 5D->4D (identity) so the backbone exports; same as the 4a export.
import torch.nn.functional as F
_sdpa = F.scaled_dot_product_attention
def _sdpa4d(q, k, v, *a, **kw):
    if q.ndim == 5:
        B, X, H, L, D = q.shape
        r = lambda t: t.reshape(B * X, H, L, D)
        return _sdpa(r(q), r(k), r(v), *a, **kw).reshape(B, X, H, L, D)
    return _sdpa(q, k, v, *a, **kw)
F.scaled_dot_product_attention = _sdpa4d
torch.nn.functional.scaled_dot_product_attention = _sdpa4d

import onnx, onnxruntime as ort
def consolidate(split_path, single_path):
    m = onnx.load(split_path)
    onnx.save(m, single_path, save_as_external_data=False)
    os.remove(split_path)
    if os.path.exists(split_path + ".data"):
        os.remove(split_path + ".data")

# ---- main graph: featuresBatch -> (S, ctx) --------------------------------------------------------------
print("[2] export main graph featuresBatch -> (S, ctx)")
class MainWrapper(torch.nn.Module):
    def __init__(self, m):
        super().__init__()
        self.backbone = m.backbone
        self.scorer = m.scorer
        self.register_buffer("outputIndices", torch.tensor(m.targetMIDIPitch))
    def forward(self, featuresBatch):
        ctx = self.backbone(featuresBatch, outputIndices=self.outputIndices)  # [1,90,T,256]
        S_batch, _ = self.scorer(ctx)
        S = S_batch.flatten(-2, -1)                                            # [T,T,90]
        return S, ctx.squeeze(0)                                              # ctx -> [90,T,256]

main = MainWrapper(model).eval()
feat = torch.randn(1, 64, 229, 6)
split = os.path.join(OUT, "_main.onnx")
torch.onnx.export(main, (feat,), split, opset_version=17,
                  input_names=["featuresBatch"], output_names=["S", "ctx"],
                  dynamic_axes={"featuresBatch": {1: "T"}, "S": {0: "T", 1: "T"}, "ctx": {1: "T"}})
main_path = os.path.join(OUT, "transkun.onnx")
consolidate(split, main_path)
print(f"  transkun.onnx {os.path.getsize(main_path)/1e6:.1f} MB")

sess = ort.InferenceSession(main_path, providers=["CPUExecutionProvider"])
S_t, ctx_t = main(feat)
S_o, ctx_o = sess.run(None, {"featuresBatch": feat.numpy()})
print(f"  S   corr={np.corrcoef(S_t.numpy().ravel(), S_o.ravel())[0,1]:.6f} shape={list(S_o.shape)}")
print(f"  ctx corr={np.corrcoef(ctx_t.numpy().ravel(), ctx_o.ravel())[0,1]:.6f} shape={list(ctx_o.shape)}")

# ---- heads: attr[N,768] -> (velLogits[N,128], ofRaw[N,4]) -----------------------------------------------
print("[3] export heads attr -> (velLogits, ofRaw)")
class HeadWrapper(torch.nn.Module):
    def __init__(self, m):
        super().__init__()
        self.vel = m.velocityPredictor
        self.of = m.refinedOFPredictor
    def forward(self, attr):
        return self.vel(attr), self.of(attr)

heads = HeadWrapper(model).eval()
attr = torch.randn(7, 768)
hsplit = os.path.join(OUT, "_heads.onnx")
torch.onnx.export(heads, (attr,), hsplit, opset_version=17,
                  input_names=["attr"], output_names=["velLogits", "ofRaw"],
                  dynamic_axes={"attr": {0: "N"}, "velLogits": {0: "N"}, "ofRaw": {0: "N"}})
heads_path = os.path.join(OUT, "transkun-heads.onnx")
consolidate(hsplit, heads_path)
print(f"  transkun-heads.onnx {os.path.getsize(heads_path)/1e3:.0f} KB")

hsess = ort.InferenceSession(heads_path, providers=["CPUExecutionProvider"])
v_t, o_t = heads(attr)
v_o, o_o = hsess.run(None, {"attr": attr.numpy()})
print(f"  velLogits corr={np.corrcoef(v_t.numpy().ravel(), v_o.ravel())[0,1]:.6f} shape={list(v_o.shape)}")
print(f"  ofRaw     corr={np.corrcoef(o_t.numpy().ravel(), o_o.ravel())[0,1]:.6f} shape={list(o_o.shape)}")

print(f"[4] wrote {main_path} + {heads_path}")
print("DONE")
