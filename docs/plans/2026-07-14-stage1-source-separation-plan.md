# Stage 1 — Source Separation Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.
> **Commit discipline:** this repo uses **GitButler**, never raw `git`. Every "Commit" step means the gitbutler skill (`but commit <branch> -m "…" --changes <ids>`), per `CLAUDE.md` §1 rule 4.

**Goal:** Add an `ISourceSeparator` port and one Spleeter-5-stem ONNX adapter that splits a mixed recording into labeled stems (piano / bass / other, plus vocals / drums), exposed as a `separate <mix.wav>` command, and proven by a synthesize→separate SI-SDR closed-loop gate in CI.

**Architecture:** Hexagonal, same discipline as the rest of the repo. A separated stem is *just another* `IAudioSource`, so the existing `ITranscriber` middle is reused verbatim in later stages. New DSP (inverse STFT + overlap-add reconstruction) lands in `AudioClaudio.Domain`; the ONNX adapter + in-memory PCM source land in `AudioClaudio.Infrastructure`; the port lands in `AudioClaudio.Application.Ports`; the model locator + `separate` verb land in `AudioClaudio.Cli`. The Spleeter weights are converted to ONNX by an offline Python script we own (mirroring `fixtures/models/transkun/export_transkun.py`) and committed under `fixtures/models/spleeter/`.

**Tech Stack:** .NET 10, `Microsoft.ML.OnnxRuntime` (already a repo dependency — **no new NuGet**), the repo's hand-rolled `Radix2Fft`, xUnit + CsCheck, MeltySynth (existing) as the closed-loop oracle. Offline export toolchain (not a repo dependency): TensorFlow 2.12.1 + PyTorch + onnx, run once on Linux x86_64.

**Scope of THIS plan:** Stage 1 only (of the falsifiable half, Stages 1–3, per `docs/plans/2026-07-13-multi-instrument-piano-reduction-design.md`). **Stop for review before Stage 2.** Nothing here transcribes, arranges, or reduces — it separates and proves the separation.

---

## 0. Context the implementer needs before starting

### 0.1 The decisions already locked (Cornelius, 2026-07-13/14)

- **Scope:** build the *falsifiable half* (Stages 1–3 → multi-track MIDI); arrangement (Stage 4) is a separate, later, opt-in verb. This plan is Stage 1.
- **Separator model:** **Spleeter 5-stem (Deezer)**. Chosen because it is the *only* model in the entire field with a native **piano** stem — the whole point of the feature — after a source-verified survey found no music-source-separation model cleanly passes the strict `CLAUDE.md` §1.7 weight-license bar (see `DECISIONS.md` → "Source separation — model + license"). Spleeter is the one arguably-compliant option (private, non-MUSDB training data + a published MIT-on-models grant).
- **License posture:** commit the weights under Deezer's MIT grant (JOSS paper: *"source code and pre-trained models are… distributed under a MIT license"*), with `fixtures/models/spleeter/LICENSE.spleeter` + a `MODEL_CARD.md` that **honestly records the README-vs-JOSS wording ambiguity**. The repo's UNLICENSE covers the code, not the bundled model — exactly as with Basic Pitch / Transkun. Commercial use is out of scope for this project, so even the pessimistic reading of the ambiguity carries no practical exposure.

### 0.2 Verified contracts to CONSUME (do not re-derive — these were grounded against the live tree)

Ports & primitives:
- `AudioClaudio.Application.Ports.IAudioSource` → `public interface IAudioSource { IEnumerable<Frame> Frames { get; } }` — **a stem is just this.**
- `AudioClaudio.Domain.Frame` → `sealed class Frame { float[] Samples; SamplePosition Start; SampleRate Rate => Start.Rate; }`.
- `AudioClaudio.Domain.FrameParameters` → `readonly record struct FrameParameters(int Size, int Hop)`.
- `AudioClaudio.Domain.Framing` → `static IReadOnlyList<Frame> Split(float[] samples, SampleRate rate, FrameParameters parameters, long startSample = 0)`; `static float[] ReconstructMono(IReadOnlyList<Frame> frames)`.
- `AudioClaudio.Domain.SampleRate`, `SamplePosition`, `SampleDuration` — integer sample time (§4 non-negotiables).
- `AudioClaudio.Application.Ports.ITranscriber` → `TranscriptionResult Transcribe(IAudioSource source)` — **reused unchanged in Stage 2.**

Audio plumbing:
- `AudioClaudio.Domain.AudioResampler` → `static float[] Resample(float[] input, int inRate, int outRate, int lobes = 4)` — band-limited Lanczos.
- `AudioClaudio.Domain.Spectral.Radix2Fft` → has `Forward(...)` (full complex spectrum) only. **No inverse exists** (built in Stage 1.1). 4096 is a supported power-of-two size.
- `AudioClaudio.Infrastructure.Audio.WavFileWriter` → `static void Write(string path, ReadOnlySpan<float> monoPcm, SampleRate rate)`; `static byte[] ToBytes(...)`.
- **No production in-memory `IAudioSource` exists** (only a test-only `InMemoryAudioSource` that `src/` cannot reference) → build `PcmAudioSource` in Stage 1.2.

ONNX adapter template to mirror (`AudioClaudio.Infrastructure.Transcription`):
- `TranskunModel : IDisposable` owns `InferenceSession _session = new(onnxPath)`, `Dispose() => _session.Dispose()`. `TranskunTranscriber(string modelDir, IFourierTransform fft)` loads multiple ONNX files from a **directory**. **This is the STFT-outside-the-graph precedent** — copy its shape.

Model locators (`AudioClaudio.Cli.Composition`, NOT Infrastructure):
- `TranskunModelLocator.Resolve(string? explicitDir = null)` walks up from `AppContext.BaseDirectory` for `fixtures/models/transkun/` — **subdirectory pattern; mirror it for `fixtures/models/spleeter/`.**

Synthesis oracle + closed-loop machinery to mirror:
- `AudioClaudio.Infrastructure.Synthesis.MeltySynthSynthesizer(string soundFontPath, int midiProgram = 0, int releaseTailMilliseconds = 1500) : ISynthesizer` — **GM program is per-instance**, so the oracle renders bass = program 32, sax = 66, piano = 0, sums the buffers, and has ground-truth stems for free. `float[] Render(IReadOnlyList<NoteEvent> notes, SampleRate sampleRate)`.
- `tests/AudioClaudio.Tests/ClosedLoop/PolyphonicClosedLoop.cs` (const `GateThreshold`, `GateToleranceMs`, `CreateSynthesizer()`, `RenderAndTranscribe(...)`), `PolyphonicClosedLoopGen.Cases(int count, int seed = 4242)` (plain seeded `System.Random`), `PolyphonicClosedLoopTests` `[Fact][Trait("Category","Slow")]`, env override `POLY_CLOSED_LOOP_CASES` — **mirror all of this for `SeparationClosedLoop*`.**

CLI kernel:
- `AudioClaudio.Cli.AppBuilder.Build(StringBuilder logBuffer, bool noColor) : CommandLineApp` is the composition root.
- `new CliCommand(name, summary).WithArgument(CliArgument).WithOption(CliOption).WithExample(string)` → `app.Register(cmd, Func<ParsedArgs, TextWriter, TextWriter, int> handler)`.
- `new CliOption("--out-dir", OptionKind.Path, "...", defaultValue: "out")`. `transcribe` writes to out-dir root only (no archive) — `separate` mirrors that simpler convention.

### 0.3 Verified Spleeter facts to BUILD against (source: 3 primary-source agents, 2026-07-13)

Architecture:
- **5 separate U-Nets** (one per stem: vocals/drums/bass/piano/other), `softmax_unet` — the 5 branches' logit masks are stacked and passed through **one joint Softmax across the stem axis**, so masks sum to 1. **Because that softmax is a pure, non-learned op, we lift it OUT of the graph** (exactly as this repo already lifts the STFT and the ratio-mask): export **5 independent per-branch ONNX files, each emitting its branch's raw pre-softmax logit** — the *proven* 2-stem per-branch recipe applied 5×, NOT an unprecedented coupled graph — and do the cross-branch softmax in C#.
- **Three non-learned stages, all *outside* the graph, all in C#:** (1) the cross-branch **softmax** over the 5 stacked per-branch logits → 5 masks; (2) mask **× mixture magnitude** → 5 estimated magnitudes; (3) the **power-ratio remask** (`separation_exponent = 2`) applied to the mixture STFT for reconstruction. The ONNX graphs contain only the learned per-branch U-Nets.

Exact DSP params (identical across Spleeter configs):
- sample_rate **44100**, n_fft/frame_length **4096**, hop/frame_step **1024**, window **Hann periodic**, full bins 2049, **F = 1024 bins fed** (hard crop → ~11.025 kHz ceiling; high band zero-filled on output — a *weight property*, document it), **T = 512** frame segments (pad+partition to a multiple of 512), **n_channels = 2 (stereo, hard requirement)** — mono input must be upmixed L = R.
- Framing is **zero-pad-then-hop, NOT centered**: prepend one full `frame_length` of zeros, `pad_end=True`; crop the leading pad after reconstruction. (sherpa-onnx matches this with `center=False`.)
- Reconstruction: mixture **phase reused** for every stem; ratio-mask → zero-extend 1024→2049 → × mixture complex STFT → iSTFT (Hann-periodic) × **2/3 window-compensation** → crop leading pad.

Weights + export:
- Released weights: TF1 `Saver` checkpoint (`5stems.tar.gz`, Deezer v1.4.0, MIT), ~198 MB uncompressed, 5 U-Nets share one checkpoint (~39 MB/net fp32, ~10–20 MB/net at fp16/int8).
- Only a **2-stem** ONNX exists publicly; **no 5-stem export exists anywhere** — but lifting the softmax to C# (see Architecture, above) reduces it to the *proven* per-branch 2-stem recipe applied 5×, exporting each branch's logit as its own ~39 MB fp32 ONNX. Recipe to extend: `k2-fsa/sherpa-onnx/scripts/spleeter` + `csukuangfj/spleeter-torch` `unet.py` (both **Apache-2.0**): TF ckpt → frozen `.pb` → hand-mapped PyTorch `state_dict` → `torch.onnx.export` (per branch). **Avoid** `bigcash/spleeter-pytorch-mnn` (**GPL-3.0**).

### 0.4 The dependency rule (mechanical, per `CLAUDE.md` §3)

| New code | Project | Why it's allowed there |
|---|---|---|
| `ISourceSeparator`, `SeparatedStem` | `AudioClaudio.Application` (`.Ports`) | a port; depends only on Domain (`IAudioSource`) |
| `Radix2Fft.Inverse`, `SpleeterSpectralFrontEnd`, `SiSdr` | `AudioClaudio.Domain` | pure DSP/math, BCL-only (R0.2) |
| `PcmAudioSource`, `SpleeterModel`, `SpleeterSourceSeparator` | `AudioClaudio.Infrastructure` | ONNX runtime + audio I/O live here |
| `SeparatorModelLocator`, `separate` verb | `AudioClaudio.Cli` | composition root only |
| `SeparationClosedLoop*`, unit tests | `tests/AudioClaudio.Tests` | — |

The mechanical test (§3): can `AudioClaudio.Domain` see `InferenceSession` or a file path? **No.** If a task needs that, the boundary is being broken — stop and raise it.

---

## Stage 1.0 — The export spike (offline Python) — **HARD GATE**

**This stage is a spike, not TDD**, because its central facts are unverified: whether the 2-stem recipe's per-branch op-name offsets generalize to 5 branches, the exact op names of the softmax/stack combiner in the frozen graph, and whether the softmax-coupled path holds tight ONNX↔TF parity. Over-specifying code here would be dishonest. It has **exit criteria and a decision gate instead of assertions.**

**Environment:** disposable **Linux x86_64** venv (GitHub Actions runner, Docker, or Colab — TF 2.12.1 has no reliable arm64 macOS wheel; this is a one-time offline step exactly like `export_transkun.py`). Deps: `tensorflow==2.12.1`, `torch`, `onnx`, `onnxruntime`, `numpy`.

**Files:**
- Create: `fixtures/models/spleeter/export_spleeter.py` (the owned, committed export script)
- Create (output, committed): **5 per-branch ONNX files** `fixtures/models/spleeter/{vocals,drums,bass,piano,other}.onnx` (**fp32, ~39 MB each — NO quantization, per Cornelius 2026-07-14**), plus `LICENSE.spleeter`, `MODEL_CARD.md`, `README.md`, `manifest.json` (all under `fixtures/models/spleeter/`; the Transkun dir is the multi-file precedent)
- Create (parity fixtures, committed): `fixtures/models/spleeter/parity/ref_mix_spec.f32`, `parity/ref_stem_specs.f32`, `parity/manifest.json`

**Spike task S0 — reproduce the 2-stem recipe end-to-end** (proves the toolchain before touching 5-stem). Pull Deezer v1.4.0 `2stems.tar.gz`, run the sherpa-onnx `convert_to_pb` → `convert_to_torch` → `export_onnx` path, and confirm ONNX-vs-TF parity on one segment. *Exit:* a working 2-stem ONNX + a parity harness you trust.

**Spike task S1 — map the 5 branches' weights** (the part most likely to surprise). Freeze `5stems.tar.gz`; **enumerate `graph.get_operations()`** and confirm (do NOT assume) the per-branch Conv2D/Conv2DTranspose/BatchNorm op-name offsets, so each branch's weights can be loaded into its own PyTorch `UNet`. We do NOT need the in-graph `Softmax`/`stack`/`*_spectrogram/mul` ops (those become C#) — only each branch's raw **logit** output. *Exit:* a written op-name→branch map committed as a comment block in `export_spleeter.py`. **If the offsets do not generalize cleanly, STOP and escalate to Cornelius — this is the pre-registered bail-out point.**

**Spike task S2 — export 5 per-branch logit ONNX** (proven 2-stem-recipe shape, applied 5×). Reuse `csukuangfj/spleeter-torch`'s `UNet` (Apache-2.0) per branch with its head set to emit the **raw logit** (no sigmoid, no softmax, no ×input — all three are C#); load each branch's weights via the S1 map; `torch.onnx.export` each branch separately (one input → one logit output, opset 13, dynamic time axis) to `{stem}.onnx`. *Exit:* 5 fp32 `{stem}.onnx` files (~39 MB each).

**Spike task S3 — validate to the repo's bar (not sherpa's loose one).** Two checks: (i) each branch's ONNX **logit** matches the frozen TF graph's per-branch logit tensor; (ii) the **full path** — 5 ONNX logits → (numpy) softmax → ×magnitude → ratio-mask → iSTFT — matches native `spleeter separate` end-to-end on a fixture WAV. **Target `export_transkun.py`'s standard: correlation ≈ 1.0, max-rel-error ~1e-5–1e-6** (NOT sherpa's `atol=1e-1`). Persist the parity fixtures. **No quantization** (per Cornelius — prior quantization caused a drastic transcription-quality loss; fp32 only). *Exit:* 5 committed fp32 ONNX + parity fixtures + parity numbers in `MODEL_CARD.md`.

**Spike task S4 — write `MODEL_CARD.md` + `LICENSE.spleeter`.** Document: input/output tensor contract (shapes, stem order), the exact STFT params, the ~11 kHz ceiling as a known weight property, the parity numbers, and — prominently — the **README-vs-JOSS license ambiguity** and the non-commercial-scope posture (§0.1). Commit Deezer's MIT text as `LICENSE.spleeter`.

**GATE (all must hold before any C# work begins):**
1. All 5 `{stem}.onnx` exist and load in `onnxruntime`.
2. Per-branch logit parity + full-path end-to-end parity meet the repo's tight bar on the committed fixtures.
3. End-to-end audio parity vs native `spleeter separate` is audibly/numerically faithful.
4. **Each file < 100 MB.** Per-branch fp32 files are ~39 MB each — comfortably under GitHub's 100 MB per-blob limit, so **no quantization** (fp32, per Cornelius: prior quantization caused a drastic transcription-quality loss) and **no Git LFS** needed. (Total ~196 MB added across 5 files — a notable but accepted repo-size increase; the repo already commits Transkun 53 MB + Basic Pitch + a SoundFont.)
5. `MODEL_CARD.md` + `LICENSE.spleeter` + `README.md` committed.

**If the gate fails** (the per-branch weight-mapping doesn't generalize, or parity can't be hit), the honest options (for Cornelius, not unilateral): iterate the op-name mapping; or reconsider the model (KUIELab-MDX-Net ships a native ONNX but has no piano stem) under a possible §1.7 relaxation. **Do not proceed to Stage 1.1 on a failed gate.** (Per-branch export already removed the artifact-size blocker — quantization/LFS are off the table, fp32 per Cornelius.)

**Commit** (gitbutler skill): `feat(fixtures): committed Spleeter 5-stem ONNX + owned export script + license`.

---

## Stage 1.1 — Reconstruction DSP in the Domain (pure, TDD)

The repo has only ever gone audio→notes; **spectrogram→waveform is a brand-new capability.** All pure, BCL-only, in `AudioClaudio.Domain` (namespace `AudioClaudio.Domain.Separation`).

### Task 1.1.a — Inverse FFT on `IFourierTransform` / `Radix2Fft`

**Design note (from the review):** `Radix2Fft.Forward` is an **instance** method behind the injected `IFourierTransform` interface (`Complex[] Forward(double[])`) — the repo wires it into `SpectralFrontEnd`/`TranskunMelFrontEnd` by constructor injection, never as a static call. To keep that seam, add `Inverse` **to `IFourierTransform`** (and its sole `Radix2Fft` implementation), and **inject `IFourierTransform`** into the Stage 1.1 DSP types — do NOT hardcode the concrete `Radix2Fft`.

**Files:**
- Modify: `src/AudioClaudio.Domain/Spectral/IFourierTransform.cs` (add `double[] Inverse(Complex[] spectrum)`)
- Modify: `src/AudioClaudio.Domain/Spectral/Radix2Fft.cs` (implement `Inverse`, matching `Forward`'s precision)
- Test: `tests/AudioClaudio.Tests/Domain/Radix2FftInverseTests.cs`

**Step 1 — failing test (round-trip):**
```csharp
[Fact]
public void Inverse_of_Forward_recovers_the_signal()
{
    var fft = new Radix2Fft();
    double[] x = Enumerable.Range(0, 4096).Select(i => Math.Sin(2*Math.PI*440*i/44100.0)).ToArray();
    Complex[] spectrum = fft.Forward(x);
    double[] roundTrip = fft.Inverse(spectrum);
    for (int i = 0; i < x.Length; i++)
        Assert.True(Math.Abs(x[i] - roundTrip[i]) < 1e-9, $"idx {i}");
}
```
**Step 2 — run, expect FAIL** (`Inverse` not defined). Run: `dotnet test --filter Radix2FftInverseTests`.
**Step 3 — implement** via the conjugate trick (`ifft(X) = conj(fft(conj(X)))/N`), reusing `Forward`. Keep `double` precision.
**Step 4 — run, expect PASS.**
**Step 5 — Commit** (gitbutler skill): `feat(domain): inverse radix-2 FFT`.

### Task 1.1.b — `SpleeterSpectralFrontEnd` (forward: waveform → cropped magnitude segments + retained complex mixture STFT)

**Files:**
- Create: `src/AudioClaudio.Domain/Separation/SpleeterSpectralFrontEnd.cs`
- Test: `tests/AudioClaudio.Tests/Domain/SpleeterSpectralFrontEndTests.cs`

Reproduce Spleeter's exact scheme (§0.3): zero-pad-then-hop (prepend one 4096 frame of zeros, pad tail), Hann **periodic** window, n_fft 4096 / hop 1024, produce full 2049-bin complex STFT, crop magnitude to F=1024, pad+partition time to a multiple of T=512. Output both the cropped magnitude segments (net input) and the retained full complex STFT (for reconstruction).

**Tests:** (1) a golden parity test vs a committed numpy reference produced in Stage 1.0 (`ref_mix_spec.f32`) within tight tolerance; (2) frame-count/shape properties (T-multiple padding); (3) the Hann-periodic coefficients match a hand-checked reference. Test-first, then implement, then commit: `feat(domain): Spleeter STFT front end`.

### Task 1.1.c — Inverse reconstruction (overlap-add iSTFT + 2/3 window compensation + leading-pad crop)

**Files:**
- Create: `src/AudioClaudio.Domain/Separation/SpleeterReconstruction.cs`
- Test: `tests/AudioClaudio.Tests/Domain/SpleeterReconstructionTests.cs`

**Tests:** (1) **round-trip property** — `Reconstruct(FrontEnd(x)) ≈ x` within tolerance for generated signals (the reconstruction trial balance); (2) golden parity vs a committed numpy iSTFT reference on one fixture; (3) the 2/3 window-compensation constant is applied exactly once. Implement `Inverse` (via Task 1.1.a), OLA, ×2/3, crop leading pad. Commit: `feat(domain): overlap-add iSTFT reconstruction`.

---

## Stage 1.2 — The port, the in-memory source, the locator (TDD)

### Task 1.2.a — `ISourceSeparator` + `SeparatedStem`

**Files:**
- Create: `src/AudioClaudio.Application/Ports/ISourceSeparator.cs`
- Create: `src/AudioClaudio.Application/Ports/SeparatedStem.cs`

```csharp
namespace AudioClaudio.Application.Ports;
public readonly record struct SeparatedStem(string Name, IAudioSource Audio);
public interface ISourceSeparator
{
    IReadOnlyList<SeparatedStem> Separate(IAudioSource mix);
}
```
No behavior yet → a compile check is the "test." Commit: `feat(app): ISourceSeparator port and SeparatedStem`.

### Task 1.2.b — `PcmAudioSource` (the missing in-memory `IAudioSource`)

**Files:**
- Create: `src/AudioClaudio.Infrastructure/Audio/PcmAudioSource.cs`
- Test: `tests/AudioClaudio.Tests/Infrastructure/PcmAudioSourceTests.cs`

Mirror the test-only `InMemoryAudioSource` (~10 lines): `PcmAudioSource(float[] samples, SampleRate rate, FrameParameters parameters)` wrapping `Framing.Split`. **Test:** frames tile the buffer at the declared hop, `ReconstructMono(source.Frames)` ≈ the input, `Frame.Rate` is carried. Test-first → implement → commit: `feat(infra): in-memory PcmAudioSource adapter`.

### Task 1.2.c — `SeparatorModelLocator`

**Files:**
- Create: `src/AudioClaudio.Cli/Composition/SeparatorModelLocator.cs`
- Test: `tests/AudioClaudio.Tests/Cli/SeparatorModelLocatorTests.cs`

Copy `TranskunModelLocator` exactly (it resolves a **directory**), retargeted to `fixtures/models/spleeter/` (probe for e.g. `piano.onnx` to confirm the dir): explicit path wins; else walk up from `AppContext.BaseDirectory`; else `FileNotFoundException` naming the `--model` flag. Test the explicit-path and not-found branches. Commit (gitbutler skill): `feat(cli): Spleeter model locator`.

---

## Stage 1.3 — The Spleeter ONNX adapter (TDD, validated against the Stage-1.0 fixtures)

**Files:**
- Create: `src/AudioClaudio.Infrastructure/Separation/SpleeterModel.cs` (`: IDisposable`, owns the **5 per-branch `InferenceSession`s** loaded from the model dir, mirroring how `TranskunTranscriber` loads multiple ONNX from one directory)
- Create: `src/AudioClaudio.Infrastructure/Separation/SpleeterSourceSeparator.cs` (`: ISourceSeparator, IDisposable`)
- Test: `tests/AudioClaudio.Tests/Infrastructure/SpleeterSourceSeparatorTests.cs`

**Pipeline the adapter implements** (per §0.3): `mix.Frames` → `ReconstructMono` → upmix mono→stereo (**L=R fake stereo — see the stereo-cue-loss risk below**) → `SpleeterSpectralFrontEnd` (cropped magnitude segments + retained complex STFT) → `SpleeterModel.Run` (feed the magnitude to all **5 per-branch ONNX → 5 raw logits**) → **cross-branch softmax in C#** (stack the 5 logits, softmax across the stem axis) → **× mixture magnitude** (5 estimated magnitudes) → power-ratio remask (exp=2) across the 5 → zero-extend 1024→2049 → × mixture complex STFT (mixture phase) → `SpleeterReconstruction` per stem → wrap each stem's mono PCM in a `PcmAudioSource` → return `[vocals, drums, bass, piano, other]` as `SeparatedStem`s.

**Extra TDD unit test (the lifted-out coupling):** the C# cross-branch softmax + ×magnitude step is unit-tested in isolation against a committed numpy reference (produced in Stage 1.0's S3) — it's the one piece that moved *out* of the graph, so it gets its own tight-tolerance golden, not just the end-to-end parity test.

**Tensor contract:** the exact `SpleeterModel.Run` input/output tensor shapes (e.g. `[batch/2ch, num_splits, 512, 1024]`) and channel layout are **pinned during Stage 1.0's spike** (S2/S3) and recorded in `MODEL_CARD.md` — Stage 1.3 consumes that contract verbatim. Because the upmix makes L=R identical, `SpleeterSpectralFrontEnd` computes one magnitude and duplicates it across the two channel slots; if the exported graph is genuinely single-channel-tolerant, prefer that. Do not guess the shape here — read it off the committed model.

**Tests (TDD):**
1. **Parity** — feed the Stage-1.0 fixture mix; assert each returned stem's spectrum matches the committed `ref_stem_specs.f32` within tolerance (the adapter reproduces the Python reference).
2. **Contract** — `Separate` returns exactly 5 named stems; each `Audio` is a non-empty `IAudioSource` at 44100 Hz; names are the fixed stem order.
3. **Determinism** — same input twice on the **same machine** → bit-identical stem PCM (§4 non-negotiable #3). Caveat, matching `PolyphonicClosedLoop`'s own documented behavior: ONNX Runtime SIMD mixdown can drift across CPU architectures, so this is a same-machine guarantee, not cross-architecture bit-exactness.
4. **Disposal** — `Dispose` releases the session (no leak across repeated construction).

Write each test first (red), implement minimally (green), commit per test. Final commit: `feat(infra): Spleeter 5-stem ONNX source separator`.

---

## Stage 1.4 — The SI-SDR closed-loop oracle + CI gate (the falsifiable proof)

This is the on-brand part: the synthesizer is the oracle, so no hand-labeled data is needed.

### Task 1.4.a — `SiSdr` metric (Domain, pure)

**Files:**
- Create: `src/AudioClaudio.Domain/Separation/SiSdr.cs`
- Test: `tests/AudioClaudio.Tests/Domain/SiSdrTests.cs`

`static double SiSdr(ReadOnlySpan<float> estimate, ReadOnlySpan<float> reference)` — scale-invariant SDR: project estimate onto reference, `10·log10(‖s_target‖² / ‖e_noise‖²)`. **Tests:** identical signals → +∞ (or a large sentinel); scaled copy (2× reference) → still ~+∞ (scale invariance — the property that names the metric); orthogonal noise → low/negative dB; known hand-computed case. Test-first → implement → commit: `feat(domain): scale-invariant SI-SDR metric`.

### Task 1.4.b — `SeparationClosedLoop` + generator (mirror the polyphonic pattern)

**Files:**
- Create: `tests/AudioClaudio.Tests/ClosedLoop/SeparationClosedLoop.cs`
- Create: `tests/AudioClaudio.Tests/ClosedLoop/SeparationClosedLoopGen.cs`

`SeparationClosedLoopGen.Cases(int count, int seed = 4242)` uses a plain seeded `System.Random` (NOT CsCheck — matches `PolyphonicClosedLoopGen`) to build, per case, a few short **monophonic** MIDI lines assigned to distinct GM programs (e.g. bass=32, tenor sax=66, piano=0). `SeparationClosedLoop` renders each line on its own `MeltySynthSynthesizer(soundFontPath, program)`, **sums the per-instrument PCM into a mix with known ground-truth stems**, runs the separator, and returns `(mix, groundTruthStems, recoveredStems)`. Consts `GateThresholdDb` and `CreateSeparator()`/`CreateSynthesizer()` mirror `PolyphonicClosedLoop`.

**Threshold policy (honest):** `GateThresholdDb` is a *policy choice* set once, measured on the seed-4242 corpus after the first green run and then frozen — exactly how the 0.75 polyphonic F1 bar was set. It is not a research fact. Leave a `// TODO: freeze after first measured run` and record the measured number in the test + `docs/CORPUS.md`.

### Task 1.4.c — the gated test

**Files:**
- Create: `tests/AudioClaudio.Tests/ClosedLoop/SeparationClosedLoopTests.cs`

```csharp
[Fact]
[Trait("Category", "Slow")]
public void Separation_closed_loop_meets_committed_SI_SDR_gate()
{
    int cases = EnvOrDefault("SEPARATION_CLOSED_LOOP_CASES", 16);
    var perStemSiSdr = new List<double>();
    foreach (var c in SeparationClosedLoopGen.Cases(cases))
    {
        var (mix, truth, recovered) = SeparationClosedLoop.RenderAndSeparate(c);
        // align recovered stem name -> ground-truth instrument, score SI-SDR on the pitched stems (piano/bass/other)
        perStemSiSdr.AddRange(SeparationClosedLoop.ScorePitchedStems(truth, recovered));
    }
    double median = Median(perStemSiSdr);
    Assert.True(median >= SeparationClosedLoop.GateThresholdDb,
        $"median SI-SDR {median:F2} dB < gate {SeparationClosedLoop.GateThresholdDb} dB");
}
```

**Run:** `dotnet test --filter Separation_closed_loop_meets_committed_SI_SDR_gate`. First run: measure the median, set `GateThresholdDb` just below it, re-run green. Persist worst cases to a quarantine dir on failure (mirror the polyphonic suite's R9.3 discipline). Commit: `test: synthesize→separate SI-SDR closed-loop gate`. This is a new CI suite, **kept separate** from `PolyphonicClosedLoopTests` so neither number contaminates the other.

### Task 1.4.d — `docs/CORPUS.md` new tier

Add a **separation SI-SDR** tier documenting the corpus (seed 4242, N cases, the GM programs used), the measured median dB, and the gate — the same honest framing as the existing mono/poly tiers. **Also add a row to `docs/CORPUS.md`'s existing ranked "guarantee hierarchy (ranked, never flattened)" table**, placing separation as a *statistical* tier strictly **below** the existing ones (monophonic bit-exact / polyphonic-batch F1 / Transkun parity) — that table is the document's guardrail against exactly the flattening the design §2 forbids, so a new tier must appear in it, not just as prose. Commit (gitbutler skill): `docs: source-separation SI-SDR corpus tier`.

---

## Stage 1.5 — The `separate` CLI verb

**Files:**
- Modify: `src/AudioClaudio.Cli/AppBuilder.cs` (register the command in `Build`)
- (optional) Create: `src/AudioClaudio.Cli/Commands/SeparateCommand.cs` if the handler is non-trivial (mirror `TranscribeCommand`)
- Test: `tests/AudioClaudio.Tests/Cli/SeparateCommandTests.cs`

Register `new CliCommand("separate", "split a mixed recording into instrument stem WAVs").WithArgument(mixWav).WithOption(outDir "out").WithOption(--model)` → handler constructs `WavAudioSource.FromFile(...)`, `new SpleeterSourceSeparator(SeparatorModelLocator.Resolve(p.String("model")))`, calls `Separate`, and writes each stem via `WavFileWriter.Write(Path.Combine(outDir, $"{stem.Name}.wav"), Framing.ReconstructMono(stem.Audio.Frames.ToList()), rate)` (`Frames` is `IEnumerable<Frame>`; `ReconstructMono` wants `IReadOnlyList<Frame>`, hence `.ToList()`). Root-only output (no session archive — mirror `transcribe`).

**Test:** an end-to-end CLI test on a tiny committed mix fixture → asserts 5 stem WAVs are written and are readable/non-empty. Commit: `feat(cli): separate command`.

---

## Stage 1 — Verify & stop

1. `dotnet build` clean; `dotnet format` clean.
2. `dotnet test` green (fast suite); `dotnet test --filter Category=Slow` green (includes the new SI-SDR gate).
3. `AudioClaudio.Domain` still references nothing beyond the BCL (§3 mechanical check).
4. `separate <mix.wav>` writes 5 stem WAVs; the piano stem is audibly a piano reduction of the mix.
5. **Human gate (one-time, R11.2-style):** does a separated **piano** stem from a real, rights-cleared mixed recording sound structurally intact on playback? Record PASS/date in `DECISIONS.md`. This is a stand-in satisfied once, *not* the permanent arrangement gate (that arrives only at Stage 4).
6. Update `docs/plans/README.md` status + the `CLAUDE.md` "Where the project is right now" note.
7. **STOP for review before Stage 2** (per-stem transcription), per the one-step-at-a-time rule.

---

## Deferred — Phase-2 upgrade path (recorded, not built now)

**Hybrid non-piano separation.** Spleeter's non-piano stems are its weakest (bass ~5 dB, "other" ~4 dB; ~11 kHz ceiling). If Stage 1.4's per-stem SI-SDR numbers (or the human gate) show Spleeter's bass/other are too weak to transcribe usefully, swap in a stronger, easier model **for the non-piano stems only**, behind the same `ISourceSeparator` port: **KUIELab-MDX-Net (Track A)** is the natural pick — it ships a **native ONNX** (no export to own), carries an explicit author CC-BY-4.0 grant, and scores ~9 dB. A thin `HybridSourceSeparator : ISourceSeparator` would take Spleeter's `piano` stem + KUIELab's `bass`/`drums`/`other`. This is a **data-driven** upgrade — built only if the Stage-1 numbers call for it, never speculatively (YAGNI); the port already supports it, so nothing here forecloses it. (KUIELab is MUSDB-trained, so adding it re-introduces the MUSDB non-commercial shadow — acceptable under this project's non-commercial scope, but a licensing note to record if/when it's built.)

---

## Risks & honest unknowns (carried forward, not hidden)

- **The export spike (1.0) is still the load-bearing risk, but smaller now** — lifting the softmax to C# turns the export into the *proven* 2-stem per-branch recipe applied 5×, so the only real unknown is whether the per-branch op-name mapping generalizes across the 5 branches. The previously-"unprecedented" softmax-coupled graph is gone. Stage 1.0's gate is the pre-registered bail-out.
- **Committed-artifact size — resolved by per-branch export.** Splitting into 5 fp32 files (~39 MB each) keeps every blob under GitHub's 100 MB limit with **no quantization** (Cornelius, 2026-07-14: prior quantization caused a drastic transcription-quality loss — fp32 only) and **no Git LFS**. Cost: ~196 MB added across 5 files — a notable but accepted repo-size increase.
- **Spleeter quality on jazz is unmeasured** — all its published numbers are pop/rock; the ~11 kHz ceiling and weak piano stem are real. The SI-SDR gate measures *intrinsic* recovery on clean synthetic mixes, not jazz fidelity — state that plainly (same posture as the Basic Pitch F1 vs real-jazz gap).
- **Real stereo separation cues are unavailable — a genuine quality ceiling.** Spleeter is stereo-native and leans on inter-channel cues to disambiguate instruments, but the repo's `IAudioSource` contract is mono (R2.1) and `WavAudioSource.Decode` force-downmixes any input to mono. So `separate` feeds Spleeter a mono signal upmixed to L=R *fake* stereo — a supported-but-degraded Spleeter mode, not true stereo separation. The SI-SDR closed-loop oracle is *also* built from mono-summed synth output, so **the CI gate is structurally blind to this degradation** — only the human gate (Verify #5) can catch it. A genuinely stereo-capable source path is a named future enhancement, deliberately out of Stage-1 scope to keep the mono `IAudioSource` contract intact. **Cornelius's call (2026-07-14): accept the mono flatten for now** — stereo input is downmixed to mono and Spleeter runs on L=R; the stereo ceiling is a documented, accepted limitation, not an open question.
- **License ambiguity** — recorded honestly in `MODEL_CARD.md` + `DECISIONS.md`; non-commercial scope means no practical exposure, but it is not pretended to be a clean grant.
