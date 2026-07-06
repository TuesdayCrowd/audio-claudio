# Step 5 — Onset Detection and Note Segmentation — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Build-spec step:** Section 6 Step 5 (R5.1, R5.2, R5.3, R5.4)
**Goal:** Turn the per-frame magnitude spectra and pitch track into discrete `NoteEvent`s via a spectral-flux onset function with adaptive peak-picking, a segmenter that opens a note at each onset and closes it at the next onset / unvoiced transition / decay-below-floor, a minimum-duration flicker filter, and correct separation of repeated same-pitch notes by their onsets.
**Architecture:** Everything here is pure `AudioClaudio.Domain` — two small stateless components (`OnsetDetector`, `NoteSegmenter`) plus their option records and the `SpectralFlux` helper and `FrameObservation` input type. No ports, no I/O, no clock (non-negotiables 1–3). Step 3's per-frame magnitude spectra and Step 4's per-frame voiced-pitch decisions are the inputs; the composition root (a later step / Step 9) adapts those upstream types into the plain BCL shapes these components accept.
**Tech Stack:** .NET 10 BCL only in the Domain; xUnit (Apache-2.0) + CsCheck (MIT) in `tests/AudioClaudio.Tests`. No new NuGet packages.
**Prerequisites:** Steps 0–4 green and committed (Section 1 rule 3). Specifically Step 1 (`Pitch`, `SampleRate`, `SamplePosition`, `SampleDuration`, `NoteEvent`), Step 2 (`Frame`, `IAudioSource`, WAV adapter, the deterministic signal generator), Step 3 (windowed spectral front end producing per-frame magnitude spectra), Step 4 (YIN detector emitting, per frame, a voiced fundamental or *unvoiced*).
**Commit (spec):** `feat(domain): onset detection and note segmentation`

---

## Approach

Step 4 tells us *what pitch* sounds in each frame; it does not tell us *when a note begins*. Section 4 makes that a deliberate split: onset detection is a spectral-energy novelty problem, pitch detection is separate, and keeping them apart is the only thing that lets two repeated C4 quarter notes come back as two events instead of one held note (R5.4). Step 5 joins the two tracks.

**Onset novelty — spectral flux (R5.1).** A piano attack injects energy across many frequency bins at once, so the frame-to-frame *increase* in magnitude spikes at note starts and is near zero during steady sustain and decay. We compute the half-wave-rectified flux `flux[m] = Σ_k max(0, |X_m[k]| − |X_{m−1}[k]|)`: only positive changes count, so decay (a falling spectrum) contributes nothing. Frame 0 is measured against an implicit all-zero "previous" frame, so a note that begins in the very first frame still registers.

**Adaptive peak-picking (R5.1).** The flux magnitude depends on the FFT's scaling, which the Domain here cannot know. So we first normalize the novelty curve by its maximum (scale-free), then accept a frame as an onset when it is (a) a local maximum within a small radius, (b) above an adaptive threshold `multiplier · localMean + delta` computed over a window around it, and (c) at least `MinGapFrames` after the previously accepted onset — which suppresses the double-triggering a single sharp attack can cause. Ties break toward the earlier frame, so the output is deterministic (non-negotiable 3).

**Segmentation (R5.2–R5.4).** Each detected onset opens a candidate note. We label it with the first *stable* voiced pitch — the first run of `StabilityFrames` consecutive voiced frames agreeing on one MIDI number — so an attack transient that YIN briefly misreads cannot mislabel the note. The note's onset time is the detected onset (frame-resolution, hence "within ±1 hop"); its pitch is the stable one. The note closes at the earliest of: the **next onset** (this is what splits legato repeated same-pitch notes, R5.4), a **transition to unvoiced or a different pitch**, or a **decay below floor** (the level falls under a fraction of the note's peak, R5.2). Finally, any note shorter than the minimum duration is dropped as flicker (R5.3), and an onset that never finds a stable pitch (e.g. a spurious peak over noise) is dropped too.

All arithmetic is on integer `SamplePosition`/`SampleDuration` carried with their `SampleRate` (non-negotiable 1); pitch comparisons are on MIDI numbers, i.e. in cents/MIDI space (non-negotiable 4); every tie-break is defined, so the same input yields the identical event sequence every run (non-negotiable 3).

**Cross-step seam (deliberately left for the composition root / Step 9):** the `OnsetDetector` takes magnitude spectra as `IReadOnlyList<IReadOnlyList<double>>` and the `NoteSegmenter` takes a parallel `IReadOnlyList<FrameObservation>` (one per frame). Wiring Step 3's spectrum type and Step 4's pitch/energy per frame into these two shapes is a composition-root concern; this step implements and unit-tests the pure Domain components against directly-constructed inputs.

---

## Requirements coverage

| Requirement | Task(s) | Proven by (test) |
|---|---|---|
| **R5.1** spectral flux + adaptive peak-picking → candidate onsets at a `SamplePosition` | 1, 2, 3 | `SpectralFluxTests.*`; `OnsetPeakPickingTests.*`; `OnsetDetectorTests.DetectFindsOneOnsetPerNoteStart`, `OnsetDetectorTests.OnsetsAreReportedAtTheirFrameStartPosition` |
| **R5.2** segmentation combines onsets + pitch track; note begins at an onset with a stable voiced pitch and ends at next onset / unvoiced transition / decay-below-floor (decay-below-floor is **opt-in** via `DecayFloorRatio`, default `0`/disabled; making that third termination path live end-to-end is a follow-up obligation on the Step 9/Step 10 composition root, which SHALL choose a sensible ratio) | 4, 6, 7 | `NoteSegmenterTests.SingleNoteBecomesOneEventEndingAtUnvoiced`; `NoteSegmenterTests.BackToBackSamePitchNotesAreSplitByTheSecondOnset`; `NoteSegmenterTests.NoteEndsWhenLevelDecaysBelowFloor` |
| **R5.3** minimum note duration (named parameter, ~50 ms in samples) suppresses flicker | 4, 5 | `NoteSegmenterTests.OnsetWithoutStableVoicedPitchIsDropped`; `NoteSegmenterTests.NotesShorterThanMinimumAreDroppedAsFlicker` |
| **R5.4** repeated same-pitch notes separated by their onsets → distinct events | 6 | `NoteSegmenterTests.RepeatedSamePitchNotesSeparatedByRestAreDistinctEvents`; `NoteSegmenterTests.BackToBackSamePitchNotesAreSplitByTheSecondOnset` |
| Non-negotiable 3 (determinism) | 6 | `NoteSegmenterTests.SegmentationIsDeterministic` |
| **Verify** — golden five-note & count property | 8 | `OnsetSegmentationGoldenTests.FiveNotesWithSilencesYieldExactlyFiveEventsWithAccurateOnsets`; `OnsetSegmentationGoldenTests.EventCountEqualsTrueNoteCountForGappedSequences` |

---

## Branch setup (once, before Task 1)

Use the **gitbutler** skill. Create and mark a virtual branch so every edit auto-stages to it:

```bash
but branch new step-05-onset-segmentation
but mark step-05-onset-segmentation
```

Each task ends with a finer-grained `but commit`; they roll up to the spec message `feat(domain): onset detection and note segmentation`. Get fresh change IDs with `but status -fv` at commit time.

---

## Task 1: `SpectralFlux` — the onset novelty function

Follow @superpowers:test-driven-development (red → green → refactor) for every task.

**Files:**
- Create: `src/AudioClaudio.Domain/SpectralFlux.cs`
- Test: `tests/AudioClaudio.Tests/Domain/SpectralFluxTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System.Collections.Generic;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public sealed class SpectralFluxTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void FrameZeroMeasuresIncreaseFromImplicitSilence()
    {
        var spectra = new List<IReadOnlyList<double>>
        {
            new double[] { 1.0, 2.0, 3.0 },
        };

        double[] novelty = SpectralFlux.Compute(spectra);

        Assert.Single(novelty);
        Assert.Equal(6.0, novelty[0], 10);   // whole spectrum appears from silence
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void RisingMagnitudeProducesPositiveFlux()
    {
        var spectra = new List<IReadOnlyList<double>>
        {
            new double[] { 0.0, 0.0 },
            new double[] { 1.0, 4.0 },
        };

        double[] novelty = SpectralFlux.Compute(spectra);

        Assert.Equal(0.0, novelty[0], 10);
        Assert.Equal(5.0, novelty[1], 10);   // (1-0) + (4-0)
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void FallingMagnitudeIsRectifiedToZero()
    {
        var spectra = new List<IReadOnlyList<double>>
        {
            new double[] { 5.0, 5.0 },
            new double[] { 1.0, 0.0 },
        };

        double[] novelty = SpectralFlux.Compute(spectra);

        // Every bin decreased; half-wave rectification zeroes the decay.
        Assert.Equal(0.0, novelty[1], 10);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~SpectralFluxTests"
```

Expected FAILURE: compile error `The name 'SpectralFlux' does not exist` — the class does not exist yet.

**Step 3 — Minimal implementation:**

```csharp
using System;
using System.Collections.Generic;

namespace AudioClaudio.Domain;

/// <summary>
/// The onset novelty function (R5.1): the half-wave-rectified frame-to-frame
/// increase in spectral magnitude. A note attack injects energy across many bins
/// at once, producing a positive spike; steady sustain and decay produce ~zero.
/// Pure and deterministic — no state, no I/O, no clock.
/// </summary>
public static class SpectralFlux
{
    /// <summary>
    /// Computes the spectral-flux novelty for a sequence of per-frame magnitude
    /// spectra. The result has one value per frame. Index 0 is measured against an
    /// implicit all-zero "previous" frame, so a note that starts in frame 0 still
    /// registers an onset. Only positive changes count (half-wave rectification):
    /// flux[m] = Σ_k max(0, |X_m[k]| − |X_{m-1}[k]|). Magnitudes are non-negative
    /// linear FFT magnitudes; the units cancel because peak-picking normalizes later.
    /// </summary>
    public static double[] Compute(IReadOnlyList<IReadOnlyList<double>> magnitudeSpectra)
    {
        ArgumentNullException.ThrowIfNull(magnitudeSpectra);

        int frameCount = magnitudeSpectra.Count;
        var novelty = new double[frameCount];

        for (int m = 0; m < frameCount; m++)
        {
            IReadOnlyList<double> current = magnitudeSpectra[m];
            IReadOnlyList<double>? previous = m > 0 ? magnitudeSpectra[m - 1] : null;

            double sum = 0.0;
            for (int k = 0; k < current.Count; k++)
            {
                double prev = previous is not null && k < previous.Count ? previous[k] : 0.0;
                double diff = current[k] - prev;
                if (diff > 0.0)
                {
                    sum += diff;
                }
            }

            novelty[m] = sum;
        }

        return novelty;
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~SpectralFluxTests"
```

Expected PASS: 3 tests green.

**Step 5 — Commit** (via the **gitbutler** skill; get `<ids>` from `but status -fv`):

```bash
but status -fv
but commit step-05-onset-segmentation -m "feat(domain): spectral-flux onset novelty function" --changes <ids> --status-after
```

---

## Task 2: `OnsetDetectorOptions` + `OnsetDetector.PickPeaks` — adaptive peak-picking

**Files:**
- Create: `src/AudioClaudio.Domain/OnsetDetectorOptions.cs`
- Create: `src/AudioClaudio.Domain/OnsetDetector.cs`
- Test: `tests/AudioClaudio.Tests/Domain/OnsetPeakPickingTests.cs`

**Step 1 — Write the failing test:** (peak-picking is tested directly on hand-built novelty curves, so it is independent of the FFT)

```csharp
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public sealed class OnsetPeakPickingTests
{
    private static readonly OnsetDetector Detector = new(new OnsetDetectorOptions
    {
        ThresholdWindowFrames = 4,
        ThresholdMultiplier = 1.0,
        ThresholdDelta = 0.1,
        LocalMaxRadiusFrames = 1,
        MinGapFrames = 3,
    });

    [Fact]
    [Trait("Category", "Fast")]
    public void SingleSpikeYieldsOneOnsetAtTheSpike()
    {
        var novelty = new double[] { 0, 0, 0, 1.0, 0, 0, 0, 0 };

        var onsets = Detector.PickPeaks(novelty);

        Assert.Equal(new[] { 3 }, onsets);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void TwoWellSeparatedSpikesYieldTwoOnsets()
    {
        var novelty = new double[] { 0, 0, 1.0, 0, 0, 0, 0, 0, 1.0, 0, 0 };

        var onsets = Detector.PickPeaks(novelty);

        Assert.Equal(new[] { 2, 8 }, onsets);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void AdjacentSpikesWithinMinGapCollapseToTheFirst()
    {
        var novelty = new double[] { 0, 0, 1.0, 0.9, 0, 0, 0, 0 };

        var onsets = Detector.PickPeaks(novelty);

        Assert.Equal(new[] { 2 }, onsets);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void FlatSilentNoveltyYieldsNoOnsets()
    {
        var novelty = new double[] { 0, 0, 0, 0, 0 };

        var onsets = Detector.PickPeaks(novelty);

        Assert.Empty(onsets);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~OnsetPeakPickingTests"
```

Expected FAILURE: compile errors `The type or namespace 'OnsetDetector'`/`'OnsetDetectorOptions'` does not exist.

**Step 3 — Minimal implementation:**

`OnsetDetectorOptions.cs`:

```csharp
namespace AudioClaudio.Domain;

/// <summary>
/// Tunable parameters for adaptive onset peak-picking (R5.1). Defaults are a
/// reasonable starting point for the MVP frame/hop; the golden and property tests
/// (Task 8) validate them and may be revisited if they fail there.
/// </summary>
public sealed record OnsetDetectorOptions
{
    /// <summary>Half-width, in frames, of the window used for the adaptive local mean.</summary>
    public int ThresholdWindowFrames { get; init; } = 8;

    /// <summary>A peak's normalized novelty must exceed Multiplier · localMean + Delta.</summary>
    public double ThresholdMultiplier { get; init; } = 1.0;

    /// <summary>Absolute margin above the local mean, in normalized-novelty units [0,1].</summary>
    public double ThresholdDelta { get; init; } = 0.1;

    /// <summary>A peak must be a local maximum within ± this many frames.</summary>
    public int LocalMaxRadiusFrames { get; init; } = 1;

    /// <summary>Minimum spacing, in frames, between accepted onsets; suppresses attack double-triggers.</summary>
    public int MinGapFrames { get; init; } = 3;
}
```

`OnsetDetector.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace AudioClaudio.Domain;

/// <summary>
/// Detects note onsets from a spectral-flux novelty curve (R5.1). Pure and
/// deterministic — no state, no I/O, no clock.
/// </summary>
public sealed class OnsetDetector
{
    private readonly OnsetDetectorOptions _options;

    public OnsetDetector()
        : this(new OnsetDetectorOptions())
    {
    }

    public OnsetDetector(OnsetDetectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Picks onset frame indices from a novelty curve. The curve is first normalized
    /// by its maximum, so the thresholds are independent of the FFT's magnitude scale.
    /// A frame is an onset iff it is a local maximum within LocalMaxRadiusFrames,
    /// exceeds the adaptive threshold (Multiplier · localMean + Delta), and is at least
    /// MinGapFrames after the previously accepted onset. Left ties lose (a plateau's
    /// first frame is chosen), so results are deterministic (non-negotiable 3).
    /// </summary>
    public IReadOnlyList<int> PickPeaks(IReadOnlyList<double> novelty)
    {
        ArgumentNullException.ThrowIfNull(novelty);

        int n = novelty.Count;
        var onsets = new List<int>();
        if (n == 0)
        {
            return onsets;
        }

        double max = 0.0;
        for (int i = 0; i < n; i++)
        {
            if (novelty[i] > max)
            {
                max = novelty[i];
            }
        }
        if (max <= 0.0)
        {
            return onsets;   // pure silence / no change → no onsets
        }

        int r = Math.Max(0, _options.LocalMaxRadiusFrames);
        int w = Math.Max(1, _options.ThresholdWindowFrames);
        int lastAccepted = int.MinValue;

        for (int m = 0; m < n; m++)
        {
            double value = novelty[m] / max;

            // Local-maximum test: strictly greater than left neighbours, >= right
            // neighbours (so the first frame of a plateau wins).
            bool isLocalMax = true;
            for (int j = m - r; j <= m + r && isLocalMax; j++)
            {
                if (j < 0 || j >= n || j == m)
                {
                    continue;
                }
                double neighbour = novelty[j] / max;
                if (j < m && value <= neighbour)
                {
                    isLocalMax = false;
                }
                else if (j > m && value < neighbour)
                {
                    isLocalMax = false;
                }
            }
            if (!isLocalMax)
            {
                continue;
            }

            // Adaptive threshold from the local mean of the normalized novelty.
            double sum = 0.0;
            int count = 0;
            for (int j = m - w; j <= m + w; j++)
            {
                if (j < 0 || j >= n)
                {
                    continue;
                }
                sum += novelty[j] / max;
                count++;
            }
            double localMean = count > 0 ? sum / count : 0.0;
            double threshold = (_options.ThresholdMultiplier * localMean) + _options.ThresholdDelta;
            if (value < threshold)
            {
                continue;
            }

            if (m - lastAccepted < _options.MinGapFrames)
            {
                continue;
            }

            onsets.Add(m);
            lastAccepted = m;
        }

        return onsets;
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~OnsetPeakPickingTests"
```

Expected PASS: 4 tests green.

**Step 5 — Commit:**

```bash
but status -fv
but commit step-05-onset-segmentation -m "feat(domain): adaptive onset peak-picking" --changes <ids> --status-after
```

---

## Task 3: `OnsetDetector.Detect` / `DetectOnsetPositions` — flux + peaks over real spectra

This wires `SpectralFlux.Compute` into `PickPeaks` and exposes the R5.1 contract "candidate note start at a `SamplePosition`".

**Files:**
- Modify: `src/AudioClaudio.Domain/OnsetDetector.cs` (add two methods after `PickPeaks`)
- Test: `tests/AudioClaudio.Tests/Domain/OnsetDetectorTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System.Collections.Generic;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public sealed class OnsetDetectorTests
{
    private static readonly SampleRate Rate = new(44100);
    private const long Hop = 512;

    // 8 frames: silence, silence, note (3 sustain frames), silence, note (2 frames).
    private static List<IReadOnlyList<double>> TwoNoteSpectra() => new()
    {
        new double[] { 0, 0, 0, 0 },   // 0 silence
        new double[] { 0, 0, 0, 0 },   // 1 silence
        new double[] { 1, 1, 1, 1 },   // 2 note A attack
        new double[] { 1, 1, 1, 1 },   // 3 sustain
        new double[] { 1, 1, 1, 1 },   // 4 sustain
        new double[] { 0, 0, 0, 0 },   // 5 silence
        new double[] { 1, 1, 1, 1 },   // 6 note B attack
        new double[] { 1, 1, 1, 1 },   // 7 sustain
    };

    [Fact]
    [Trait("Category", "Fast")]
    public void DetectFindsOneOnsetPerNoteStart()
    {
        var onsets = new OnsetDetector().Detect(TwoNoteSpectra());

        Assert.Equal(new[] { 2, 6 }, onsets);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void OnsetsAreReportedAtTheirFrameStartPosition()
    {
        var spectra = TwoNoteSpectra();
        var starts = new List<SamplePosition>();
        for (int i = 0; i < spectra.Count; i++)
        {
            starts.Add(new SamplePosition(i * Hop, Rate));
        }

        var positions = new OnsetDetector().DetectOnsetPositions(spectra, starts);

        Assert.Equal(2, positions.Count);
        Assert.Equal(2 * Hop, positions[0].Samples);
        Assert.Equal(6 * Hop, positions[1].Samples);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~OnsetDetectorTests"
```

Expected FAILURE: compile errors — `Detect` and `DetectOnsetPositions` do not exist on `OnsetDetector`.

**Step 3 — Minimal implementation:** add to `OnsetDetector.cs` (inside the class, after `PickPeaks`):

```csharp
    /// <summary>
    /// Detects onsets from per-frame magnitude spectra: computes the spectral-flux
    /// novelty (<see cref="SpectralFlux"/>) then picks peaks. Returns frame indices.
    /// </summary>
    public IReadOnlyList<int> Detect(IReadOnlyList<IReadOnlyList<double>> magnitudeSpectra)
    {
        ArgumentNullException.ThrowIfNull(magnitudeSpectra);
        double[] novelty = SpectralFlux.Compute(magnitudeSpectra);
        return PickPeaks(novelty);
    }

    /// <summary>
    /// Detects onsets and expresses each as the starting <see cref="SamplePosition"/>
    /// of its frame (the R5.1 contract). <paramref name="frameStarts"/> must be parallel
    /// to <paramref name="magnitudeSpectra"/> (one start per frame).
    /// </summary>
    public IReadOnlyList<SamplePosition> DetectOnsetPositions(
        IReadOnlyList<IReadOnlyList<double>> magnitudeSpectra,
        IReadOnlyList<SamplePosition> frameStarts)
    {
        ArgumentNullException.ThrowIfNull(frameStarts);

        IReadOnlyList<int> frames = Detect(magnitudeSpectra);
        var positions = new List<SamplePosition>(frames.Count);
        foreach (int f in frames)
        {
            positions.Add(frameStarts[f]);
        }
        return positions;
    }
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~OnsetDetectorTests"
```

Expected PASS: 2 tests green. Sanity: the whole Fast suite still green — `dotnet test --filter Category=Fast`.

**Step 5 — Commit:**

```bash
but status -fv
but commit step-05-onset-segmentation -m "feat(domain): onset detection over magnitude spectra" --changes <ids> --status-after
```

---

## Task 4: `FrameObservation`, `NoteSegmenterOptions`, `NoteSegmenter` — the segmenter core (R5.2)

The core: open a note at each onset, label it with the first stable voiced pitch, close it at an unvoiced/different-pitch frame. *No minimum-duration filter and no next-onset bounding yet* — those are motivated red-greens in Tasks 5 and 6.

**Files:**
- Create: `src/AudioClaudio.Domain/FrameObservation.cs`
- Create: `src/AudioClaudio.Domain/NoteSegmenterOptions.cs`
- Create: `src/AudioClaudio.Domain/NoteSegmenter.cs`
- Test: `tests/AudioClaudio.Tests/Domain/NoteSegmenterTests.cs`

**Step 1 — Write the failing test:** (the shared `Voiced`/`Silent`/`MakeSegmenter` helpers are added here and reused by Tasks 5–7)

```csharp
using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public sealed class NoteSegmenterTests
{
    private static readonly SampleRate Rate = new(44100);
    private const long Hop = 512;

    private static FrameObservation Voiced(int index, int midi, double energy = 1.0)
        => new(new SamplePosition(index * Hop, Rate), new Pitch(midi), energy);

    private static FrameObservation Silent(int index)
        => new(new SamplePosition(index * Hop, Rate), null, 0.0);

    private static NoteSegmenter MakeSegmenter(long minDurationSamples = 1000, double decayFloor = 0.0)
        => new(new NoteSegmenterOptions
        {
            MinNoteDuration = new SampleDuration(minDurationSamples, Rate),
            StabilityFrames = 2,
            DecayFloorRatio = decayFloor,
            Velocity = 64,
        });

    [Fact]
    [Trait("Category", "Fast")]
    public void SingleNoteBecomesOneEventEndingAtUnvoiced()
    {
        var frames = new List<FrameObservation>
        {
            Silent(0), Silent(1),
            Voiced(2, 60), Voiced(3, 60), Voiced(4, 60),
            Voiced(5, 60), Voiced(6, 60), Voiced(7, 60),
            Silent(8), Silent(9),
        };

        var events = MakeSegmenter().Segment(frames, new[] { 2 });

        var e = Assert.Single(events);
        Assert.Equal(60, e.Pitch.MidiNumber);
        Assert.Equal(2 * Hop, e.Onset.Samples);          // onset at its frame start
        Assert.Equal((8 - 2) * Hop, e.Duration.Samples); // ends at the first unvoiced frame
        Assert.Equal(64, e.Velocity);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void OnsetWithoutStableVoicedPitchIsDropped()
    {
        var frames = new List<FrameObservation>
        {
            Silent(0), Silent(1), Silent(2), Silent(3), Silent(4),
        };

        var events = MakeSegmenter().Segment(frames, new[] { 2 });

        Assert.Empty(events);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~NoteSegmenterTests"
```

Expected FAILURE: compile errors — `FrameObservation`, `NoteSegmenterOptions`, `NoteSegmenter` do not exist.

**Step 3 — Minimal implementation:**

`FrameObservation.cs`:

```csharp
namespace AudioClaudio.Domain;

/// <summary>
/// The per-frame evidence the segmenter consumes: the frame's starting position,
/// the voiced pitch (or <c>null</c> when the YIN detector reported unvoiced, R4.1),
/// and a non-negative per-frame level (e.g. RMS amplitude) used for the
/// decay-below-floor end condition (R5.2). Voicing is encoded by <see cref="Pitch"/>
/// being non-null. <see cref="Pitch"/> is assumed to be a value type (Section 1's
/// contract: <c>Pitch { int MidiNumber }</c>).
/// </summary>
public readonly record struct FrameObservation(SamplePosition Start, Pitch? Pitch, double Energy)
{
    /// <summary>True when this frame carries a voiced pitch.</summary>
    public bool IsVoiced => Pitch.HasValue;
}
```

`NoteSegmenterOptions.cs`:

```csharp
namespace AudioClaudio.Domain;

/// <summary>Parameters for turning onsets + a pitch track into NoteEvents (R5.2–R5.4).</summary>
public sealed record NoteSegmenterOptions
{
    /// <summary>
    /// Notes shorter than this are discarded as flicker (R5.3). Expressed in integer
    /// samples via <see cref="SampleDuration"/>; the CLI derives it from ~50 ms and the
    /// sample rate. Required — a duration without its rate is a bug (non-negotiable 1).
    /// </summary>
    public required SampleDuration MinNoteDuration { get; init; }

    /// <summary>
    /// A note's pitch is committed only after this many consecutive voiced frames agree
    /// on one MIDI number, so attack-transient flicker cannot mislabel it (R5.2, R5.3).
    /// </summary>
    public int StabilityFrames { get; init; } = 2;

    /// <summary>
    /// If &gt; 0, a note ends when its level falls below this fraction of its peak level
    /// (the decay-below-floor condition, R5.2). 0 disables the amplitude floor, leaving
    /// termination to the next onset or the unvoiced transition. The default is 0
    /// (disabled), so the shipped default never exercises R5.2's third termination path;
    /// enabling it with a sensible ratio is a follow-up obligation on the Step 9/Step 10
    /// composition root (see the R5.2 requirements-coverage row), not something Step 5
    /// turns on by default.
    /// </summary>
    public double DecayFloorRatio { get; init; } = 0.0;

    /// <summary>Constant MVP velocity for emitted NoteEvents (R1.4).</summary>
    public int Velocity { get; init; } = 64;
}
```

`NoteSegmenter.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace AudioClaudio.Domain;

/// <summary>
/// Combines detected onsets with the per-frame pitch track to produce discrete
/// NoteEvents (R5.2). Each onset opens a note; the note is labelled with the first
/// stable voiced pitch found at/after the onset and closed at a transition to
/// unvoiced or a different pitch. Pure and deterministic (non-negotiable 3).
/// </summary>
public sealed class NoteSegmenter
{
    private readonly NoteSegmenterOptions _options;

    public NoteSegmenter(NoteSegmenterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Segments a pitch track into NoteEvents. <paramref name="frames"/> holds one
    /// observation per analysis frame in order; <paramref name="onsetFrames"/> holds
    /// onset frame indices (from <see cref="OnsetDetector.Detect"/>) in ascending order.
    /// </summary>
    public IReadOnlyList<NoteEvent> Segment(
        IReadOnlyList<FrameObservation> frames,
        IReadOnlyList<int> onsetFrames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(onsetFrames);

        var events = new List<NoteEvent>();
        int n = frames.Count;
        if (n == 0 || onsetFrames.Count == 0)
        {
            return events;
        }

        SampleRate rate = frames[0].Start.Rate;
        long hop = n >= 2 ? frames[1].Start.Samples - frames[0].Start.Samples : 0;

        foreach (int startFrame in onsetFrames)
        {
            (Pitch Pitch, int StableStart)? stable = FindStablePitch(frames, startFrame, n);
            if (stable is not { } found)
            {
                continue;   // spurious onset with no stable voiced pitch → drop
            }

            int endFrame = FindEndFrame(frames, found.StableStart, n, found.Pitch);

            SamplePosition onset = frames[startFrame].Start;
            long endSamples = endFrame < n
                ? frames[endFrame].Start.Samples
                : frames[n - 1].Start.Samples + hop;
            long durationSamples = endSamples - onset.Samples;

            events.Add(new NoteEvent(
                found.Pitch,
                onset,
                new SampleDuration(durationSamples, rate),
                _options.Velocity));
        }

        return events;
    }

    /// <summary>
    /// Returns the first pitch that stays constant across StabilityFrames consecutive
    /// voiced frames in [start, limit), together with the frame that run begins on;
    /// or null if no such run exists.
    /// </summary>
    private (Pitch Pitch, int StableStart)? FindStablePitch(
        IReadOnlyList<FrameObservation> frames, int start, int limit)
    {
        int runStart = -1;
        Pitch runPitch = default;
        int runLength = 0;

        for (int j = start; j < limit; j++)
        {
            if (frames[j].Pitch is not { } p)
            {
                runLength = 0;
                continue;
            }

            if (runLength > 0 && p.MidiNumber == runPitch.MidiNumber)
            {
                runLength++;
            }
            else
            {
                runPitch = p;
                runStart = j;
                runLength = 1;
            }

            if (runLength >= _options.StabilityFrames)
            {
                return (runPitch, runStart);
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the first frame in [stableStart, limit) at which the note terminates —
    /// a transition to unvoiced or to a different pitch — or <paramref name="limit"/>
    /// if it never terminates within the window.
    /// </summary>
    private static int FindEndFrame(
        IReadOnlyList<FrameObservation> frames, int stableStart, int limit, Pitch pitch)
    {
        for (int j = stableStart; j < limit; j++)
        {
            if (frames[j].Pitch is not { } p || p.MidiNumber != pitch.MidiNumber)
            {
                return j;
            }
        }

        return limit;
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~NoteSegmenterTests"
```

Expected PASS: 2 tests green.

**Step 5 — Commit:**

```bash
but status -fv
but commit step-05-onset-segmentation -m "feat(domain): note segmenter core (onset + stable-pitch)" --changes <ids> --status-after
```

---

## Task 5: minimum-duration flicker filter (R5.3)

**Files:**
- Modify: `src/AudioClaudio.Domain/NoteSegmenter.cs` (add the duration guard in `Segment`, just before `events.Add`)
- Test: `tests/AudioClaudio.Tests/Domain/NoteSegmenterTests.cs` (add one test to the existing class)

**Step 1 — Write the failing test:**

```csharp
    [Fact]
    [Trait("Category", "Fast")]
    public void NotesShorterThanMinimumAreDroppedAsFlicker()
    {
        var frames = new List<FrameObservation>
        {
            Silent(0), Silent(1),
            Voiced(2, 60), Voiced(3, 60), Voiced(4, 60),
            Voiced(5, 60), Voiced(6, 60), Voiced(7, 60),
            Silent(8),
            Voiced(9, 67), Voiced(10, 67),   // a 1024-sample blip
            Silent(11),
        };

        // Min duration 2000 samples: the long note (3072) survives, the blip (1024) is dropped.
        var events = MakeSegmenter(minDurationSamples: 2000).Segment(frames, new[] { 2, 9 });

        var e = Assert.Single(events);
        Assert.Equal(60, e.Pitch.MidiNumber);
    }
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~NoteSegmenterTests.NotesShorterThanMinimumAreDroppedAsFlicker"
```

Expected FAILURE: `Assert.Single() Failure: The collection contained 2 items` — without the filter, the 1024-sample blip is emitted alongside the long note.

**Step 3 — Minimal implementation:** in `NoteSegmenter.Segment`, insert the guard between the `durationSamples` computation and `events.Add`:

```csharp
            long durationSamples = endSamples - onset.Samples;

            if (durationSamples < _options.MinNoteDuration.Samples)
            {
                continue;   // shorter than the minimum note duration → flicker (R5.3)
            }

            events.Add(new NoteEvent(
                found.Pitch,
                onset,
                new SampleDuration(durationSamples, rate),
                _options.Velocity));
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~NoteSegmenterTests"
```

Expected PASS: 3 tests green (the new one plus the two from Task 4).

**Step 5 — Commit:**

```bash
but status -fv
but commit step-05-onset-segmentation -m "feat(domain): minimum-duration flicker filter" --changes <ids> --status-after
```

---

## Task 6: repeated same-pitch separation via next-onset bounding (R5.4) + determinism

This is Section 4's headline case: two same-pitch notes played back-to-back with no silence between them have no unvoiced frame to split them, so the *only* thing that separates them is the second onset. The core (Tasks 4–5) does not yet bound a note by the next onset, so a legato repeat merges into one over-long event. We fix that here.

**Files:**
- Modify: `src/AudioClaudio.Domain/NoteSegmenter.cs` (bound `FindStablePitch`/`FindEndFrame` by the next onset)
- Test: `tests/AudioClaudio.Tests/Domain/NoteSegmenterTests.cs` (add three tests)

**Step 1 — Write the failing test:**

```csharp
    [Fact]
    [Trait("Category", "Fast")]
    public void BackToBackSamePitchNotesAreSplitByTheSecondOnset()
    {
        // Ten consecutive voiced C4 frames, no silence anywhere; two onsets at 2 and 7.
        var frames = new List<FrameObservation>
        {
            Silent(0), Silent(1),
            Voiced(2, 60), Voiced(3, 60), Voiced(4, 60), Voiced(5, 60), Voiced(6, 60),
            Voiced(7, 60), Voiced(8, 60), Voiced(9, 60), Voiced(10, 60), Voiced(11, 60),
        };

        var events = MakeSegmenter().Segment(frames, new[] { 2, 7 });

        Assert.Equal(2, events.Count);
        Assert.Equal(2 * Hop, events[0].Onset.Samples);
        Assert.Equal(7 * Hop, events[1].Onset.Samples);
        // The first note must stop at the second onset, not run through it.
        Assert.Equal((7 - 2) * Hop, events[0].Duration.Samples);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void RepeatedSamePitchNotesSeparatedByRestAreDistinctEvents()
    {
        var frames = new List<FrameObservation>
        {
            Silent(0), Silent(1),
            Voiced(2, 60), Voiced(3, 60), Voiced(4, 60), Voiced(5, 60),
            Silent(6), Silent(7),
            Voiced(8, 60), Voiced(9, 60), Voiced(10, 60), Voiced(11, 60),
            Silent(12),
        };

        var events = MakeSegmenter().Segment(frames, new[] { 2, 8 });

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(60, e.Pitch.MidiNumber));
        Assert.Equal(2 * Hop, events[0].Onset.Samples);
        Assert.Equal(8 * Hop, events[1].Onset.Samples);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void SegmentationIsDeterministic()
    {
        var frames = new List<FrameObservation>
        {
            Silent(0), Silent(1),
            Voiced(2, 64), Voiced(3, 64), Voiced(4, 64),
            Silent(5),
            Voiced(6, 67), Voiced(7, 67), Voiced(8, 67),
            Silent(9),
        };
        var segmenter = MakeSegmenter();

        var first = segmenter.Segment(frames, new[] { 2, 6 });
        var second = segmenter.Segment(frames, new[] { 2, 6 });

        var projFirst = first.Select(e => (e.Pitch.MidiNumber, e.Onset.Samples, e.Duration.Samples, e.Velocity));
        var projSecond = second.Select(e => (e.Pitch.MidiNumber, e.Onset.Samples, e.Duration.Samples, e.Velocity));
        Assert.Equal(projFirst, projSecond);
    }
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~NoteSegmenterTests.BackToBackSamePitchNotesAreSplitByTheSecondOnset"
```

Expected FAILURE: `Assert.Equal() Failure: Expected 2560, Actual 5120` — without next-onset bounding the first note runs to the end of the buffer and swallows the second note's frames. (`RepeatedSamePitchNotesSeparatedByRestAreDistinctEvents` and `SegmentationIsDeterministic` already pass — the rest supplies the split there, and the algorithm is already deterministic — but they lock in the behavior.)

**Step 3 — Minimal implementation:** replace the `foreach` loop head in `NoteSegmenter.Segment` so each note is bounded by the next onset. Change:

```csharp
        foreach (int startFrame in onsetFrames)
        {
            (Pitch Pitch, int StableStart)? stable = FindStablePitch(frames, startFrame, n);
            if (stable is not { } found)
            {
                continue;   // spurious onset with no stable voiced pitch → drop
            }

            int endFrame = FindEndFrame(frames, found.StableStart, n, found.Pitch);
```

to:

```csharp
        for (int i = 0; i < onsetFrames.Count; i++)
        {
            int startFrame = onsetFrames[i];
            int limit = i + 1 < onsetFrames.Count ? onsetFrames[i + 1] : n;

            (Pitch Pitch, int StableStart)? stable = FindStablePitch(frames, startFrame, limit);
            if (stable is not { } found)
            {
                continue;   // spurious onset with no stable voiced pitch → drop
            }

            int endFrame = FindEndFrame(frames, found.StableStart, limit, found.Pitch);
```

(The `FindStablePitch`/`FindEndFrame` signatures already take a `limit`; only the call sites and loop change. The rest of the loop body is unchanged.)

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~NoteSegmenterTests"
```

Expected PASS: 6 tests green.

**Step 5 — Commit:**

```bash
but status -fv
but commit step-05-onset-segmentation -m "feat(domain): split repeated same-pitch notes by onset" --changes <ids> --status-after
```

---

## Task 7: decay-below-floor end condition (R5.2, third termination)

R5.2 lists three ways a note ends; Tasks 4 and 6 cover "next onset" and "transition to unvoiced". The third is decay: a note whose level falls below a fraction of its own peak has ended even if YIN still reports a (quiet) voiced pitch. This is opt-in via `DecayFloorRatio` (0 disables it, as in every prior test). Because the default is `0` (disabled), the shipped default configuration never exercises this path; enabling it with a sensible ratio is a follow-up obligation on the Step 9/Step 10 composition root (recorded in the R5.2 coverage row), not something Step 5 turns on by default.

**Files:**
- Modify: `src/AudioClaudio.Domain/NoteSegmenter.cs` (make `FindEndFrame` an instance method; track peak level and cut on decay)
- Test: `tests/AudioClaudio.Tests/Domain/NoteSegmenterTests.cs` (add one test)

**Step 1 — Write the failing test:**

```csharp
    [Fact]
    [Trait("Category", "Fast")]
    public void NoteEndsWhenLevelDecaysBelowFloor()
    {
        // Stays voiced on C4 throughout, but the level decays; floor = 25% of peak.
        var frames = new List<FrameObservation>
        {
            Silent(0), Silent(1),
            Voiced(2, 60, 1.0), Voiced(3, 60, 0.8), Voiced(4, 60, 0.5),
            Voiced(5, 60, 0.2),                        // 0.2 < 0.25 * peak(1.0) → note ends here
            Voiced(6, 60, 0.15), Voiced(7, 60, 0.1),
            Silent(8),
        };

        var events = MakeSegmenter(decayFloor: 0.25).Segment(frames, new[] { 2 });

        var e = Assert.Single(events);
        Assert.Equal(60, e.Pitch.MidiNumber);
        Assert.Equal((5 - 2) * Hop, e.Duration.Samples);
    }
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~NoteSegmenterTests.NoteEndsWhenLevelDecaysBelowFloor"
```

Expected FAILURE: `Assert.Equal() Failure: Expected 1536, Actual 3072` — with no decay cut the note runs to the unvoiced frame at index 8.

**Step 3 — Minimal implementation:** replace `FindEndFrame` in `NoteSegmenter.cs` (drop `static`, add peak tracking and the decay cut):

```csharp
    /// <summary>
    /// Returns the first frame in [stableStart, limit) at which the note terminates —
    /// a transition to unvoiced, a change of pitch, or (when DecayFloorRatio &gt; 0) a
    /// level below that fraction of the note's running peak — or <paramref name="limit"/>
    /// if it never terminates within the window.
    /// </summary>
    private int FindEndFrame(
        IReadOnlyList<FrameObservation> frames, int stableStart, int limit, Pitch pitch)
    {
        double peak = 0.0;

        for (int j = stableStart; j < limit; j++)
        {
            FrameObservation f = frames[j];
            if (f.Pitch is not { } p || p.MidiNumber != pitch.MidiNumber)
            {
                return j;   // transition to unvoiced or a different pitch
            }

            if (f.Energy > peak)
            {
                peak = f.Energy;
            }

            if (_options.DecayFloorRatio > 0.0 && peak > 0.0 &&
                f.Energy < peak * _options.DecayFloorRatio)
            {
                return j;   // decayed below the amplitude floor (R5.2)
            }
        }

        return limit;
    }
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~NoteSegmenterTests"
```

Expected PASS: 7 tests green (decay cut only fires when `DecayFloorRatio > 0`, so every earlier test is unaffected).

**Step 5 — Commit:**

```bash
but status -fv
but commit step-05-onset-segmentation -m "feat(domain): decay-below-floor note termination" --changes <ids> --status-after
```

---

## Task 8: headline golden + count property (the Step 5 *Verify*)

These exercise `OnsetDetector.Detect` and `NoteSegmenter.Segment` together on constructed note tracks — the deterministic Domain analogue of the spec's "generated sequence of five known notes with silences". (The full audio path — signal generator → Step 3 spectra → Step 4 YIN → here — is the closed loop of **Step 9**; Step 5's acceptance stays on the pure components.) A shared `BuildTrack` helper renders a list of notes to parallel magnitude spectra (silence between notes, so each attack is a clean flux spike) and `FrameObservation`s.

**Files:**
- Test: `tests/AudioClaudio.Tests/Domain/OnsetSegmentationGoldenTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public sealed class OnsetSegmentationGoldenTests
{
    private static readonly SampleRate Rate = new(44100);
    private const long Hop = 512;

    // Renders notes to parallel spectra + observations: `leadFrames` of leading silence,
    // then each note as `Voiced` frames followed by `restFrames` of silence. A note is a
    // constant nonzero magnitude pattern, so silence→note is one flux spike and the sustain
    // is flat (zero flux). The rest guarantees the next note spikes again.
    private static (List<IReadOnlyList<double>> Spectra, List<FrameObservation> Observations)
        BuildTrack((int Midi, int Voiced)[] notes, int restFrames, int leadFrames)
    {
        var spectra = new List<IReadOnlyList<double>>();
        var obs = new List<FrameObservation>();

        void AddSilence(int count)
        {
            for (int i = 0; i < count; i++)
            {
                int idx = spectra.Count;
                spectra.Add(new double[] { 0, 0, 0, 0 });
                obs.Add(new FrameObservation(new SamplePosition(idx * Hop, Rate), null, 0.0));
            }
        }

        AddSilence(leadFrames);
        foreach ((int midi, int voiced) in notes)
        {
            var pattern = new double[] { 1.0, 0.5, 0.25, 0.1 };
            for (int i = 0; i < voiced; i++)
            {
                int idx = spectra.Count;
                spectra.Add(pattern);
                obs.Add(new FrameObservation(new SamplePosition(idx * Hop, Rate), new Pitch(midi), 1.0));
            }
            AddSilence(restFrames);
        }

        return (spectra, obs);
    }

    private static NoteSegmenter Segmenter() => new(new NoteSegmenterOptions
    {
        MinNoteDuration = new SampleDuration(Hop, Rate),   // 1 hop minimum
        StabilityFrames = 2,
        Velocity = 64,
    });

    [Fact]
    [Trait("Category", "Fast")]
    public void FiveNotesWithSilencesYieldExactlyFiveEventsWithAccurateOnsets()
    {
        var notes = new (int Midi, int Voiced)[]
        {
            (60, 6), (62, 6), (64, 6), (65, 6), (67, 6),
        };
        var (spectra, obs) = BuildTrack(notes, restFrames: 3, leadFrames: 2);

        var onsetFrames = new OnsetDetector().Detect(spectra);
        var events = Segmenter().Segment(obs, onsetFrames);

        Assert.Equal(5, events.Count);
        int[] truthOnsetFrames = { 2, 11, 20, 29, 38 };
        for (int i = 0; i < notes.Length; i++)
        {
            Assert.Equal(notes[i].Midi, events[i].Pitch.MidiNumber);
            long truth = truthOnsetFrames[i] * Hop;
            Assert.True(
                Math.Abs(events[i].Onset.Samples - truth) <= Hop,
                $"note {i} onset off by more than one hop");
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void EventCountEqualsTrueNoteCountForGappedSequences()
    {
        Gen<(int Midi, int Voiced)> genNote =
            Gen.Select(Gen.Int[40, 80], Gen.Int[4, 10], (midi, voiced) => (Midi: midi, Voiced: voiced));

        genNote.Array[2, 6].Sample(
            notes =>
            {
                var (spectra, obs) = BuildTrack(notes, restFrames: 3, leadFrames: 2);
                var onsetFrames = new OnsetDetector().Detect(spectra);
                var events = Segmenter().Segment(obs, onsetFrames);

                return events.Count == notes.Length
                    && Enumerable.Range(0, notes.Length)
                        .All(i => events[i].Pitch.MidiNumber == notes[i].Midi);
            },
            iter: 200);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~OnsetSegmentationGoldenTests"
```

Expected outcome: with Tasks 1–7 complete these are the acceptance tests and should pass on the first run. If either is RED, treat the failure as the ground truth (Section 1 rule 8) and apply @superpowers:systematic-debugging — the likely lever is an `OnsetDetectorOptions` default (`ThresholdMultiplier`/`ThresholdDelta`/`MinGapFrames`) that lets an attack double-trigger or drops a real onset. Do not weaken an assertion to make it pass. To reproduce a specific CsCheck counter-example, re-run with the seed it prints: `...Sample(predicate, seed: "<printed>", iter: 200)`; keep that pinned seed while debugging (Section 5: fixed seeds).

**Step 3 — Minimal implementation:** none — this task adds only tests over existing components.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~OnsetSegmentationGoldenTests"
dotnet test --filter Category=Fast
dotnet test
```

Expected PASS: both new tests green; the full Fast suite green; the full suite (including the Slow property) green.

**Step 5 — Commit** (this is the step's roll-up commit — use the spec message):

```bash
dotnet format
but status -fv
but commit step-05-onset-segmentation -m "feat(domain): onset detection and note segmentation" --changes <ids> --status-after
```

---

## Verify (step exit criteria)

Restating Section 6 Step 5's *Verify* for this step:

- [ ] **Golden.** A generated sequence of five known notes with silences yields exactly five events with onsets within ±1 hop of truth — `OnsetSegmentationGoldenTests.FiveNotesWithSilencesYieldExactlyFiveEventsWithAccurateOnsets` (Task 8).
- [ ] **Property.** For generated note sequences with inter-onset gaps above the minimum duration, the event count equals the true note count (no merges, no splits) — `OnsetSegmentationGoldenTests.EventCountEqualsTrueNoteCountForGappedSequences` (Task 8).
- [ ] **Example.** Repeated same-pitch notes come back as distinct events — `NoteSegmenterTests.RepeatedSamePitchNotesSeparatedByRestAreDistinctEvents` and `...BackToBackSamePitchNotesAreSplitByTheSecondOnset` (Task 6).

## Definition of Done

- [ ] `dotnet build` succeeds with warnings-as-errors (the dependency rule and analyzers bite).
- [ ] `dotnet format` reports no changes.
- [ ] All new tests green; `dotnet test --filter Category=Fast` green; full `dotnet test` green (Fast + the one Slow property).
- [ ] Dependency rule intact: every new type lives in `AudioClaudio.Domain` and uses only the BCL — no audio-device type, no MIDI library, no `DateTime` (§3, non-negotiable 2). Verify `AudioClaudio.Domain.csproj` still references nothing beyond the BCL.
- [ ] Non-negotiables asserted where touched: integer sample time carried with its rate (all duration/position arithmetic), pitch decisions in MIDI space, determinism (`SegmentationIsDeterministic`).
- [ ] Requirement-coverage table fully satisfied: R5.1, R5.2, R5.3, R5.4 each proven by the listed tests.
- [ ] Committed via the **gitbutler** skill; the step roll-up carries the spec message `feat(domain): onset detection and note segmentation`.
- [ ] `DECISIONS.md`: no update required — Step 5 has no *Design decision* gate and adds no NuGet package. (The internal peak-picking method — normalized spectral flux with an adaptive-mean threshold — is an implementation choice made under Section 1 rule 4, not a Cornelius-owned fork.)
- [ ] Before claiming done, run @superpowers:verification-before-completion and paste the passing `dotnet test` output.
