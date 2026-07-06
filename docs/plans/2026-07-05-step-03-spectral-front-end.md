# Step 3 — The Spectral Front End — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Build-spec step:** Section 6 Step 3 (R3.1, R3.2, R3.3)
**Goal:** Turn a `Frame` into a per-frame magnitude spectrum by applying a Hann window at exactly one place and running a forward FFT — the pure DSP substrate both pitch (Step 4) and onset (Step 5) detection sit on top of.
**Architecture:** All the front-end logic lives in `AudioClaudio.Domain` (layer 1, BCL-only). The FFT is reached through a Domain-owned abstraction, `IFourierTransform`, so the concrete transform is a swappable seam. Nothing here reads a device, a clock, or a file — frames in, spectra out.
**Tech Stack:** `System.Numerics.Complex` (BCL) for the hand-rolled radix-2 path, or NWaves (MIT) for the library path (see the DECISION GATE); xUnit (Apache-2.0) and CsCheck (MIT) for tests.
**Prerequisites:** Step 0 (scaffold, dependency rule, CI) and Step 1 (`SampleRate`) green and committed; Step 2 (`Frame`, signal generator) green and committed — this step consumes `Frame` and reuses Step 2's deterministic sine generator. Section 1 rule 3: do not start until Step 2's *Verify* is green.
**Commit (spec):** `feat(domain): windowed spectral front end`

---

## Approach

A microphone or a WAV file gives us a stream of overlapping *frames* — short windows of PCM samples (Step 2). To ask "what pitches are sounding," we move each frame from the time domain (amplitude over time) into the frequency domain (energy per frequency). Three ideas carry this step.

**Windowing (R3.1).** A frame is a finite slice cut out of a longer signal. Slicing it abruptly is like multiplying by a rectangle, and the sharp edges smear energy across the whole spectrum — *spectral leakage* — which would drown weak partials and confuse onset detection. A *window* tapers the frame smoothly to zero at both ends before the transform. We use the **Hann window**, `w[n] = 0.5·(1 − cos(2π·n/(N−1)))` for `n = 0..N−1`: it is zero at both endpoints, symmetric, and a standard choice for audio. The non-negotiable is that the window is applied at *exactly one place* — inside the spectral front end and nowhere else — so every downstream stage sees the same, consistently-tapered spectra.

**The Fourier transform (R3.2).** The Discrete Fourier Transform maps `N` time samples to `N` complex numbers `X[k]`, where bin `k` corresponds to frequency `k·sampleRate/N` Hz. Computed naively it costs `O(N²)`; the **radix-2 Cooley–Tukey FFT** does it in `O(N·log N)` when `N` is a power of two, by recursively splitting even/odd samples (we implement the classic iterative bit-reversal-plus-butterflies form). We take the *magnitude* `|X[k]| = √(re² + im²)` of each bin. For a real input the spectrum is symmetric, so only bins `0 .. N/2` (DC through the Nyquist frequency) carry independent information — that half-spectrum is what we keep. The bin-to-frequency map is what makes the result pitch-meaningful.

**Parseval as the trial balance.** The DFT conserves energy: `Σ x[n]² = (1/N)·Σ |X[k]²|`. This is Parseval's theorem, and it is this step's version of the ledger's trial balance — if the two sides disagree, the transform is wrong. We assert it as a property over many generated signals. The example check is complementary: a windowed sine at frequency `f` must peak in the bin nearest `f`.

This step builds *only* the front end. Step 4's YIN detector is autocorrelation-based and does not use this FFT; Step 5's onset detector (spectral flux) is the first real consumer of these magnitude spectra — it will zip each spectrum with its source frame's `Start` position (both streams are ordered), so the front end deliberately stays position-agnostic. No pitch or onset logic is written here.

---

## ⚠ DECISION GATE (Cornelius owns this — Section 1 rule 2)

**The fork: which FFT implementation backs `IFourierTransform`?**

Both options satisfy R3.1–R3.3 identically; the whole test suite runs through the `TestFft.Create()` seam, so this decision changes exactly one factory line plus *where one file lives* — not the tests, not the window, not the front end.

- **Option A — hand-rolled radix-2 FFT, in `AudioClaudio.Domain`.**
  About 60 lines of iterative Cooley–Tukey over `System.Numerics.Complex`.
  - *For:* Zero dependencies; keeps `AudioClaudio.Domain` strictly BCL-only, honoring **R0.2** cleanly. A legitimate craft exercise consistent with this repo's hand-rolled character (it also hand-rolls RIFF parsing and MusicXML). Full `double` precision.
  - *Against:* We own the correctness (mitigated by the Parseval property). No free resampling for Phase 2.

- **Option B — NWaves (MIT), adapter in `AudioClaudio.Infrastructure`.**
  A thin `NWavesFourierTransform : IFourierTransform` wrapping `NWaves.Transforms.Fft`.
  - *For:* Battle-tested; brings resampling for free, which Phase 2 wants for Basic Pitch's 22.05 kHz input.
  - *Against:* A third-party NuGet in the graph. **R0.2 tension:** `AudioClaudio.Domain` SHALL reference nothing beyond the BCL, so NWaves cannot live in Domain. The resolution that keeps *both* R0.2 and R3.2 true is to put the NWaves adapter in **Infrastructure** and have Domain depend only on the BCL-only `IFourierTransform` abstraction. NWaves works in single precision (`float`), so the Parseval/DC tolerances are set loose enough to pass either path (they still pass the `double` path with orders of magnitude to spare).

**The spec states no preference** ("Either satisfies the requirements"); it only notes the Phase-2 pull toward NWaves and the repo-character pull toward hand-rolling. Per the Foundation, when a Foundation convenience (a library) and the constitution (R0.2, dependency rule) tension, the constitution wins — hence the "adapter in Infrastructure" resolution above rather than a Domain package reference. Both options preserve the dependency rule.

**Cross-step note (Step 9, load-bearing):** the Step 9 `TranscriptionPipeline` (Application) receives its `IFourierTransform` by constructor injection and never names a concrete FFT — so under Option A it is handed `new Radix2Fft()` (Domain) and under Option B the CLI composition root hands it `new NWavesFourierTransform()` (Infrastructure); Application → Infrastructure is never introduced either way.

**Resolution:** record in DECISIONS.md before implementing; do not silently pick a side.

Only **Task 2** changes with the decision. Under Option A its files are `src/AudioClaudio.Domain/Spectral/Radix2Fft.cs` (no csproj change, `Complex` is BCL). Under Option B they are `src/AudioClaudio.Infrastructure/Spectral/NWavesFourierTransform.cs` plus a `NWaves` `<PackageReference>` in `AudioClaudio.Infrastructure.csproj`, and the one commented line in `TestFft`. Tasks 1, 3, and 4 are identical either way.

---

## Requirements coverage

| Requirement | Task(s) | Proven by (test) |
|---|---|---|
| **R3.1** — Hann window applied at exactly one place | Task 1 (window defined once), Task 4 (applied only inside `SpectralFrontEnd`) | `Hann_matches_the_reference_formula`, `Hann_endpoints_are_zero`, `Hann_is_symmetric`; `Analyze_applies_the_hann_window_to_the_frame` |
| **R3.2** — FFT available to the domain, magnitude spectra per frame | Task 2 (`IFourierTransform` + impl), Task 3 (`MagnitudeSpectrum`), Task 4 (`SpectralFrontEnd.Analyze`) | `Forward_satisfies_Parsevals_theorem`; `FrequencyOf_maps_bins_to_hertz`, `BinCount_is_half_frame_plus_one`; `Peak_bin_of_windowed_sine_is_the_bin_nearest_the_frequency` |
| **R3.3** — Pure: frames in, spectra out, no state beyond the declared overlap | Task 4 (stateless per-frame transform, validation) | `Analyze_is_deterministic_bit_for_bit`, `Analyze_rejects_a_frame_whose_length_differs_from_the_configured_size`, `Constructor_rejects_non_power_of_two_frame_size` |

Non-negotiables asserted here: **#3 determinism** (`Analyze_is_deterministic_bit_for_bit`, and `PeakBin`'s defined tie-break) and **#1 integer sample time** (`MagnitudeSpectrum` carries its `SampleRate`; `FrequencyOf` is the only Hz conversion, at the display edge).

---

## Task 1: The Hann window

Use @superpowers:test-driven-development for every task: write the failing test, watch it fail, write the minimal code, watch it pass.

**Files:**
- Create: `src/AudioClaudio.Domain/Spectral/HannWindow.cs`
- Test: `tests/AudioClaudio.Tests/Spectral/HannWindowTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System;
using AudioClaudio.Domain.Spectral;
using Xunit;

namespace AudioClaudio.Tests.Spectral;

[Trait("Category", "Fast")]
public class HannWindowTests
{
    [Fact]
    public void Hann_matches_the_reference_formula()
    {
        const int n = 16;
        double[] w = HannWindow.Coefficients(n);

        Assert.Equal(n, w.Length);
        for (int k = 0; k < n; k++)
        {
            double expected = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * k / (n - 1)));
            Assert.Equal(expected, w[k], 12);
        }
    }

    [Fact]
    public void Hann_endpoints_are_zero()
    {
        double[] w = HannWindow.Coefficients(8);
        Assert.Equal(0.0, w[0], 12);
        Assert.Equal(0.0, w[^1], 12);
    }

    [Fact]
    public void Hann_is_symmetric()
    {
        double[] w = HannWindow.Coefficients(9);
        for (int i = 0; i < w.Length; i++)
            Assert.Equal(w[i], w[w.Length - 1 - i], 12);
    }

    [Fact]
    public void Hann_rejects_non_positive_size()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HannWindow.Coefficients(0));
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~HannWindowTests"
```

Expected FAILURE: compile error `CS0103`/`CS0246` — `HannWindow` does not exist yet. Red because the production type is missing.

**Step 3 — Minimal implementation:**

```csharp
using System;

namespace AudioClaudio.Domain.Spectral;

/// <summary>
/// The Hann window. This is the single place a window is defined for the pipeline (R3.1);
/// only <see cref="SpectralFrontEnd"/> applies it. Pure, BCL-only.
/// </summary>
public static class HannWindow
{
    /// <summary>
    /// Symmetric Hann coefficients <c>w[n] = 0.5·(1 − cos(2π·n/(N−1)))</c> for <c>n = 0..N−1</c>.
    /// Zero at both endpoints; unit-length special case returns <c>{1}</c>.
    /// </summary>
    /// <param name="size">Window length N in samples; must be positive.</param>
    public static double[] Coefficients(int size)
    {
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), size, "Window size must be positive.");

        var w = new double[size];
        if (size == 1)
        {
            w[0] = 1.0;
            return w;
        }

        double denom = size - 1;
        for (int n = 0; n < size; n++)
            w[n] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / denom));
        return w;
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~HannWindowTests"
```

Expected PASS: all four tests green.

**Step 5 — Commit** (use the @gitbutler skill — never raw git):

```bash
but branch new step-03-spectral-front-end
but mark step-03-spectral-front-end
but status -fv    # read the file/hunk IDs for the two changed files
but commit step-03-spectral-front-end -m "feat(domain): Hann window function" --changes <ids> --status-after
```

---

## Task 2: `IFourierTransform` and the FFT implementation — ⚠ DECISION-DEPENDENT

This is the only task the DECISION GATE touches. The interface, the `TestFft` seam, and the Parseval test are identical either way; only the concrete implementation (and where it lives) differs.

**Files:**
- Create: `src/AudioClaudio.Domain/Spectral/IFourierTransform.cs` (always)
- Create — **Option A:** `src/AudioClaudio.Domain/Spectral/Radix2Fft.cs`  **·  Option B:** `src/AudioClaudio.Infrastructure/Spectral/NWavesFourierTransform.cs`
- Modify — **Option B only:** `src/AudioClaudio.Infrastructure/AudioClaudio.Infrastructure.csproj` (add the NWaves `<PackageReference>`)
- Modify: `DECISIONS.md` (record the resolved decision; under Option B also record the NWaves version + MIT license — Section 1 rule 7)
- Create: `tests/AudioClaudio.Tests/Spectral/TestFft.cs`
- Test: `tests/AudioClaudio.Tests/Spectral/FourierTransformTests.cs`

**Step 1 — Write the failing test** (the Parseval property; identical for both options):

```csharp
using System;
using AudioClaudio.Domain.Spectral;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.Spectral;

[Trait("Category", "Fast")]
public class FourierTransformTests
{
    // Parseval's theorem — the FFT's trial balance: Σ x[n]² == (1/N)·Σ |X[k]|².
    // Relative tolerance is loose enough for the single-precision NWaves path and
    // trivially satisfied by the double-precision hand-rolled path.
    [Fact]
    public void Forward_satisfies_Parsevals_theorem()
    {
        IFourierTransform fft = TestFft.Create();

        Gen.Double[-1.0, 1.0].Array[1024].Sample(samples =>
        {
            System.Numerics.Complex[] spectrum = fft.Forward(samples);

            double timeEnergy = 0.0;
            foreach (double s in samples) timeEnergy += s * s;

            double freqEnergy = 0.0;
            foreach (System.Numerics.Complex c in spectrum) freqEnergy += c.Magnitude * c.Magnitude;
            freqEnergy /= samples.Length;

            double denom = Math.Max(timeEnergy, 1e-9);
            return Math.Abs(timeEnergy - freqEnergy) / denom < 1e-4;
        }, iter: 200);
        // Determinism (non-negotiable #3): CsCheck's default run is reproducible.
        // On a failure it prints a Seed = "..."; paste it back as `seed: "..."` to replay.
    }

    [Fact]
    public void Forward_rejects_non_power_of_two_lengths()
    {
        IFourierTransform fft = TestFft.Create();
        Assert.Throws<ArgumentException>(() => fft.Forward(new double[1000]));
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~FourierTransformTests"
```

Expected FAILURE: compile error — `IFourierTransform`, `TestFft`, and the concrete FFT type do not exist yet. Red because the production code and the seam are missing.

**Step 3 — Minimal implementation.** First the interface and the test seam (both options):

```csharp
// src/AudioClaudio.Domain/Spectral/IFourierTransform.cs
using System.Numerics;

namespace AudioClaudio.Domain.Spectral;

/// <summary>
/// A forward Discrete Fourier Transform of real-valued samples. This is the Step 3
/// design-decision seam: the hand-rolled <c>Radix2Fft</c> and any library-backed
/// implementation both satisfy it, so the pipeline depends only on this BCL-only
/// abstraction (keeps <c>AudioClaudio.Domain</c> dependency-free per R0.2).
/// </summary>
public interface IFourierTransform
{
    /// <summary>
    /// Forward DFT. <paramref name="samples"/> length MUST be a power of two, else
    /// <see cref="System.ArgumentException"/>. Returns the full complex spectrum of the
    /// same length (bins 0..N−1); callers keep the 0..N/2 half for real signals.
    /// </summary>
    Complex[] Forward(double[] samples);
}
```

```csharp
// tests/AudioClaudio.Tests/Spectral/TestFft.cs
using AudioClaudio.Domain.Spectral;
// Option B also needs: using AudioClaudio.Infrastructure.Spectral;

namespace AudioClaudio.Tests.Spectral;

/// <summary>
/// The single seam for the Step 3 FFT design decision. The whole spectral suite
/// runs through this factory, so resolving the DECISION GATE is a one-line change
/// here (plus which project the implementation lives in).
/// </summary>
internal static class TestFft
{
    public static IFourierTransform Create() => new Radix2Fft();          // ← Option A
    // public static IFourierTransform Create() => new NWavesFourierTransform(); // ← Option B
}
```

Then implement **exactly one** of the following, per the resolved DECISION GATE.

**— Option A** (hand-rolled radix-2, in `AudioClaudio.Domain`; no csproj change):

```csharp
// src/AudioClaudio.Domain/Spectral/Radix2Fft.cs
using System;
using System.Numerics;

namespace AudioClaudio.Domain.Spectral;

/// <summary>
/// Hand-rolled iterative radix-2 Cooley–Tukey FFT over <see cref="Complex"/> (BCL only).
/// Forward transform, <c>O(N·log N)</c>; input length must be a power of two.
/// </summary>
public sealed class Radix2Fft : IFourierTransform
{
    public Complex[] Forward(double[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        int n = samples.Length;
        if (n == 0 || (n & (n - 1)) != 0)
            throw new ArgumentException($"FFT length must be a positive power of two, got {n}.", nameof(samples));

        var a = new Complex[n];
        for (int i = 0; i < n; i++) a[i] = new Complex(samples[i], 0.0);

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }

        // Butterflies. wlen = exp(-2πi/len) is the forward-transform twiddle.
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2.0 * Math.PI / len;
            var wlen = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                Complex w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    Complex u = a[i + k];
                    Complex v = a[i + k + len / 2] * w;
                    a[i + k] = u + v;
                    a[i + k + len / 2] = u - v;
                    w *= wlen;
                }
            }
        }

        return a;
    }
}
```

**— Option B** (NWaves adapter, in `AudioClaudio.Infrastructure`; add the package):

```xml
<!-- src/AudioClaudio.Infrastructure/AudioClaudio.Infrastructure.csproj -->
<ItemGroup>
  <!-- NWaves is MIT; pin the exact version resolved and log it in DECISIONS.md. -->
  <PackageReference Include="NWaves" Version="0.9.6" />
</ItemGroup>
```

```csharp
// src/AudioClaudio.Infrastructure/Spectral/NWavesFourierTransform.cs
using System;
using System.Numerics;
using AudioClaudio.Domain.Spectral;
using NWaves.Transforms;

namespace AudioClaudio.Infrastructure.Spectral;

/// <summary>
/// NWaves-backed forward FFT. Lives in Infrastructure (not Domain) so the third-party
/// dependency never crosses into the BCL-only Domain layer (R0.2). NWaves works in
/// single precision; the Parseval tolerance in the tests accounts for it. Verify the
/// exact Direct(...) signature against the installed NWaves version.
/// </summary>
public sealed class NWavesFourierTransform : IFourierTransform
{
    public Complex[] Forward(double[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        int n = samples.Length;
        if (n == 0 || (n & (n - 1)) != 0)
            throw new ArgumentException($"FFT length must be a positive power of two, got {n}.", nameof(samples));

        var re = new float[n];
        var im = new float[n];
        for (int i = 0; i < n; i++) re[i] = (float)samples[i];

        var fft = new Fft(n);
        fft.Direct(re, im); // unnormalized forward DFT, in place

        var result = new Complex[n];
        for (int i = 0; i < n; i++) result[i] = new Complex(re[i], im[i]);
        return result;
    }
}
```

Record the decision in `DECISIONS.md`, e.g. (Option A):

```markdown
## Step 3 — FFT implementation
**Decision:** Hand-rolled iterative radix-2 Cooley–Tukey FFT (`Radix2Fft`), in `AudioClaudio.Domain`.
**Why:** Dependency-free, keeps Domain BCL-only per R0.2; full double precision; Parseval property guards correctness.
**Alternative rejected:** NWaves (MIT) — deferred; if adopted for Phase 2 resampling, its adapter goes in Infrastructure, not Domain.
```

or (Option B):

```markdown
## Step 3 — FFT implementation
**Decision:** NWaves 0.9.6 (MIT), wrapped by `NWavesFourierTransform` in `AudioClaudio.Infrastructure`.
**Why:** Battle-tested; brings resampling for Phase 2's 22.05 kHz model input.
**R0.2:** Adapter placed in Infrastructure so Domain still references only the BCL and the `IFourierTransform` abstraction.
**License:** NWaves — MIT (permissive, no copyleft) — OK.
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~FourierTransformTests"
```

Expected PASS: Parseval holds across 200 generated signals; the non-power-of-two guard throws. If Parseval fails, reach for @superpowers:systematic-debugging — check the twiddle sign, the bit-reversal, and that the `(1/N)` normalization is on the frequency side only.

**Step 5 — Commit** (message per the resolved option):

```bash
but status -fv
# Option A:
but commit step-03-spectral-front-end -m "feat(domain): radix-2 FFT behind IFourierTransform" --changes <ids> --status-after
# Option B:
but commit step-03-spectral-front-end -m "feat(infra): NWaves FFT behind IFourierTransform" --changes <ids> --status-after
```

---

## Task 3: `MagnitudeSpectrum`

The per-frame result type: the half-spectrum magnitudes plus the metadata (`SampleRate`, frame size) needed to map bins to frequencies. Immutable, so a spectrum can never be mutated out from under a downstream consumer.

**Files:**
- Create: `src/AudioClaudio.Domain/Spectral/MagnitudeSpectrum.cs`
- Test: `tests/AudioClaudio.Tests/Spectral/MagnitudeSpectrumTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;
using Xunit;

namespace AudioClaudio.Tests.Spectral;

[Trait("Category", "Fast")]
public class MagnitudeSpectrumTests
{
    [Fact]
    public void BinCount_is_half_frame_plus_one()
    {
        var s = new MagnitudeSpectrum(new double[9], frameSize: 16, rate: new SampleRate(8000));
        Assert.Equal(9, s.BinCount);
    }

    [Fact]
    public void FrequencyOf_maps_bins_to_hertz()
    {
        var s = new MagnitudeSpectrum(new double[9], frameSize: 16, rate: new SampleRate(8000));
        Assert.Equal(0.0, s.FrequencyOf(0), 9);      // DC
        Assert.Equal(500.0, s.FrequencyOf(1), 9);    // 1·8000/16
        Assert.Equal(4000.0, s.FrequencyOf(8), 9);   // Nyquist = 8000/2
    }

    [Fact]
    public void PeakBin_returns_the_largest_bin_and_breaks_ties_to_the_lowest()
    {
        var s = new MagnitudeSpectrum(new double[] { 1.0, 3.0, 3.0, 2.0 }, frameSize: 6, rate: new SampleRate(8000));
        Assert.Equal(1, s.PeakBin()); // defined tie-break: lowest bin wins (non-negotiable #3)
    }

    [Fact]
    public void Magnitudes_are_copied_defensively()
    {
        var raw = new double[] { 1.0, 2.0 };
        var s = new MagnitudeSpectrum(raw, frameSize: 2, rate: new SampleRate(8000));
        raw[0] = 99.0;
        Assert.Equal(1.0, s[0]); // external mutation must not leak in
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~MagnitudeSpectrumTests"
```

Expected FAILURE: compile error — `MagnitudeSpectrum` does not exist yet.

**Step 3 — Minimal implementation:**

```csharp
using System;
using System.Collections.Generic;
using AudioClaudio.Domain;

namespace AudioClaudio.Domain.Spectral;

/// <summary>
/// A per-frame magnitude spectrum: bins <c>0..N/2</c> inclusive (the real-signal
/// half-spectrum, DC through Nyquist), carrying the <see cref="SampleRate"/> and
/// frame size N so bins map to Hz. Immutable; equal inputs produce equal spectra
/// (determinism, non-negotiable #3).
/// </summary>
public sealed class MagnitudeSpectrum
{
    private readonly double[] _magnitudes;

    /// <param name="magnitudes">Bin magnitudes; length MUST equal <c>frameSize/2 + 1</c>.</param>
    /// <param name="frameSize">Analysis window length N (samples); a power of two.</param>
    /// <param name="rate">The sample rate the frame was captured at.</param>
    public MagnitudeSpectrum(double[] magnitudes, int frameSize, SampleRate rate)
    {
        ArgumentNullException.ThrowIfNull(magnitudes);
        int expected = frameSize / 2 + 1;
        if (magnitudes.Length != expected)
            throw new ArgumentException(
                $"Expected {expected} bins for frame size {frameSize}, got {magnitudes.Length}.", nameof(magnitudes));

        FrameSize = frameSize;
        Rate = rate;
        _magnitudes = (double[])magnitudes.Clone(); // defensive copy — immutability
    }

    /// <summary>Analysis window length N in samples.</summary>
    public int FrameSize { get; }

    /// <summary>The sample rate the source frame was captured at.</summary>
    public SampleRate Rate { get; }

    /// <summary>Number of bins, <c>N/2 + 1</c> (DC through Nyquist).</summary>
    public int BinCount => _magnitudes.Length;

    /// <summary>Magnitude |X[k]| of bin k.</summary>
    public double this[int bin] => _magnitudes[bin];

    /// <summary>Read-only view of all bin magnitudes.</summary>
    public IReadOnlyList<double> Magnitudes => _magnitudes;

    /// <summary>Centre frequency of bin k in Hz: <c>k · sampleRate / N</c> (the only Hz conversion — a display-edge concern).</summary>
    public double FrequencyOf(int bin) => (double)bin * Rate.Hz / FrameSize;

    /// <summary>Index of the largest-magnitude bin. Ties resolve to the lowest bin (defined tie-break, non-negotiable #3).</summary>
    public int PeakBin()
    {
        int peak = 0;
        for (int k = 1; k < _magnitudes.Length; k++)
            if (_magnitudes[k] > _magnitudes[peak]) peak = k;
        return peak;
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~MagnitudeSpectrumTests"
```

Expected PASS: all four tests green.

**Step 5 — Commit:**

```bash
but status -fv
but commit step-03-spectral-front-end -m "feat(domain): magnitude spectrum with bin-to-frequency mapping" --changes <ids> --status-after
```

---

## Task 4: `SpectralFrontEnd`

The one place the window is applied (R3.1) and the one place a frame becomes a spectrum (R3.2). Pure and stateless per frame (R3.3): it holds only immutable configuration (the precomputed Hann coefficients and the injected FFT), and `Analyze` is a pure function of its `Frame` argument.

**Files:**
- Create: `src/AudioClaudio.Domain/Spectral/SpectralFrontEnd.cs`
- Test: `tests/AudioClaudio.Tests/Spectral/SpectralFrontEndTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;
using Xunit;

namespace AudioClaudio.Tests.Spectral;

[Trait("Category", "Fast")]
public class SpectralFrontEndTests
{
    // Canonical Step 2 Frame contract: Frame(float[] samples, SamplePosition start) — a 2-arg
    // sealed class. Rate is derived (Frame.Rate => Start.Rate), never a ctor parameter. Members: Samples, Start, Rate.
    private static Frame SineFrame(double frequencyHz, int sampleRateHz, int frameSize)
    {
        var rate = new SampleRate(sampleRateHz);
        var samples = new float[frameSize];
        for (int n = 0; n < frameSize; n++)
            samples[n] = (float)Math.Sin(2.0 * Math.PI * frequencyHz * n / sampleRateHz);
        return new Frame(samples, new SamplePosition(0, rate));
    }

    [Fact]
    public void Analyze_applies_the_hann_window_to_the_frame()
    {
        const int n = 64;
        var rate = new SampleRate(8000);
        var ones = new float[n];
        for (int i = 0; i < n; i++) ones[i] = 1.0f;
        var frame = new Frame(ones, new SamplePosition(0, rate));

        var frontEnd = new SpectralFrontEnd(n, TestFft.Create());
        MagnitudeSpectrum spectrum = frontEnd.Analyze(frame);

        // A windowed all-ones (DC) frame equals the Hann coefficients, so its DC bin
        // magnitude equals their sum (~31.5). Without windowing it would be n (=64).
        double expectedDc = HannWindow.Coefficients(n).Sum();
        Assert.True(Math.Abs(spectrum[0] - expectedDc) / expectedDc < 1e-4,
            $"DC bin {spectrum[0]} should equal the summed Hann coefficients {expectedDc}; the window was not applied.");
    }

    [Theory]
    [InlineData(500.0, 64)]    // 500 Hz sits exactly on bin 64: 64·8000/1024 = 500
    [InlineData(1000.0, 128)]  // 1000 Hz on bin 128: 128·8000/1024 = 1000
    [InlineData(517.0, 66)]    // off-bin: nearest bin to 517 Hz is 66 (515.6 Hz)
    public void Peak_bin_of_windowed_sine_is_the_bin_nearest_the_frequency(double freq, int expectedBin)
    {
        const int n = 1024, sr = 8000;
        var frontEnd = new SpectralFrontEnd(n, TestFft.Create());
        MagnitudeSpectrum spectrum = frontEnd.Analyze(SineFrame(freq, sr, n));
        Assert.Equal(expectedBin, spectrum.PeakBin());
    }

    [Fact]
    public void Analyze_is_deterministic_bit_for_bit()
    {
        const int n = 1024;
        var frontEnd = new SpectralFrontEnd(n, TestFft.Create());
        Frame frame = SineFrame(440.0, 44100, n);

        MagnitudeSpectrum a = frontEnd.Analyze(frame);
        MagnitudeSpectrum b = frontEnd.Analyze(frame);
        Assert.Equal(a.Magnitudes, b.Magnitudes); // exact, element-wise
    }

    [Fact]
    public void Analyze_maps_a_stream_of_frames_to_a_stream_of_spectra()
    {
        const int n = 256, sr = 8000;
        var frontEnd = new SpectralFrontEnd(n, TestFft.Create());
        var frames = new List<Frame> { SineFrame(500.0, sr, n), SineFrame(1000.0, sr, n) };

        List<MagnitudeSpectrum> spectra = frontEnd.Analyze(frames).ToList();

        Assert.Equal(2, spectra.Count);
        Assert.Equal(16, spectra[0].PeakBin());  // 500 Hz → bin 16 (16·8000/256)
        Assert.Equal(32, spectra[1].PeakBin());  // 1000 Hz → bin 32
    }

    [Fact]
    public void Constructor_rejects_non_power_of_two_frame_size()
    {
        Assert.Throws<ArgumentException>(() => new SpectralFrontEnd(1000, TestFft.Create()));
    }

    [Fact]
    public void Analyze_rejects_a_frame_whose_length_differs_from_the_configured_size()
    {
        var frontEnd = new SpectralFrontEnd(1024, TestFft.Create());
        var rate = new SampleRate(8000);
        var shortFrame = new Frame(new float[512], new SamplePosition(0, rate));
        Assert.Throws<ArgumentException>(() => frontEnd.Analyze(shortFrame));
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~SpectralFrontEndTests"
```

Expected FAILURE: compile error — `SpectralFrontEnd` does not exist yet. Red because the production type is missing.

**Step 3 — Minimal implementation:**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AudioClaudio.Domain;

namespace AudioClaudio.Domain.Spectral;

/// <summary>
/// The spectral front end: window a <see cref="Frame"/> with Hann (the single window
/// site — R3.1), forward-FFT it, and return the half-spectrum magnitudes (R3.2). Pure —
/// same frame in, same spectrum out; no I/O, no clock; the only state is immutable
/// configuration (the precomputed window and the injected transform) (R3.3).
/// </summary>
public sealed class SpectralFrontEnd
{
    private readonly IFourierTransform _fft;
    private readonly double[] _window;
    private readonly int _frameSize;

    /// <param name="frameSize">Analysis window length N; MUST be a power of two.</param>
    /// <param name="fft">The forward transform (the Step 3 design-decision seam; explicit dependency).</param>
    public SpectralFrontEnd(int frameSize, IFourierTransform fft)
    {
        ArgumentNullException.ThrowIfNull(fft);
        if (frameSize <= 0 || (frameSize & (frameSize - 1)) != 0)
            throw new ArgumentException($"Frame size must be a positive power of two, got {frameSize}.", nameof(frameSize));

        _frameSize = frameSize;
        _fft = fft;
        _window = HannWindow.Coefficients(frameSize); // computed once — the single window site
    }

    /// <summary>Window, transform, and take the magnitude half-spectrum of one frame.</summary>
    public MagnitudeSpectrum Analyze(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        IReadOnlyList<float> samples = frame.Samples;
        if (samples.Count != _frameSize)
            throw new ArgumentException(
                $"Frame length {samples.Count} does not match configured frame size {_frameSize}.", nameof(frame));

        var windowed = new double[_frameSize];
        for (int n = 0; n < _frameSize; n++)
            windowed[n] = samples[n] * _window[n];

        Complex[] spectrum = _fft.Forward(windowed);

        int bins = _frameSize / 2 + 1; // real-signal half-spectrum: DC through Nyquist
        var magnitudes = new double[bins];
        for (int k = 0; k < bins; k++)
            magnitudes[k] = spectrum[k].Magnitude;

        return new MagnitudeSpectrum(magnitudes, _frameSize, frame.Rate);
    }

    /// <summary>Analyze an ordered stream of frames, one spectrum each — frames in, spectra out (R3.3).</summary>
    public IEnumerable<MagnitudeSpectrum> Analyze(IEnumerable<Frame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        return frames.Select(Analyze);
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~SpectralFrontEndTests"
```

Expected PASS: window-applied, peak-bin (all three cases), determinism, stream mapping, and both validation guards green. Then run the fast suite and the formatter to confirm nothing else regressed:

```bash
dotnet format
dotnet test --filter Category=Fast
```

Expected PASS: whole fast suite green; `dotnet format` reports no changes.

**Step 5 — Commit** (the step's headline message per the spec):

```bash
but status -fv
but commit step-03-spectral-front-end -m "feat(domain): windowed spectral front end" --changes <ids> --status-after
```

---

## Verify (step exit criteria)

From Section 6 Step 3's *Verify* section:

- [ ] **Property — Parseval's theorem** holds within numerical tolerance for generated signals (energy in time equals energy in frequency; the FFT's trial balance) — `Forward_satisfies_Parsevals_theorem` over 200 generated signals.
- [ ] **Example — peak bin of a windowed sine at f** lies at the bin nearest f — `Peak_bin_of_windowed_sine_is_the_bin_nearest_the_frequency` (on-bin 500/1000 Hz and off-bin 517 Hz).
- [ ] **R3.1** — the Hann window is applied at exactly one place (`SpectralFrontEnd`, proven by the DC-frame test; `HannWindow` is the sole definition).
- [ ] **R3.2** — an FFT is available to the domain via `IFourierTransform`, and `Analyze` produces per-frame `MagnitudeSpectrum` results.
- [ ] **R3.3** — the front end is pure: deterministic (`Analyze_is_deterministic_bit_for_bit`), no I/O, no clock, no per-frame mutable state; frames in, spectra out.

## Definition of Done

- [ ] `dotnet build` succeeds; `dotnet format` reports no changes.
- [ ] All new tests pass, and `dotnet test --filter Category=Fast` is green.
- [ ] Dependency rule intact: all new Domain code (`HannWindow`, `IFourierTransform`, `MagnitudeSpectrum`, `SpectralFrontEnd`, and — under Option A — `Radix2Fft`) references only the BCL; under Option B the NWaves adapter is in Infrastructure, never Domain. Verified by project references, not aspiration.
- [ ] The requirement-coverage table is fully satisfied (every R3.x proven by a named test).
- [ ] `DECISIONS.md` records the resolved FFT decision (and, under Option B, the NWaves version + MIT license — Section 1 rule 7).
- [ ] Work committed on branch `step-03-spectral-front-end` via the @gitbutler skill, culminating in `feat(domain): windowed spectral front end`.
