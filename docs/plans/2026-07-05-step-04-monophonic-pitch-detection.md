# Step 4 — Monophonic Pitch Detection (YIN) — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Build-spec step:** Section 6 Step 4 (R4.1, R4.2, R4.3, R4.4)
**Goal:** A pure, per-frame YIN detector that turns one analysis `Frame` into either a fundamental-frequency estimate with a confidence, or *unvoiced*, accurate to ±10 cents across MIDI 33–96 and un-fooled by piano-like overtones.
**Architecture:** Lives entirely in `AudioClaudio.Domain` (the innermost layer) as a static algorithm alongside `PitchMath` — no port, no interface, no adapter. It consumes the Step 2 `Frame` (raw mono PCM) and returns a new domain value type `PitchEstimate`. Downstream (Step 5 segmentation) will call it frame-by-frame; the closed loop (Step 9) will prove it end-to-end. Because it is Domain code the dependency rule *physically* forbids it from touching an audio device, a MIDI library, or `DateTime` — purity is enforced by project references, not discipline.
**Tech Stack:** C# / .NET 10 BCL only (no NuGet in Domain); xUnit + CsCheck in the test project; the Step 2 `SignalGenerator` test utility for deterministic sine and harmonic-stack fixtures.
**Prerequisites:** Steps 0–3 green and committed (§1 rule 3). Note: YIN is a **time-domain** autocorrelation-family method — it reads the frame's raw samples directly and does **not** consume the Step 3 FFT/magnitude spectrum. Step 3 must still be complete before this step is started, but this step adds no dependency on it.
**Commit (spec):** `feat(domain): YIN pitch detection with cents-accuracy properties`

---

## Approach (read before writing code)

YIN (de Cheveigné & Kawahara, 2002) estimates the fundamental period of a signal in four movements, then converts period to frequency. Teach yourself the shape before touching the tests:

1. **Difference function `d(τ)`.** For each candidate lag `τ`, sum the squared difference between the frame and a copy of itself shifted by `τ`, over an integration window of `W` samples: `d(τ) = Σ_{j=0..W-1} (x[j] − x[j+τ])²`. When `τ` equals the signal's period the shifted copy lines up with the original and `d(τ)` collapses toward zero. Computed directly in `O(W·τ_max)` — a couple of million multiply-adds per frame, trivial at MVP rates.

2. **Cumulative-mean normalization `d'(τ)`.** Divide `d(τ)` by the running mean of `d` over `[1..τ]`: `d'(τ) = d(τ) · τ / Σ_{j=1..τ} d(j)`, with `d'(0) = 1`. This turns the raw difference into a scale-free *aperiodicity* that starts near 1 and dips toward 0 only at genuine periods. This is YIN's key trick: it removes the "`τ=0` is trivially best" bias of raw autocorrelation and flattens the shallow dips that overtones create — exactly what keeps the estimator off a partial (R4.3).

3. **Absolute threshold + smallest-lag rule.** Scan the musically-plausible lag window and take the **first** `τ` whose `d'` falls below a named threshold, then descend to the bottom of that dip (its local minimum). Taking the *smallest* qualifying lag is what makes YIN pick the fundamental period rather than a multiple of it: a harmonic signal dips at `T, 2T, 3T…`; the smallest is the true period. If no lag dips below the threshold, the frame is aperiodic → **unvoiced** (silence and white noise both land here, R4.1).

4. **Parabolic interpolation.** The true period is rarely a whole number of samples. Fitting a parabola through `d'` at `(τ−1, τ, τ+1)` and taking its vertex recovers sub-sample precision. At the top of the range one whole sample of lag is several cents, so this refinement is what lets a ~21-sample period at C7 land inside ±10 cents (R4.2).

Then `f0 = sampleRate / τ*` and **confidence** `= 1 − d'(τ*)` (a deep dip is high confidence; a shallow one is low).

**Why not FFT-peak:** piano partials routinely exceed the fundamental in magnitude, so the tallest spectral bin is often an overtone — a built-in octave error. YIN works in the period domain where the fundamental is the unambiguous *smallest* period. **Why not pYIN:** it layers a probabilistic HMM the MVP deliberately forgoes to stay deterministic (non-negotiable 3); pYIN is a recorded Phase-2 upgrade.

**Sizing.** All tests run at `44100 Hz` with a frame of `N = 4096` samples, split as integration window `W = N/2 = 2048` and lag search up to `N/2`. The plausible-frequency window (default `[45 Hz, 2500 Hz]`) clamps the lag search to roughly `[17, 980]` samples: `τ_max ≈ 980` comfortably exceeds MIDI 33's period of ~802 samples (≈2.5 window-periods of support), and `τ_min ≈ 17` leaves room *below* MIDI 96's ~21-sample period so parabolic interpolation has both neighbours at the very top. This is why `N = 4096` (not 2048) is the fixture size: the low end needs the window.

---

## Requirements coverage

| Requirement | Task(s) | Proven by (test) |
|---|---|---|
| **R4.1** — voiced `f0`+confidence *or* unvoiced; threshold is a **named parameter** | 1, 2, 3, 4 | `PitchEstimate_VoicedAndUnvoiced_*`; `YinOptions_Defaults_And_Validation`; `Detect_PureSineA4_IsVoicedWithin10Cents`; `Detect_SilenceAndWhiteNoise_AreUnvoiced` |
| **R4.2** — accurate within **±10 cents** across MIDI 33–96 | 3, 5 | `Detect_LandmarkPitches_Within10Cents` (Theory); `Property_PureSine_Within10CentsAcrossRange` (CsCheck) |
| **R4.3** — robust to the harmonic stack; **no octave errors** | 6 | `Property_HarmonicStack_Within10CentsAndNoOctaveError` (CsCheck) |
| **R4.4** — pure function; per-frame; no I/O; deterministic | 7 (+ structural) | `Detect_IsDeterministic_SameFrameSameEstimate`; enforced structurally by the Domain dependency rule (no `IClock`/`DateTime`/device types reachable) |

---

## Branch setup (once, before Task 1)

Use the @gitbutler skill. Create and mark a branch so edits auto-stage to it:

```bash
but status -fv                       # inspect workspace, learn fresh CLI IDs
but branch new step-04-yin
but mark step-04-yin
```

Each task below commits incrementally with a finer conventional message; they roll up to the spec message `feat(domain): YIN pitch detection with cents-accuracy properties` (§1 rule 5 allows finer-grained commits). Run `dotnet format` before every commit.

---

## Assumptions about upstream steps (single points of change)

These types are delivered by Steps 1–2. If a name differs when you reach this step, adjust the *one* place noted and nothing else:

- **Step 1 (Domain, root namespace `AudioClaudio.Domain`):** `Pitch { int MidiNumber; double Frequency(); static Pitch FromFrequency(double hz); }`, `PitchMath.CentsBetween(double f1, double f2)`, `SampleRate` with an `int Hz` accessor and `new SampleRate(44100)`, `SamplePosition(long samples, SampleRate rate)`.
- **Step 2 (Domain):** `Frame` exposing `float[] Samples`, `SamplePosition Start`, and `SampleRate Rate` (derived: `Rate => Start.Rate`), constructed as `new Frame(float[] samples, SamplePosition start)`. Frame length comes from `Samples.Length` — there is no `Length` member. *(If `Frame` exposes `ReadOnlySpan<float>` instead of `float[]`, change the one line `float[] x = frame.Samples;` in `YinPitchDetector` to copy the span.)*
- **Step 2 test utility (`AudioClaudio.Tests.Signals`, R2.3), canonical API** — the mandated deterministic fixture source (§5); the calls appear only inside `MakeFrame`-adjacent helpers in the test files:

```csharp
public static class SignalGenerator
{
    // Pure sine, `sampleCount` samples at `rate`, amplitude in [-1,1].
    public static float[] Sine(double frequencyHz, int sampleCount, SampleRate rate, double amplitude = 0.8);
    // Fundamental + (partials-1) overtones; amplitude of partial k is 1/k^decay, normalized to `amplitude`.
    public static float[] HarmonicStack(double fundamentalHz, int sampleCount, SampleRate rate,
                                        int partials = 6, double decay = 1.0, double amplitude = 0.8);
}
```

All new files in this step use the root namespace `AudioClaudio.Domain` (for domain types) and `AudioClaudio.Tests` (for tests), matching "namespaces mirror project names."

---

## Task 1: `PitchEstimate` — the detector's return type

The value type a frame maps to: voiced (frequency + confidence) or unvoiced. Immutable, value-equatable (the determinism test in Task 7 asserts equality), no I/O.

**Files:**
- Create: `src/AudioClaudio.Domain/Detection/PitchEstimate.cs`
- Test: `tests/AudioClaudio.Tests/Detection/PitchEstimateTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests;

public class PitchEstimateTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Voiced_CarriesFrequencyConfidenceAndIsVoiced()
    {
        var e = PitchEstimate.Voiced(frequencyHz: 440.0, confidence: 0.97);

        Assert.True(e.IsVoiced);
        Assert.Equal(440.0, e.FrequencyHz, precision: 9);
        Assert.Equal(0.97, e.Confidence, precision: 9);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Unvoiced_IsNotVoiced()
    {
        Assert.False(PitchEstimate.Unvoiced.IsVoiced);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Voiced_RejectsNonPositiveFrequency(double badHz)
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => PitchEstimate.Voiced(badHz, confidence: 0.5));
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Voiced_RejectsConfidenceOutsideUnitInterval(double badConf)
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => PitchEstimate.Voiced(440.0, badConf));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Estimates_HaveValueEquality()
    {
        Assert.Equal(PitchEstimate.Voiced(440.0, 0.9), PitchEstimate.Voiced(440.0, 0.9));
        Assert.NotEqual(PitchEstimate.Voiced(440.0, 0.9), PitchEstimate.Unvoiced);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~PitchEstimateTests"
```

Expected FAILURE: compile error — `PitchEstimate` does not exist yet.

**Step 3 — Minimal implementation:**

```csharp
using System;

namespace AudioClaudio.Domain;

/// <summary>
/// The result of running the pitch detector on a single analysis frame:
/// either a voiced fundamental-frequency estimate with a confidence in [0, 1],
/// or <see cref="Unvoiced"/> (silence or noise). A pure domain value; carries
/// no clock, no I/O. (R4.1)
/// </summary>
public readonly record struct PitchEstimate
{
    /// <summary>True when a fundamental was found; false for silence/noise.</summary>
    public bool IsVoiced { get; }

    /// <summary>Estimated fundamental in Hz. Meaningful only when <see cref="IsVoiced"/>.</summary>
    public double FrequencyHz { get; }

    /// <summary>Detection confidence in [0, 1]; 1 − aperiodicity at the chosen lag. 0 when unvoiced.</summary>
    public double Confidence { get; }

    private PitchEstimate(bool isVoiced, double frequencyHz, double confidence)
    {
        IsVoiced = isVoiced;
        FrequencyHz = frequencyHz;
        Confidence = confidence;
    }

    /// <summary>A voiced estimate. Frequency must be positive; confidence must lie in [0, 1].</summary>
    public static PitchEstimate Voiced(double frequencyHz, double confidence)
    {
        if (frequencyHz <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(frequencyHz), frequencyHz, "Voiced frequency must be positive.");
        if (confidence is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "Confidence must lie in [0, 1].");
        return new PitchEstimate(true, frequencyHz, confidence);
    }

    /// <summary>The canonical "no pitch here" result (silence or noise).</summary>
    public static readonly PitchEstimate Unvoiced = new(false, 0.0, 0.0);
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~PitchEstimateTests"
```

Expected PASS: all six cases green.

**Step 5 — Commit** (via the @gitbutler skill; get `<ids>` from `but status -fv`):

```bash
dotnet format
but status -fv
but commit step-04-yin -m "feat(domain): PitchEstimate value type" --changes <ids from but status -fv> --status-after
```

---

## Task 2: `YinOptions` — the named threshold and search range

R4.1 requires the voiced/unvoiced threshold to be a **named parameter**, not a magic number. Bundle it with the frequency search window (also parameters, per the R2.4 "no scattered constants" spirit). Fail fast on nonsense.

**Files:**
- Create: `src/AudioClaudio.Domain/Detection/YinOptions.cs`
- Test: `tests/AudioClaudio.Tests/Detection/YinOptionsTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests;

public class YinOptionsTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Default_HasNamedThresholdAndPianoSearchRange()
    {
        var o = YinOptions.Default;

        Assert.Equal(0.15, o.Threshold, precision: 9);
        Assert.Equal(45.0, o.MinFrequencyHz, precision: 9);
        Assert.Equal(2500.0, o.MaxFrequencyHz, precision: 9);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void Constructor_RejectsThresholdOutsideOpenUnitInterval(double badThreshold)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new YinOptions(threshold: badThreshold));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Constructor_RejectsNonPositiveMinFrequency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new YinOptions(minFrequencyHz: 0.0));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Constructor_RejectsMaxNotAboveMin()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new YinOptions(minFrequencyHz: 400.0, maxFrequencyHz: 400.0));
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~YinOptionsTests"
```

Expected FAILURE: compile error — `YinOptions` does not exist yet.

**Step 3 — Minimal implementation:**

```csharp
using System;

namespace AudioClaudio.Domain;

/// <summary>
/// Named tuning parameters for <see cref="YinPitchDetector"/>. The
/// <see cref="Threshold"/> is the R4.1 voiced/unvoiced cutoff on the
/// cumulative-mean-normalized difference; the frequency window bounds the lag
/// search. All values are validated on construction (fail fast).
/// </summary>
public sealed class YinOptions
{
    /// <summary>Aperiodicity cutoff in (0, 1): the first lag whose d' dips below this is a voiced candidate.</summary>
    public double Threshold { get; }

    /// <summary>Lowest fundamental to search for, in Hz. Sets the maximum lag.</summary>
    public double MinFrequencyHz { get; }

    /// <summary>Highest fundamental to search for, in Hz. Sets the minimum lag.</summary>
    public double MaxFrequencyHz { get; }

    public YinOptions(double threshold = 0.15, double minFrequencyHz = 45.0, double maxFrequencyHz = 2500.0)
    {
        if (threshold is <= 0.0 or >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "Threshold must lie in the open interval (0, 1).");
        if (minFrequencyHz <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(minFrequencyHz), minFrequencyHz, "Minimum frequency must be positive.");
        if (maxFrequencyHz <= minFrequencyHz)
            throw new ArgumentOutOfRangeException(nameof(maxFrequencyHz), maxFrequencyHz, "Maximum frequency must exceed the minimum.");

        Threshold = threshold;
        MinFrequencyHz = minFrequencyHz;
        MaxFrequencyHz = maxFrequencyHz;
    }

    /// <summary>Defaults covering MIDI 33–96 with head-room at both ends (see the plan's sizing note).</summary>
    public static YinOptions Default { get; } = new();
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~YinOptionsTests"
```

Expected PASS: defaults and all four validation cases green.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit step-04-yin -m "feat(domain): YinOptions named threshold and search range" --changes <ids from but status -fv> --status-after
```

---

## Task 3: `YinPitchDetector.Detect` — voiced estimate within ±10 cents

The core algorithm. Drive it out with an example first (A4 = 440 Hz), plus a landmark `[Theory]` sweep that gives fast, deterministic ±10-cent signal across the low, middle, and high of the required range (R4.2). Follow @superpowers:test-driven-development: watch it go red, then green.

**Files:**
- Create: `src/AudioClaudio.Domain/Detection/YinPitchDetector.cs`
- Test: `tests/AudioClaudio.Tests/Detection/YinPitchDetectorTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests;

public class YinPitchDetectorTests
{
    private const int Rate = 44100;
    private const int FrameSize = 4096;                 // N; W = N/2 = 2048 (see plan sizing)
    private static readonly SampleRate R = new(Rate);

    /// <summary>Single point of coupling to the Step 2 Frame constructor.</summary>
    private static Frame MakeFrame(float[] samples) =>
        new(samples, new SamplePosition(0L, R));

    [Fact]
    [Trait("Category", "Fast")]
    public void Detect_PureSineA4_IsVoicedWithin10Cents()
    {
        double hz = new Pitch(69).Frequency();          // A4 = 440 Hz
        Frame frame = MakeFrame(SignalGenerator.Sine(hz, FrameSize, R));

        PitchEstimate e = YinPitchDetector.Detect(frame, YinOptions.Default);

        Assert.True(e.IsVoiced);
        Assert.True(Math.Abs(PitchMath.CentsBetween(hz, e.FrequencyHz)) <= 10.0,
            $"A4: {PitchMath.CentsBetween(hz, e.FrequencyHz):F2} cents off (got {e.FrequencyHz:F3} Hz)");
        Assert.True(e.Confidence > 0.9, $"expected a deep dip; confidence was {e.Confidence:F3}");
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(33)]   // A1  ~55 Hz  — the low-end window stress case
    [InlineData(45)]   // A2  ~110 Hz
    [InlineData(57)]   // A3  ~220 Hz
    [InlineData(69)]   // A4  440 Hz
    [InlineData(84)]   // C6  ~1047 Hz
    [InlineData(96)]   // C7  ~2093 Hz — parabolic-interpolation stress case
    public void Detect_LandmarkPitches_Within10Cents(int midi)
    {
        double hz = new Pitch(midi).Frequency();
        Frame frame = MakeFrame(SignalGenerator.Sine(hz, FrameSize, R));

        PitchEstimate e = YinPitchDetector.Detect(frame, YinOptions.Default);

        Assert.True(e.IsVoiced, $"MIDI {midi} came back unvoiced");
        double cents = PitchMath.CentsBetween(hz, e.FrequencyHz);
        Assert.True(Math.Abs(cents) <= 10.0, $"MIDI {midi}: {cents:F2} cents off (got {e.FrequencyHz:F3} Hz)");
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~YinPitchDetectorTests.Detect_PureSineA4_IsVoicedWithin10Cents"
```

Expected FAILURE: compile error — `YinPitchDetector` does not exist yet.

**Step 3 — Minimal implementation:**

```csharp
using System;

namespace AudioClaudio.Domain;

/// <summary>
/// Monophonic fundamental-frequency estimator via the YIN algorithm
/// (de Cheveigné &amp; Kawahara, 2002): the difference function, its
/// cumulative-mean normalization, an absolute threshold with the smallest-lag
/// rule, and parabolic interpolation. Pure and per-frame (R4.4); deterministic
/// (non-negotiable 3). Works in the lag/period domain, so piano partials do not
/// pull the estimate to an overtone (R4.3).
/// </summary>
public static class YinPitchDetector
{
    /// <summary>Detect with the default options.</summary>
    public static PitchEstimate Detect(Frame frame) => Detect(frame, YinOptions.Default);

    /// <summary>
    /// Estimate the fundamental of one frame, or report it unvoiced. Identical
    /// input yields an identical estimate on every run and machine.
    /// </summary>
    public static PitchEstimate Detect(Frame frame, YinOptions options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);

        float[] x = frame.Samples;            // mono PCM in [-1, 1]  (single Frame-shape coupling)
        int n = x.Length;
        int rate = frame.Rate.Hz;

        // Split the frame: integration window W and lag search each get half, so
        // the deepest access x[j + tau] with j < W and tau <= N/2 stays in bounds.
        int w = n / 2;
        int tauCeiling = n / 2;

        // A high frequency is a short period (small lag); a low frequency a long one.
        int tauMin = Math.Max(1, (int)Math.Floor(rate / options.MaxFrequencyHz));
        int tauMax = Math.Min(tauCeiling, (int)Math.Ceiling(rate / options.MinFrequencyHz));

        if (w <= 0 || tauMin >= tauMax)
            return PitchEstimate.Unvoiced;

        // (1) Difference function d(tau), computed over the FULL range 1..tauMax so
        //     the cumulative mean in step (2) is exact; the search is narrowed later.
        double[] d = new double[tauMax + 1];
        for (int tau = 1; tau <= tauMax; tau++)
        {
            double sum = 0.0;
            for (int j = 0; j < w; j++)
            {
                double diff = x[j] - x[j + tau];
                sum += diff * diff;
            }
            d[tau] = sum;
        }

        // (2) Cumulative-mean-normalized difference d'(tau). d'(0) = 1 by convention;
        //     an all-zero (silent) frame stays at 1 everywhere and reads unvoiced.
        double[] dPrime = new double[tauMax + 1];
        dPrime[0] = 1.0;
        double runningSum = 0.0;
        for (int tau = 1; tau <= tauMax; tau++)
        {
            runningSum += d[tau];
            dPrime[tau] = runningSum > 0.0 ? d[tau] * tau / runningSum : 1.0;
        }

        // (3) Absolute threshold + smallest-lag rule. Within the plausible window,
        //     take the first lag whose d' dips below the threshold, then descend to
        //     the bottom of that dip. Smallest qualifying lag => the fundamental,
        //     not a multiple of it (this is what avoids octave errors).
        int tauStar = -1;
        for (int tau = tauMin; tau <= tauMax; tau++)
        {
            if (dPrime[tau] < options.Threshold)
            {
                while (tau + 1 <= tauMax && dPrime[tau + 1] < dPrime[tau])
                    tau++;
                tauStar = tau;
                break;
            }
        }

        if (tauStar == -1)
            return PitchEstimate.Unvoiced;   // no periodic dip => silence or noise (R4.1)

        // (4) Parabolic interpolation for sub-sample precision — essential at the
        //     top of the range where one whole sample of lag is several cents.
        double refinedTau = ParabolicMinimum(dPrime, tauStar, tauMin, tauMax);

        double f0 = rate / refinedTau;
        double confidence = Math.Clamp(1.0 - dPrime[tauStar], 0.0, 1.0);
        return PitchEstimate.Voiced(f0, confidence);
    }

    /// <summary>
    /// Refine an integer lag minimum to sub-sample precision by fitting a parabola
    /// through d' at (tau-1, tau, tau+1) and returning its vertex. Falls back to the
    /// integer lag at the search edges or a degenerate (flat/divergent) fit.
    /// </summary>
    private static double ParabolicMinimum(double[] dPrime, int tau, int tauMin, int tauMax)
    {
        if (tau <= tauMin || tau >= tauMax)
            return tau;

        double s0 = dPrime[tau - 1];
        double s1 = dPrime[tau];
        double s2 = dPrime[tau + 1];

        double denom = s0 + s2 - 2.0 * s1;
        if (denom == 0.0)
            return tau;                       // flat: no better estimate than the integer lag

        double delta = 0.5 * (s0 - s2) / denom;
        if (delta is < -1.0 or > 1.0)
            return tau;                       // divergent fit: keep the integer lag

        return tau + delta;
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~YinPitchDetectorTests.Detect_PureSineA4_IsVoicedWithin10Cents"
dotnet test --filter "FullyQualifiedName~YinPitchDetectorTests.Detect_LandmarkPitches_Within10Cents"
```

Expected PASS: A4 voiced and within a fraction of a cent; all six landmarks within ±10 cents (the C7 case is the one parabolic interpolation rescues — if it fails, that fallback or the `tauMin` head-room is wrong; debug with @superpowers:systematic-debugging).

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit step-04-yin -m "feat(domain): YIN detector core with parabolic interpolation" --changes <ids from but status -fv> --status-after
```

---

## Task 4: Unvoiced on silence and white noise

R4.1's other branch: a silent frame and a white-noise frame both return unvoiced. White-noise input is generated with a **fixed-seed** RNG *in the test* (the randomness is test input, not domain behavior — the domain stays deterministic). White noise's `d'` sits near 1.0 at every lag, so it never dips below the threshold; test ten seeds to be robust.

**Files:**
- Modify: `tests/AudioClaudio.Tests/Detection/YinPitchDetectorTests.cs`

**Step 1 — Write the failing test** (add these methods to the existing class):

```csharp
    [Fact]
    [Trait("Category", "Fast")]
    public void Detect_Silence_IsUnvoiced()
    {
        Frame frame = MakeFrame(new float[FrameSize]);   // all zeros

        PitchEstimate e = YinPitchDetector.Detect(frame, YinOptions.Default);

        Assert.False(e.IsVoiced);
        Assert.Equal(PitchEstimate.Unvoiced, e);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Detect_WhiteNoise_IsUnvoiced()
    {
        for (int seed = 1000; seed < 1010; seed++)
        {
            var rng = new Random(seed);                  // fixed seed => deterministic test input
            var samples = new float[FrameSize];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (float)(rng.NextDouble() * 2.0 - 1.0);

            PitchEstimate e = YinPitchDetector.Detect(MakeFrame(samples), YinOptions.Default);

            Assert.False(e.IsVoiced, $"white noise (seed {seed}) was reported voiced at {e.FrequencyHz:F1} Hz");
        }
    }
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~YinPitchDetectorTests.Detect_Silence_IsUnvoiced|FullyQualifiedName~YinPitchDetectorTests.Detect_WhiteNoise_IsUnvoiced"
```

Expected: with the Task 3 implementation already in place these should **pass immediately** — the silence and noise branches are already handled. If either fails, the threshold or the `runningSum > 0` guard is wrong; fix the code, not the test (§1 rule 8). (If you are following strict red-green and want to see red first, temporarily assert `Assert.True(e.IsVoiced)`, watch it fail, then flip it back.)

**Step 3 — Minimal implementation:** none required — the Task 3 `Detect` already returns `Unvoiced` for both cases. If a case is red, correct `Detect`.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~YinPitchDetectorTests"
```

Expected PASS: silence and all ten noise seeds unvoiced; Task 3 tests still green.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit step-04-yin -m "test(domain): silence and white noise are unvoiced" --changes <ids from but status -fv> --status-after
```

---

## Task 5: Property — pure sine within ±10 cents across MIDI 33–96

The headline property of the step (R4.2): over thousands of generated pitches, the detected `f0` is within 10 cents of the truth. CsCheck with a fixed seed and a large iteration count; marked **Slow** so `--filter Category=Fast` skips it.

**Files:**
- Modify: `tests/AudioClaudio.Tests/Detection/YinPitchDetectorTests.cs`

**Step 1 — Write the failing test** (add `using CsCheck;` at the top of the file; add this method):

```csharp
    [Fact]
    [Trait("Category", "Slow")]
    public void Property_PureSine_Within10CentsAcrossRange()
    {
        // A frequency uniformly inside each semitone band so we exercise off-grid
        // fundamentals too, not just the 64 exact piano pitches.
        Gen<double> hzGen =
            from midi in Gen.Int[33, 96]
            from detune in Gen.Double[-0.49, 0.49]     // fraction of a semitone
            select new Pitch(midi).Frequency() * Math.Pow(2.0, detune / 12.0);

        hzGen.Sample(hz =>
        {
            Frame frame = MakeFrame(SignalGenerator.Sine(hz, FrameSize, R));
            PitchEstimate e = YinPitchDetector.Detect(frame, YinOptions.Default);

            Assert.True(e.IsVoiced, $"{hz:F2} Hz came back unvoiced");
            double cents = PitchMath.CentsBetween(hz, e.FrequencyHz);
            Assert.True(Math.Abs(cents) <= 10.0, $"{hz:F2} Hz: {cents:F2} cents off (got {e.FrequencyHz:F3} Hz)");
        },
        iter: 2000, seed: "0N0XdO8lNYZ0");
        // Seed pins reproducibility (§5). CsCheck seeds are interchangeable; if this
        // literal is rejected by your CsCheck version, run once and pin the seed it prints.
    }
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~YinPitchDetectorTests.Property_PureSine_Within10CentsAcrossRange"
```

Expected: with Task 3 in place this generally **passes**. If a case fails, CsCheck shrinks to the smallest offending frequency and prints its seed — a genuine accuracy bug (likely the very top of the range needing more parabolic head-room, or the low end needing a larger `W`). Treat the test as right (§1 rule 8) and fix `Detect`/sizing with @superpowers:systematic-debugging.

**Step 3 — Minimal implementation:** none beyond Task 3, unless a shrunk counter-example exposes a real accuracy gap. Per R4.2 the required range is MIDI 33–96; if a handful of cases at the extreme top prove irreducibly marginal, that status is **documented in `DECISIONS.md`, not hidden** — but first try widening `W` (a larger frame) or the search head-room.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~YinPitchDetectorTests.Property_PureSine_Within10CentsAcrossRange"
```

Expected PASS: 2000 generated cases all within ±10 cents.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit step-04-yin -m "test(domain): pure-sine cents-accuracy property (MIDI 33-96)" --changes <ids from but status -fv> --status-after
```

---

## Task 6: Property — harmonic stack, ±10 cents and no octave error

R4.3: piano-like partials must not pull the estimate to an overtone. Generate harmonic stacks (fundamental + `k` partials decaying as `1/k^p`) across the range and assert the estimate is within ±10 cents of the **fundamental**. A ±10-cent bound mathematically precludes an octave error (an octave is 1200 cents away), so this single assertion *is* the no-octave-error guarantee; an explicit octave check is added for a clear failure message. Marked **Slow**.

**Files:**
- Modify: `tests/AudioClaudio.Tests/Detection/YinPitchDetectorTests.cs`

**Step 1 — Write the failing test** (add this method):

```csharp
    [Fact]
    [Trait("Category", "Slow")]
    public void Property_HarmonicStack_Within10CentsAndNoOctaveError()
    {
        var gen =
            from midi in Gen.Int[33, 96]
            from partials in Gen.Int[3, 8]             // fundamental + up to 7 overtones
            from decay in Gen.Double[0.5, 2.0]         // amplitude of partial k = 1/k^decay
            select (midi, partials, decay);

        gen.Sample(t =>
        {
            double f0 = new Pitch(t.midi).Frequency();
            Frame frame = MakeFrame(
                SignalGenerator.HarmonicStack(f0, FrameSize, R, partials: t.partials, decay: t.decay));

            PitchEstimate e = YinPitchDetector.Detect(frame, YinOptions.Default);

            Assert.True(e.IsVoiced, $"MIDI {t.midi} (k={t.partials}, p={t.decay:F2}) came back unvoiced");

            double cents = PitchMath.CentsBetween(f0, e.FrequencyHz);
            Assert.True(Math.Abs(cents) <= 10.0,
                $"MIDI {t.midi} (k={t.partials}, p={t.decay:F2}): {cents:F2} cents off (got {e.FrequencyHz:F3} Hz)");

            // Explicit octave-error guard: the estimate must sit far nearer the
            // fundamental than either the octave-up partial or the octave-down alias.
            double toFundamental = Math.Abs(PitchMath.CentsBetween(f0, e.FrequencyHz));
            double toOctaveUp    = Math.Abs(PitchMath.CentsBetween(2.0 * f0, e.FrequencyHz));
            double toOctaveDown  = Math.Abs(PitchMath.CentsBetween(0.5 * f0, e.FrequencyHz));
            Assert.True(toFundamental < toOctaveUp && toFundamental < toOctaveDown,
                $"octave error at MIDI {t.midi}: locked near {e.FrequencyHz:F2} Hz");
        },
        iter: 1000, seed: "0N0XdO8lNYZ0");
    }
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~YinPitchDetectorTests.Property_HarmonicStack_Within10CentsAndNoOctaveError"
```

Expected: passes if the smallest-lag rule and cumulative-mean normalization are correct. A failure that shrinks to a high partial count / shallow decay means an overtone is winning — the classic octave-up trap; revisit the threshold and the smallest-lag descent in `Detect` with @superpowers:systematic-debugging.

**Step 3 — Minimal implementation:** none beyond Task 3 unless a counter-example exposes a real octave error; then fix `Detect` (the test is presumed right, §1 rule 8).

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~YinPitchDetectorTests.Property_HarmonicStack_Within10CentsAndNoOctaveError"
```

Expected PASS: 1000 harmonic-stack cases within ±10 cents, zero octave errors.

**Step 5 — Commit:**

```bash
dotnet format
but status -fv
but commit step-04-yin -m "test(domain): harmonic-stack robustness and no-octave-error property" --changes <ids from but status -fv> --status-after
```

---

## Task 7: Determinism and purity (R4.4)

The detector is a pure function of the frame: the same frame yields a bit-for-bit identical estimate every call (non-negotiable 3), and it reads nothing but the frame — no clock, no device. Purity is *also* enforced structurally: `AudioClaudio.Domain` cannot reference `IClock`, `DateTime`, or any device type, so the compiler already forbids the violation. This test nails the runtime determinism half.

**Files:**
- Modify: `tests/AudioClaudio.Tests/Detection/YinPitchDetectorTests.cs`

**Step 1 — Write the failing test** (add this method):

```csharp
    [Fact]
    [Trait("Category", "Fast")]
    public void Detect_IsDeterministic_SameFrameSameEstimate()
    {
        double hz = new Pitch(60).Frequency();          // middle C
        float[] samples = SignalGenerator.Sine(hz, FrameSize, R);

        PitchEstimate first  = YinPitchDetector.Detect(MakeFrame(samples), YinOptions.Default);
        PitchEstimate second = YinPitchDetector.Detect(MakeFrame(samples), YinOptions.Default);

        // Value equality across all fields — no run-to-run drift.
        Assert.Equal(first, second);
        Assert.Equal(first.FrequencyHz, second.FrequencyHz, precision: 12);
        Assert.Equal(first.Confidence, second.Confidence, precision: 12);
    }
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~YinPitchDetectorTests.Detect_IsDeterministic_SameFrameSameEstimate"
```

Expected: passes with the Task 3 implementation (the algorithm is branch-deterministic with no randomness). To see red first, temporarily assert `Assert.NotEqual(first, second)`, watch it fail, then restore.

**Step 3 — Minimal implementation:** none — `Detect` is already pure and deterministic. If this ever fails, a non-deterministic construct crept in (e.g. iterating a hash set); remove it.

**Step 4 — Run to verify it passes, and confirm the whole step and the Fast filter are green:**

```bash
dotnet test --filter "FullyQualifiedName~YinPitchDetectorTests"
dotnet test --filter Category=Fast
dotnet test
```

Expected PASS: the determinism test green; the full Step 4 class green; the Fast filter completes quickly (Slow properties skipped); the full suite green including both CsCheck properties.

**Step 5 — Commit** (this task carries the spec commit message, closing the step):

```bash
dotnet format
but status -fv
but commit step-04-yin -m "feat(domain): YIN pitch detection with cents-accuracy properties" --changes <ids from but status -fv> --status-after
```

---

## Verify (step exit criteria)

- [ ] **Property (headline):** for any generated pitch in MIDI 33–96 and any harmonic-stack profile, the detected `f0` is within **10 cents** of the true fundamental — thousands of generated cases (`Property_PureSine_Within10CentsAcrossRange`, `Property_HarmonicStack_Within10CentsAndNoOctaveError`).
- [ ] **Property:** octave errors are **zero** across the generated corpus (`Property_HarmonicStack_Within10CentsAndNoOctaveError`, both the ≤10-cent bound and the explicit octave guard).
- [ ] **Example:** a silent frame and a white-noise frame both return **unvoiced** (`Detect_Silence_IsUnvoiced`, `Detect_WhiteNoise_IsUnvoiced`).
- [ ] **R4.1:** every voiced result carries a positive frequency and a confidence in [0, 1]; the voiced/unvoiced threshold is a named `YinOptions.Threshold`.
- [ ] **R4.4:** `Detect` is a pure static Domain function; determinism proven (`Detect_IsDeterministic_SameFrameSameEstimate`), purity enforced by the dependency rule.

## Definition of Done

- [ ] `dotnet build` clean; `dotnet format` reports no changes.
- [ ] All new tests green: `dotnet test` (full) **and** `dotnet test --filter Category=Fast` (Slow properties correctly skipped).
- [ ] Dependency rule intact: the three new files live in `AudioClaudio.Domain`, reference only the BCL and Step 1–2 domain types, and touch no port, clock, or device.
- [ ] Requirement-coverage table fully satisfied — every R4.x mapped to a passing test.
- [ ] Committed via the @gitbutler skill; the closing commit uses the spec message `feat(domain): YIN pitch detection with cents-accuracy properties` (finer commits roll up to it).
- [ ] **DECISIONS.md:** no new NuGet package was added (Domain stays BCL-only), so no license entry is required. Add an entry only if the R4.2 top-of-range status has to be documented (e.g. a specific high pitch left outside ±10 cents), per R4.2's "documented, not hidden."
