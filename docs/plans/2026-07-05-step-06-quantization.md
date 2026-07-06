# Step 6 — Quantization to Score — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Build-spec step:** Section 6 Step 6 (R6.1, R6.2, R6.3, R6.4)
**Goal:** Turn a list of continuous-time `NoteEvent`s into a `Score` on a tempo grid — snapping onsets to grid lines and durations to standard note values — as a pure, idempotent, non-mutating function.
**Architecture:** Pure Domain work only (`AudioClaudio.Domain`). Quantization is an algorithm over `NoteEvent`s, not I/O, so it needs no port and no Application/Infrastructure changes. It consumes the Step 1 primitives (`Pitch`, `SampleRate`, `SamplePosition`, `SampleDuration`, `NoteEvent`) and produces new `Score`/`Measure`/`ScoreElement` value types. The dependency rule is untouched: Domain still references nothing but the BCL.
**Tech Stack:** C# / .NET 10, xUnit (Apache-2.0), CsCheck (MIT). No new NuGet packages.
**Prerequisites:** Per §1 rule 3, Steps 0–5 must be green and committed before Step 6 begins. Note that Step 6's *code* depends only on the Step 1 domain primitives — it operates on `NoteEvent` lists, not on the detector/onset pipeline (Steps 2–5). If Step 1's type names differ from those assumed here (`new SampleRate(48000)` with `.Hz`; `new Pitch(60)` with `.MidiNumber`; `new SamplePosition(long, SampleRate)`; `new SampleDuration(long, SampleRate)`; `new NoteEvent(Pitch, SamplePosition, SampleDuration, int)`), adjust the calls to match the real signatures.
**Commit (spec):** `feat(domain): grid quantization to Score`

---

## DECISION GATE (Cornelius owns this)

**R6.1 says "ties beyond MVP." This plan must be explicit about the one thing it
does not build and the different thing it does — because the word "tie" covers
both, and only one is out of scope.** Per §1 rules 2 and 6, an implementation
choice that touches spec-excluded territory is surfaced here for Cornelius, not
silently built in.

R6.1 reads: "snap each duration to the nearest standard value (whole through
sixteenth, with dotted values; **ties beyond MVP**)." That exclusion is about
**notation spelling** — representing *one note's* snapped duration as a chain of
tied standard note values (writing, say, a 5-tick note as an eighth tied to a
sixteenth). **This plan does not do that.** A `ScoreElement` stores a single
integer `LengthTicks`; spelling an awkward run as tied glyphs is deferred to the
Step 11 notation writer (R11.1). This is scope call #1 in *Approach* below.

The `TiedToNext` flag this plan *does* introduce is a **different** mechanism:
**structural bar-splitting.** When a note's quantized span crosses a barline it is
cut at the barline into per-measure segments, so that (a) every measure's element
lengths sum to exactly one bar — the bar-conservation invariant (R6.4) — and
(b) Step 11 can notate the barline-crossing note as tied across the bar, which is
the only correct way to write it. The earlier segment carries `TiedToNext = true`;
that is a positional fact about where the barline falls, not a duration-spelling
choice. Structural bar-splitting is **not optional** once a note may cross a
barline and bar-conservation must hold.

`TiedToNext` is also fixed by the cross-step contract (`CONTRACTS.md` §6) — Step 11's
MusicXML writer reads it — so it **is not removed** under any option here.

**Decision required — pick one, then record it in `DECISIONS.md` before Task 8
(barline splitting) is implemented:**

1. **(Default, implemented below) Keep the structural cross-barline split.** A note
   crossing a barline is split into tied per-measure segments; bar-conservation
   holds for arbitrary input; Step 11 renders the tie. `TiedToNext` is populated as
   needed.
2. **Additionally constrain the closed-loop corpus so no note ever crosses a
   barline.** Step 9's generator already keeps notes on-grid with rests between
   them; if it also guarantees no note spans a barline, the split path is never
   exercised in the closed loop and `TiedToNext` is always `false` on generated
   input. The field and the split code remain (contract-fixed) as a determinism
   guard for arbitrary/live input.

This gate asks only that Cornelius confirm the structural-split behaviour (option 1)
or add the corpus constraint (option 2). It does not change the type shapes fixed by
`CONTRACTS.md`.

---

## Approach

Quantization is the step where "what was played" (continuous sample positions) becomes "what was meant" (a grid of notes and rests). The mathematics is small and worth stating before any code.

**From tempo to a sample grid.** In 4/4, a *beat* is a quarter note, and BPM counts beats per minute. So one beat lasts `60 / bpm` seconds, which at a sample rate of `sr` Hz is `samplesPerBeat = 60/bpm * sr` samples. A *subdivision* (the grid resolution, e.g. sixteenths) chops each beat into equal *ticks*: a sixteenth grid has 4 ticks per quarter-note beat, so `samplesPerTick = samplesPerBeat / 4`.

**The integer-samples tension, resolved.** Non-negotiable 1 says time is integer samples. But `samplesPerTick` is often fractional — at 120 BPM / 44.1 kHz it is exactly 5512.5 samples. We honour the non-negotiable by keeping *ticks* as the integer currency of the score and confining the fractional `samplesPerTick` to a single conversion at the boundary (`round(samples / samplesPerTick)`). The fraction is never accumulated; each onset is converted once, independently.

**Snapping onsets.** An onset at sample position `s` maps to grid tick `round(s / samplesPerTick)` — the nearest grid line. Rounding is `MidpointRounding.AwayFromZero` so the rule is deterministic (non-negotiable 3).

**Snapping durations to standard values.** Durations do not snap to *any* tick count; R6.1 says they snap to the nearest *standard note value* — whole, half, quarter, eighth, sixteenth, and their dotted variants. Measured in ticks on a sixteenth grid these are `{16, 12, 8, 6, 4, 3, 2, 1}` (whole, dotted-half, half, dotted-quarter, quarter, dotted-eighth, eighth, sixteenth). A value is only *representable* on a grid when its tick length is an integer, so an eighth-note grid drops the sixteenth (0.5 ticks) and the dotted-eighth (1.5 ticks). Snapping a raw duration picks the representable standard value nearest in ticks; ties break toward the *shorter* value, deterministically.

**From notes to measures.** A `Score` is measures of notes *and rests*. We lay the snapped notes on a tick timeline, fill every gap (before the first note, between notes, after the last) with rests, pad the tail out to a whole number of measures, then cut the timeline at each barline (`ticksPerMeasure = beats × ticksPerBeat`, i.e. 16 on a 4/4 sixteenth grid). A note or rest that crosses a barline is split into per-measure segments; note segments before the last carry a `TiedToNext` flag. This guarantees the *bar-conservation* invariant — every measure's element lengths sum to exactly one bar — which Step 11's MusicXML writer later relies on.

**Two deliberate scope calls.** (Step 6 lists no formal *Design decision*, but the
cross-barline *tie* question is surfaced separately in the DECISION GATE above —
R6.1's "ties beyond MVP" wording earns Cornelius's explicit confirmation. The two
narrower engineering choices below are recorded in this plan.)

1. **Tick runs, not notation spelling.** A `ScoreElement` stores an integer `LengthTicks`. A cross-barline split (or an odd remainder) can leave a run whose length is not itself a single standard value (e.g. 14 ticks). Spelling such a run as a chain of tied standard note values / dotted rests is a *notation* concern and belongs to Step 11 (R11.1). Step 6 guarantees the ticks are correct and conserved; Step 11 decides how they are written. This resolves the mild overlap between R6.4 ("the Score carries measures") and R11.1 ("measures barred by the time signature"): Step 6 owns the barring and the arithmetic; Step 11 owns the glyphs.
2. **Monophonic overlap truncation.** After snapping, two originally-separate notes can collide (a note snapped to a quarter can reach into the next note's onset). Since the MVP is monophonic, at most one note may sound at a time, so we clip each note to end no later than the next note's onset. On an exact same-tick collision the earlier note collapses to zero length and is dropped. Step 9's constrained corpus keeps ≥ 1 grid rest between notes, so truncation never fires there — it is purely a determinism guard for arbitrary input.

Code lives in the tasks below; each task is one red→green TDD loop (@superpowers:test-driven-development).

---

## Requirements coverage

| Requirement | Task(s) | Proven by (test) |
|---|---|---|
| **R6.1** Snap onsets to nearest grid line; durations to nearest standard value (whole…sixteenth, dotted) | Task 4 (onset snap), Task 5 (duration snap) | `SamplesToTick_snaps_onset_40ms_late_to_intended_grid_line`; `NearestStandardValueTicks_snaps_to_standard_values`; `StandardValueTicks_include_dotted_and_exclude_unrepresentable` |
| **R6.2** Pure `[NoteEvent] -> Score`; raw events never mutated | Task 7 (returns new Score), Task 9 (input unchanged, deterministic) | `Quantize_reproduces_on_grid_events_exactly`; `Quantize_does_not_mutate_input`; `Quantize_is_deterministic` |
| **R6.3** Tempo is a declared input, never estimated | Task 8b | `Quantize_honours_declared_tempo_same_events_two_tempos_differ` |
| **R6.4** `Score` carries tempo, 4/4 time signature, measures of quantized notes and rests | Task 6 (types), Task 7 (single measure, notes+rests), Task 8 (multi-measure, bar conservation) | `Score_carries_tempo_timesignature_and_measures`; `Quantize_fills_gaps_with_rests`; `Quantize_splits_note_across_barline_with_tie`; `Quantize_measures_each_sum_to_one_bar` |

Non-negotiables asserted in these tests: integer-tick time with carried sample rate and rejection of mismatched rates (Task 7, `Quantize_rejects_sample_rate_mismatch`); determinism (Task 9).

---

## Task 1: `Tempo` value type

**Files:**
- Create: `src/AudioClaudio.Domain/Tempo.cs`
- Test: `tests/AudioClaudio.Tests/Domain/TempoTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class TempoTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Tempo_stores_beats_per_minute()
    {
        var tempo = new Tempo(120.0);
        Assert.Equal(120.0, tempo.BeatsPerMinute);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Tempo_rejects_non_positive_or_non_finite_bpm(double bpm)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Tempo(bpm));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Tempo_has_value_equality()
    {
        Assert.Equal(new Tempo(96.0), new Tempo(96.0));
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Domain.TempoTests"
```

Expected FAILURE: compile error — `Tempo` does not exist in `AudioClaudio.Domain`.

**Step 3 — Minimal implementation:**

```csharp
using System;

namespace AudioClaudio.Domain;

/// <summary>
/// Musical tempo in beats per minute. In 4/4 the beat is a quarter note.
/// A positive, finite BPM; the value type carries no clock and no I/O (R1.5, R6.5-style purity).
/// </summary>
public readonly record struct Tempo
{
    /// <summary>Beats (quarter notes in 4/4) per minute.</summary>
    public double BeatsPerMinute { get; }

    public Tempo(double beatsPerMinute)
    {
        if (!(beatsPerMinute > 0) || double.IsInfinity(beatsPerMinute))
        {
            throw new ArgumentOutOfRangeException(
                nameof(beatsPerMinute), beatsPerMinute, "Tempo must be a positive, finite BPM.");
        }

        BeatsPerMinute = beatsPerMinute;
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Domain.TempoTests"
```

Expected PASS: 5 tests green (1 + 4 theory cases).

**Step 5 — Commit** (via the @gitbutler skill — create the step branch first, then commit; get IDs from `but status -fv`):

```bash
but branch new step-06-quantization && but mark step-06-quantization
but status -fv
but commit step-06-quantization -m "feat(domain): Tempo value type" --changes <ids from but status -fv> --status-after
```

---

## Task 2: `TimeSignature` value type

**Files:**
- Create: `src/AudioClaudio.Domain/TimeSignature.cs`
- Test: `tests/AudioClaudio.Tests/Domain/TimeSignatureTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class TimeSignatureTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void FourFour_is_four_over_four()
    {
        var ts = TimeSignature.FourFour;
        Assert.Equal(4, ts.BeatsPerMeasure);
        Assert.Equal(4, ts.BeatUnit);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Rejects_non_positive_numerator()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeSignature(0, 4));
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(3)]  // not a power of two
    [InlineData(0)]
    [InlineData(-4)]
    public void Rejects_denominator_that_is_not_a_positive_power_of_two(int denominator)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeSignature(4, denominator));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Has_value_equality()
    {
        Assert.Equal(new TimeSignature(4, 4), TimeSignature.FourFour);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Domain.TimeSignatureTests"
```

Expected FAILURE: compile error — `TimeSignature` does not exist.

**Step 3 — Minimal implementation:**

```csharp
using System;

namespace AudioClaudio.Domain;

/// <summary>
/// A time signature such as 4/4. The MVP uses 4/4 only; the type is written a
/// little more generally (any positive numerator, any power-of-two denominator)
/// so the grid math has a single honest source for beats-per-measure.
/// </summary>
public readonly record struct TimeSignature
{
    /// <summary>Numerator — beats in one measure.</summary>
    public int BeatsPerMeasure { get; }

    /// <summary>Denominator — the note value that gets one beat (4 = quarter).</summary>
    public int BeatUnit { get; }

    public TimeSignature(int beatsPerMeasure, int beatUnit)
    {
        if (beatsPerMeasure <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(beatsPerMeasure), beatsPerMeasure, "Numerator (beats per measure) must be positive.");
        }

        if (beatUnit <= 0 || (beatUnit & (beatUnit - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(beatUnit), beatUnit, "Denominator (beat unit) must be a positive power of two.");
        }

        BeatsPerMeasure = beatsPerMeasure;
        BeatUnit = beatUnit;
    }

    /// <summary>The MVP time signature, 4/4.</summary>
    public static TimeSignature FourFour => new(4, 4);
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Domain.TimeSignatureTests"
```

Expected PASS: all cases green.

**Step 5 — Commit** (@gitbutler):

```bash
but status -fv
but commit step-06-quantization -m "feat(domain): TimeSignature value type" --changes <ids from but status -fv> --status-after
```

---

## Task 3: `Subdivision` and `QuantizationGrid` grid math

This task adds the grid resolution enum and the object that owns *all* the sample↔tick arithmetic, so the numbers are declared once (the R2.4 discipline).

**Files:**
- Create: `src/AudioClaudio.Domain/Subdivision.cs`
- Create: `src/AudioClaudio.Domain/QuantizationGrid.cs`
- Test: `tests/AudioClaudio.Tests/Domain/QuantizationGridTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class QuantizationGridTests
{
    private static QuantizationGrid Grid(int sr, double bpm, Subdivision sub) =>
        new(new SampleRate(sr), new Tempo(bpm), TimeSignature.FourFour, sub);

    [Fact]
    [Trait("Category", "Fast")]
    public void Sixteenth_grid_has_four_ticks_per_beat_and_sixteen_per_measure()
    {
        var grid = Grid(48000, 120, Subdivision.Sixteenth);
        Assert.Equal(4, grid.TicksPerBeat);
        Assert.Equal(16, grid.TicksPerMeasure);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Eighth_grid_has_two_ticks_per_beat_and_eight_per_measure()
    {
        var grid = Grid(48000, 120, Subdivision.Eighth);
        Assert.Equal(2, grid.TicksPerBeat);
        Assert.Equal(8, grid.TicksPerMeasure);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Samples_per_tick_is_a_fractional_boundary_value_not_rounded_away()
    {
        // 120 BPM at 44.1 kHz: a quarter = 22050 samples, a sixteenth = 5512.5.
        var grid = Grid(44100, 120, Subdivision.Sixteenth);
        Assert.Equal(22050.0, grid.SamplesPerBeat, 6);
        Assert.Equal(5512.5, grid.SamplesPerTick, 6);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Samples_per_tick_is_integer_at_48k_120bpm_sixteenth()
    {
        var grid = Grid(48000, 120, Subdivision.Sixteenth);
        Assert.Equal(6000.0, grid.SamplesPerTick, 6);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Domain.QuantizationGridTests"
```

Expected FAILURE: compile error — `Subdivision` and `QuantizationGrid` do not exist.

**Step 3 — Minimal implementation** (two files):

`src/AudioClaudio.Domain/Subdivision.cs`:

```csharp
using System;

namespace AudioClaudio.Domain;

/// <summary>The quantization grid resolution: the note value one grid tick represents.</summary>
public enum Subdivision
{
    Quarter,
    Eighth,
    Sixteenth,
}

public static class SubdivisionExtensions
{
    /// <summary>How many grid ticks fill one quarter note (Quarter=1, Eighth=2, Sixteenth=4).</summary>
    public static int TicksPerQuarter(this Subdivision subdivision) => subdivision switch
    {
        Subdivision.Quarter => 1,
        Subdivision.Eighth => 2,
        Subdivision.Sixteenth => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(subdivision), subdivision, "Unknown subdivision."),
    };
}
```

`src/AudioClaudio.Domain/QuantizationGrid.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace AudioClaudio.Domain;

/// <summary>
/// The fixed grid a performance is quantized onto: sample rate, tempo, time
/// signature, and subdivision. All grid math lives here so the numbers are
/// declared once, never scattered (the R2.4 discipline).
///
/// Time in a <see cref="Score"/> is integer ticks (a tick == one subdivision
/// unit). <see cref="SamplesPerTick"/> may be fractional (e.g. 5512.5 at 120 BPM
/// / 44.1 kHz); that fraction lives only inside the sample↔tick conversion and is
/// never accumulated (non-negotiable 1: never accumulate floating time).
/// The 4/4-with-quarter-beat mapping is assumed (the MVP time signature).
/// </summary>
public readonly record struct QuantizationGrid
{
    public SampleRate SampleRate { get; }
    public Tempo Tempo { get; }
    public TimeSignature TimeSignature { get; }
    public Subdivision Subdivision { get; }

    public QuantizationGrid(
        SampleRate sampleRate, Tempo tempo, TimeSignature timeSignature, Subdivision subdivision)
    {
        SampleRate = sampleRate;
        Tempo = tempo;
        TimeSignature = timeSignature;
        Subdivision = subdivision;
    }

    /// <summary>Grid ticks per beat (the beat is the time-signature denominator note).</summary>
    public int TicksPerBeat => Subdivision.TicksPerQuarter() * 4 / TimeSignature.BeatUnit;

    /// <summary>Grid ticks in one full measure.</summary>
    public int TicksPerMeasure => TimeSignature.BeatsPerMeasure * TicksPerBeat;

    /// <summary>Samples per beat (may be fractional).</summary>
    public double SamplesPerBeat => 60.0 / Tempo.BeatsPerMinute * SampleRate.Hz;

    /// <summary>Samples per grid tick (may be fractional; confined to conversions).</summary>
    public double SamplesPerTick => SamplesPerBeat / TicksPerBeat;
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Domain.QuantizationGridTests"
```

Expected PASS: all four grid-math tests green.

**Step 5 — Commit** (@gitbutler):

```bash
but status -fv
but commit step-06-quantization -m "feat(domain): QuantizationGrid and Subdivision grid math" --changes <ids from but status -fv> --status-after
```

---

## Task 4: Onset-to-tick snapping (R6.1, onset half)

**Files:**
- Modify: `src/AudioClaudio.Domain/QuantizationGrid.cs` (add `SamplesToTick`)
- Test: `tests/AudioClaudio.Tests/Domain/QuantizationGridTests.cs` (add cases)

**Step 1 — Write the failing test** (append to `QuantizationGridTests`):

```csharp
    [Fact]
    [Trait("Category", "Fast")]
    public void SamplesToTick_snaps_on_grid_sample_to_its_tick()
    {
        // 120 BPM, 44.1 kHz, sixteenth grid: tick 4 sits at 4 * 5512.5 = 22050 samples.
        var grid = Grid(44100, 120, Subdivision.Sixteenth);
        Assert.Equal(4L, grid.SamplesToTick(22050));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void SamplesToTick_snaps_onset_40ms_late_to_intended_grid_line()
    {
        // The spec example: an onset 40 ms late at 120 BPM sixteenths snaps to its grid line.
        // 40 ms at 44.1 kHz = 1764 samples; intended line is tick 4 (22050 samples).
        var grid = Grid(44100, 120, Subdivision.Sixteenth);
        long lateOnset = 22050 + 1764; // 23814
        Assert.Equal(4L, grid.SamplesToTick(lateOnset));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void SamplesToTick_rounds_half_away_from_zero_deterministically()
    {
        // To pin the midpoint tie-break we need an exact half-tick sample position,
        // which requires an integer SamplesPerTick. 48 kHz / 120 BPM / sixteenth gives
        // SamplesPerTick = 6000: tick 4 = 24000, tick 5 = 30000, so the midpoint tick 4.5
        // is 27000 samples exactly. Half-away-from-zero rounds 4.5 up to 5; one sample
        // below the midpoint (26999 -> 4.4998...) stays at 4 — the two together pin the rule.
        // (The old 44.1 kHz case used SamplesPerTick = 5512.5, whose tick-4.5 boundary is
        // 24806.25 — not an integer sample count, so 24806/5512.5 = 4.4999... rounds to 4,
        // not 5, and the assertion was wrong.)
        var grid = Grid(48000, 120, Subdivision.Sixteenth);
        Assert.Equal(5L, grid.SamplesToTick(27000)); // exactly tick 4.5 -> rounds up to 5
        Assert.Equal(4L, grid.SamplesToTick(26999)); // just below the midpoint -> 4
    }
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Domain.QuantizationGridTests.SamplesToTick"
```

Expected FAILURE: compile error — `SamplesToTick` is not defined on `QuantizationGrid`.

**Step 3 — Minimal implementation** (add to `QuantizationGrid`):

```csharp
    /// <summary>
    /// Snap an absolute sample position to the nearest grid tick index.
    /// Rounding is half-away-from-zero so the rule is deterministic (non-negotiable 3).
    /// </summary>
    public long SamplesToTick(long samples) =>
        (long)Math.Round(samples / SamplesPerTick, MidpointRounding.AwayFromZero);
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Domain.QuantizationGridTests"
```

Expected PASS: all grid tests including the three new onset-snap cases.

**Step 5 — Commit** (@gitbutler):

```bash
but status -fv
but commit step-06-quantization -m "feat(domain): onset-to-tick snapping" --changes <ids from but status -fv> --status-after
```

---

## Task 5: Standard note-value duration snapping (R6.1, duration half)

**Files:**
- Modify: `src/AudioClaudio.Domain/QuantizationGrid.cs` (add `StandardValueTicks`, `NearestStandardValueTicks`)
- Test: `tests/AudioClaudio.Tests/Domain/QuantizationGridTests.cs` (add cases)

**Step 1 — Write the failing test** (append to `QuantizationGridTests`):

```csharp
    [Fact]
    [Trait("Category", "Fast")]
    public void StandardValueTicks_include_dotted_on_a_sixteenth_grid()
    {
        // whole=16, dotted-half=12, half=8, dotted-quarter=6, quarter=4,
        // dotted-eighth=3, eighth=2, sixteenth=1  -> ascending.
        var grid = Grid(48000, 120, Subdivision.Sixteenth);
        Assert.Equal(new[] { 1, 2, 3, 4, 6, 8, 12, 16 }, grid.StandardValueTicks);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void StandardValueTicks_exclude_values_unrepresentable_on_an_eighth_grid()
    {
        // On an eighth grid a sixteenth (0.5 ticks) and a dotted-eighth (1.5) are not integers.
        var grid = Grid(48000, 120, Subdivision.Eighth);
        Assert.Equal(new[] { 1, 2, 3, 4, 6, 8 }, grid.StandardValueTicks);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(4.0, 4)]   // exact quarter
    [InlineData(4.9, 4)]   // nearer 4 than 6
    [InlineData(3.0, 3)]   // dotted eighth is a standard value
    [InlineData(0.2, 1)]   // clamps up to the shortest value; a note cannot vanish
    [InlineData(100.0, 16)] // longer than a whole note snaps to a whole note
    public void NearestStandardValueTicks_snaps_to_standard_values(double rawTicks, int expected)
    {
        var grid = Grid(48000, 120, Subdivision.Sixteenth);
        Assert.Equal(expected, grid.NearestStandardValueTicks(rawTicks));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void NearestStandardValueTicks_breaks_ties_toward_the_shorter_value()
    {
        // 5.0 is equidistant from 4 and 6 -> shorter (4) wins; 7.0 from 6 and 8 -> 6 wins.
        var grid = Grid(48000, 120, Subdivision.Sixteenth);
        Assert.Equal(4, grid.NearestStandardValueTicks(5.0));
        Assert.Equal(6, grid.NearestStandardValueTicks(7.0));
    }
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Domain.QuantizationGridTests"
```

Expected FAILURE: compile error — `StandardValueTicks` / `NearestStandardValueTicks` not defined.

**Step 3 — Minimal implementation** (add to `QuantizationGrid`):

```csharp
    /// <summary>
    /// Tick lengths of the standard note values (whole … sixteenth, with dotted
    /// variants) that are exactly representable on this grid, ascending. A value
    /// is included only when its length is a whole number of ticks — a sixteenth
    /// is unrepresentable on an eighth-note grid, for example.
    /// </summary>
    public IReadOnlyList<int> StandardValueTicks
    {
        get
        {
            // Each standard value expressed as (numerator, denominator) quarter notes.
            // Dotted values are 1.5x their base. quarter == 1.
            (int Num, int Den)[] valuesInQuarters =
            {
                (4, 1), // whole
                (3, 1), // dotted half
                (2, 1), // half
                (3, 2), // dotted quarter
                (1, 1), // quarter
                (3, 4), // dotted eighth
                (1, 2), // eighth
                (1, 4), // sixteenth
            };

            int ticksPerQuarter = Subdivision.TicksPerQuarter();
            var result = new List<int>();
            foreach (var (num, den) in valuesInQuarters)
            {
                int numerator = ticksPerQuarter * num;
                if (numerator % den == 0)
                {
                    result.Add(numerator / den);
                }
            }

            result.Sort();
            return result;
        }
    }

    /// <summary>
    /// Snap a raw duration (in ticks, possibly fractional) to the nearest
    /// representable standard note value, returning its length in ticks. Ties break
    /// toward the shorter value (deterministic — non-negotiable 3). Never returns
    /// less than the shortest standard value, so a quantized note cannot vanish.
    /// </summary>
    public int NearestStandardValueTicks(double rawTicks)
    {
        IReadOnlyList<int> values = StandardValueTicks; // ascending
        int best = values[0];
        double bestDistance = Math.Abs(rawTicks - best);
        foreach (int value in values)
        {
            double distance = Math.Abs(rawTicks - value);
            if (distance < bestDistance)
            {
                best = value;
                bestDistance = distance;
            }
        }

        return best;
    }
```

Because `values` is ascending and we only replace on *strictly* smaller distance, the shortest of any equidistant pair is kept — the documented tie-break.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Domain.QuantizationGridTests"
```

Expected PASS: all grid tests including the standard-value and tie-break cases.

**Step 5 — Commit** (@gitbutler):

```bash
but status -fv
but commit step-06-quantization -m "feat(domain): standard note-value duration snapping" --changes <ids from but status -fv> --status-after
```

---

## Task 6: `Score`, `Measure`, `ScoreElement` model (R6.4, types)

The value types the quantizer produces. `ScoreElement` is a positional `readonly record struct` for clean structural equality; `Measure` and `Score` are sealed classes with element-wise `IEquatable` so tests can compare whole scores with `Assert.Equal`.

**Accepted limitation — the positional primary constructor is public.** The
cross-step contract (`CONTRACTS.md` §6) fixes `ScoreElement` as a positional record
`ScoreElement(ElementKind, Pitch?, int Velocity, int LengthTicks, bool TiedToNext)`
with `Note`/`Rest` factories, so the generated primary constructor is public and can
in principle bypass the `LengthTicks > 0` and velocity-range checks the factories
enforce (as with any `readonly record struct`, `default(ScoreElement)` is likewise
unforbiddable). The `Note`/`Rest` factories are the intended construction path and
are the only way `Quantizer` builds elements; this limitation is documented and
accepted rather than resolved by privatising the constructor, because doing so would
diverge from the contract shape other steps consume.

**Files:**
- Create: `src/AudioClaudio.Domain/ScoreElement.cs`
- Create: `src/AudioClaudio.Domain/Measure.cs`
- Create: `src/AudioClaudio.Domain/Score.cs`
- Test: `tests/AudioClaudio.Tests/Domain/ScoreModelTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class ScoreModelTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Note_element_carries_pitch_velocity_length_and_tie()
    {
        var note = ScoreElement.Note(new Pitch(60), velocity: 100, lengthTicks: 4, tiedToNext: true);
        Assert.Equal(ElementKind.Note, note.Kind);
        Assert.Equal(60, note.Pitch!.Value.MidiNumber);
        Assert.Equal(100, note.Velocity);
        Assert.Equal(4, note.LengthTicks);
        Assert.True(note.TiedToNext);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Rest_element_has_no_pitch_and_no_tie()
    {
        var rest = ScoreElement.Rest(2);
        Assert.Equal(ElementKind.Rest, rest.Kind);
        Assert.Null(rest.Pitch);
        Assert.Equal(2, rest.LengthTicks);
        Assert.False(rest.TiedToNext);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Elements_reject_non_positive_length()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScoreElement.Rest(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScoreElement.Note(new Pitch(60), 100, 0));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Measure_reports_total_ticks()
    {
        var measure = new Measure(new[] { ScoreElement.Note(new Pitch(60), 100, 4), ScoreElement.Rest(12) });
        Assert.Equal(16, measure.TotalTicks);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Score_carries_tempo_timesignature_and_measures_with_value_equality()
    {
        var a = new Score(new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth,
            new[] { new Measure(new[] { ScoreElement.Rest(16) }) });
        var b = new Score(new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth,
            new[] { new Measure(new[] { ScoreElement.Rest(16) }) });

        Assert.Equal(120.0, a.Tempo.BeatsPerMinute);
        Assert.Equal(TimeSignature.FourFour, a.TimeSignature);
        Assert.Single(a.Measures);
        Assert.Equal(a, b); // structural equality over tempo, signature, subdivision, measures
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Scores_differ_when_a_measure_element_differs()
    {
        var a = new Score(new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth,
            new[] { new Measure(new[] { ScoreElement.Rest(16) }) });
        var b = new Score(new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth,
            new[] { new Measure(new[] { ScoreElement.Note(new Pitch(60), 100, 16) }) });
        Assert.NotEqual(a, b);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Domain.ScoreModelTests"
```

Expected FAILURE: compile error — `ElementKind`, `ScoreElement`, `Measure`, `Score` do not exist.

**Step 3 — Minimal implementation** (three files):

`src/AudioClaudio.Domain/ScoreElement.cs`:

```csharp
using System;

namespace AudioClaudio.Domain;

/// <summary>Whether a <see cref="ScoreElement"/> is a sounding note or a rest.</summary>
public enum ElementKind
{
    Note,
    Rest,
}

/// <summary>
/// One note or rest within a measure, its length measured in grid ticks.
///
/// A note that crosses a barline is stored as several elements whose lengths sum
/// to the note's quantized duration; every element except the last of such a run
/// carries <see cref="TiedToNext"/> = true. Spelling a multi-tick run as tied
/// standard note values / dotted rests is the notation writer's job (Step 11);
/// this type only guarantees the ticks are correct.
/// </summary>
public readonly record struct ScoreElement(
    ElementKind Kind, Pitch? Pitch, int Velocity, int LengthTicks, bool TiedToNext)
{
    /// <summary>A sounding note of the given pitch, velocity and tick length.</summary>
    public static ScoreElement Note(Pitch pitch, int velocity, int lengthTicks, bool tiedToNext = false)
    {
        if (lengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthTicks), lengthTicks, "Length must be positive.");
        }

        if (velocity is < 0 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(velocity), velocity, "Velocity must be 0..127.");
        }

        return new ScoreElement(ElementKind.Note, pitch, velocity, lengthTicks, tiedToNext);
    }

    /// <summary>A rest of the given tick length.</summary>
    public static ScoreElement Rest(int lengthTicks)
    {
        if (lengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthTicks), lengthTicks, "Length must be positive.");
        }

        return new ScoreElement(ElementKind.Rest, null, 0, lengthTicks, false);
    }
}
```

`src/AudioClaudio.Domain/Measure.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace AudioClaudio.Domain;

/// <summary>One barred measure: an ordered list of notes and rests.</summary>
public sealed class Measure : IEquatable<Measure>
{
    public IReadOnlyList<ScoreElement> Elements { get; }

    public Measure(IReadOnlyList<ScoreElement> elements)
    {
        Elements = elements ?? throw new ArgumentNullException(nameof(elements));
    }

    /// <summary>Sum of the element tick lengths — one full bar when the measure is well-formed.</summary>
    public int TotalTicks
    {
        get
        {
            int total = 0;
            foreach (ScoreElement element in Elements)
            {
                total += element.LengthTicks;
            }

            return total;
        }
    }

    public bool Equals(Measure? other) => other is not null && Elements.SequenceEqual(other.Elements);

    public override bool Equals(object? obj) => Equals(obj as Measure);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (ScoreElement element in Elements)
        {
            hash.Add(element);
        }

        return hash.ToHashCode();
    }
}
```

`src/AudioClaudio.Domain/Score.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace AudioClaudio.Domain;

/// <summary>
/// A quantized performance: the tempo, time signature and grid subdivision it was
/// quantized against, plus its measures of notes and rests. The <see cref="Score"/>
/// is a derived view — the raw <see cref="NoteEvent"/> list it came from is never
/// mutated (R6.2). Tick lengths are interpreted against <see cref="Subdivision"/>.
/// </summary>
public sealed class Score : IEquatable<Score>
{
    public Tempo Tempo { get; }
    public TimeSignature TimeSignature { get; }
    public Subdivision Subdivision { get; }
    public IReadOnlyList<Measure> Measures { get; }

    public Score(Tempo tempo, TimeSignature timeSignature, Subdivision subdivision, IReadOnlyList<Measure> measures)
    {
        Tempo = tempo;
        TimeSignature = timeSignature;
        Subdivision = subdivision;
        Measures = measures ?? throw new ArgumentNullException(nameof(measures));
    }

    public bool Equals(Score? other) =>
        other is not null
        && Tempo.Equals(other.Tempo)
        && TimeSignature.Equals(other.TimeSignature)
        && Subdivision == other.Subdivision
        && Measures.SequenceEqual(other.Measures);

    public override bool Equals(object? obj) => Equals(obj as Score);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Tempo);
        hash.Add(TimeSignature);
        hash.Add(Subdivision);
        foreach (Measure measure in Measures)
        {
            hash.Add(measure);
        }

        return hash.ToHashCode();
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Domain.ScoreModelTests"
```

Expected PASS: all model tests green.

**Step 5 — Commit** (@gitbutler):

```bash
but status -fv
but commit step-06-quantization -m "feat(domain): Score, Measure, ScoreElement model" --changes <ids from but status -fv> --status-after
```

---

## Task 7: `Quantizer.Quantize` core — snap, rest-fill, single measure (R6.2, R6.4)

The first working `[NoteEvent] -> Score`: snap each event, order deterministically, resolve monophonic overlap, fill gaps with rests, pad the tail to a whole measure, and (for now) exercise the single-measure path. Cross-barline splitting is added in Task 8 but the same code handles it — this task's tests simply stay within one bar.

**Files:**
- Create: `src/AudioClaudio.Domain/Quantizer.cs`
- Test: `tests/AudioClaudio.Tests/Domain/QuantizerTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System;
using System.Linq;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class QuantizerTests
{
    private static readonly SampleRate Rate = new(48000);
    private static readonly QuantizationGrid Grid48 =
        new(Rate, new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth); // SamplesPerTick = 6000

    private static NoteEvent Event(int midi, long onsetTicks, int durationTicks, int velocity = 100) =>
        new(new Pitch(midi),
            new SamplePosition(onsetTicks * 6000, Rate),
            new SampleDuration(durationTicks * 6000, Rate),
            velocity);

    [Fact]
    [Trait("Category", "Fast")]
    public void Quantize_empty_events_yields_a_score_with_no_measures()
    {
        Score score = Quantizer.Quantize(Array.Empty<NoteEvent>(), Grid48);
        Assert.Empty(score.Measures);
        Assert.Equal(120.0, score.Tempo.BeatsPerMinute);
        Assert.Equal(TimeSignature.FourFour, score.TimeSignature);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Quantize_reproduces_on_grid_events_exactly_and_fills_gaps_with_rests()
    {
        // Quarter at tick 0, eighth at tick 6, inside one 16-tick measure.
        var events = new[] { Event(60, onsetTicks: 0, durationTicks: 4), Event(62, onsetTicks: 6, durationTicks: 2) };

        Score score = Quantizer.Quantize(events, Grid48);

        var expected = new Score(Grid48.Tempo, Grid48.TimeSignature, Grid48.Subdivision, new[]
        {
            new Measure(new[]
            {
                ScoreElement.Note(new Pitch(60), 100, 4),
                ScoreElement.Rest(2),
                ScoreElement.Note(new Pitch(62), 100, 2),
                ScoreElement.Rest(8),
            }),
        });

        Assert.Equal(expected, score);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Quantize_inserts_a_leading_rest_when_the_first_note_is_late()
    {
        var events = new[] { Event(64, onsetTicks: 4, durationTicks: 4) };

        Score score = Quantizer.Quantize(events, Grid48);

        var expected = new Score(Grid48.Tempo, Grid48.TimeSignature, Grid48.Subdivision, new[]
        {
            new Measure(new[]
            {
                ScoreElement.Rest(4),
                ScoreElement.Note(new Pitch(64), 100, 4),
                ScoreElement.Rest(8),
            }),
        });

        Assert.Equal(expected, score);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Quantize_does_not_mutate_input()
    {
        var events = new[] { Event(60, 0, 4), Event(62, 6, 2) };
        NoteEvent[] snapshot = events.ToArray();

        _ = Quantizer.Quantize(events, Grid48);

        Assert.Equal(snapshot, events); // NoteEvent has value equality; the list is unchanged
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Quantize_rejects_sample_rate_mismatch()
    {
        // Onset carries 44.1 kHz but the grid is 48 kHz — the currency-mismatch rule (non-negotiable 1).
        var mismatched = new NoteEvent(
            new Pitch(60),
            new SamplePosition(0, new SampleRate(44100)),
            new SampleDuration(24000, new SampleRate(44100)),
            100);

        Assert.Throws<ArgumentException>(() => Quantizer.Quantize(new[] { mismatched }, Grid48));
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Domain.QuantizerTests"
```

Expected FAILURE: compile error — `Quantizer` does not exist.

**Step 3 — Minimal implementation:**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace AudioClaudio.Domain;

/// <summary>
/// Quantizes a continuous-time performance (a list of <see cref="NoteEvent"/>s)
/// onto a tempo grid, producing a <see cref="Score"/> of measures, notes and rests.
/// Pure and deterministic: same input, same score, every run and machine
/// (non-negotiable 3). The input list is never mutated (R6.2).
/// </summary>
public static class Quantizer
{
    public static Score Quantize(IReadOnlyList<NoteEvent> events, QuantizationGrid grid)
    {
        ArgumentNullException.ThrowIfNull(events);

        // 1. Snap every event to (onsetTick, durationTicks), validating the sample rate.
        var notes = new List<GridNote>(events.Count);
        foreach (NoteEvent ev in events)
        {
            if (!ev.Onset.Rate.Equals(grid.SampleRate) || !ev.Duration.Rate.Equals(grid.SampleRate))
            {
                throw new ArgumentException(
                    $"Event sample rate does not match grid sample rate {grid.SampleRate.Hz} Hz.", nameof(events));
            }

            long onsetTick = grid.SamplesToTick(ev.Onset.Samples);
            int durationTicks = grid.NearestStandardValueTicks(ev.Duration.Samples / grid.SamplesPerTick);
            notes.Add(new GridNote(onsetTick, durationTicks, ev.Pitch, ev.Velocity));
        }

        // 2. Deterministic order: by onset, then pitch, then original index (defined tie-break).
        List<GridNote> ordered = notes
            .Select((note, index) => (note, index))
            .OrderBy(x => x.note.OnsetTick)
            .ThenBy(x => x.note.Pitch.MidiNumber)
            .ThenBy(x => x.index)
            .Select(x => x.note)
            .ToList();

        // 3. Monophonic overlap resolution: clip each note to the next onset;
        //    a note left with no room (same-tick collision) is dropped.
        var laid = new List<GridNote>(ordered.Count);
        for (int k = 0; k < ordered.Count; k++)
        {
            GridNote current = ordered[k];
            long end = current.OnsetTick + current.DurationTicks;
            if (k + 1 < ordered.Count && ordered[k + 1].OnsetTick < end)
            {
                end = ordered[k + 1].OnsetTick;
            }

            int length = (int)(end - current.OnsetTick);
            if (length <= 0)
            {
                continue; // collapsed by collision; dropped deterministically
            }

            laid.Add(current with { DurationTicks = length });
        }

        if (laid.Count == 0)
        {
            return new Score(grid.Tempo, grid.TimeSignature, grid.Subdivision, Array.Empty<Measure>());
        }

        // 4. Build a gap-filled timeline of runs and pad the tail to a whole measure.
        long totalTicks = laid[^1].OnsetTick + laid[^1].DurationTicks;
        int ticksPerMeasure = grid.TicksPerMeasure;
        long measureCount = (totalTicks + ticksPerMeasure - 1) / ticksPerMeasure;
        long paddedTotal = measureCount * ticksPerMeasure;

        var runs = new List<Run>();
        long cursor = 0;
        foreach (GridNote note in laid)
        {
            if (note.OnsetTick > cursor)
            {
                runs.Add(Run.Rest(note.OnsetTick - cursor));
            }

            runs.Add(Run.Note(note.Pitch, note.Velocity, note.DurationTicks));
            cursor = note.OnsetTick + note.DurationTicks;
        }

        if (paddedTotal > cursor)
        {
            runs.Add(Run.Rest(paddedTotal - cursor));
        }

        // 5. Split runs at barlines and group into measures.
        var measures = new List<Measure>((int)measureCount);
        var currentElements = new List<ScoreElement>();
        long positionInMeasure = 0;
        foreach (Run run in runs)
        {
            long remaining = run.Length;
            while (remaining > 0)
            {
                long room = ticksPerMeasure - positionInMeasure;
                long take = Math.Min(room, remaining);
                bool crossesBarline = take < remaining;
                currentElements.Add(run.ToElement((int)take, tiedToNext: run.IsNote && crossesBarline));

                remaining -= take;
                positionInMeasure += take;
                if (positionInMeasure == ticksPerMeasure)
                {
                    measures.Add(new Measure(currentElements));
                    currentElements = new List<ScoreElement>();
                    positionInMeasure = 0;
                }
            }
        }

        // paddedTotal is a whole number of measures, so currentElements is always flushed.
        return new Score(grid.Tempo, grid.TimeSignature, grid.Subdivision, measures);
    }

    private readonly record struct GridNote(long OnsetTick, int DurationTicks, Pitch Pitch, int Velocity);

    private readonly record struct Run(bool IsNote, Pitch Pitch, int Velocity, long Length)
    {
        public static Run Note(Pitch pitch, int velocity, long length) => new(true, pitch, velocity, length);

        public static Run Rest(long length) => new(false, default, 0, length);

        public ScoreElement ToElement(int length, bool tiedToNext) =>
            IsNote
                ? ScoreElement.Note(Pitch, Velocity, length, tiedToNext)
                : ScoreElement.Rest(length);
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Domain.QuantizerTests"
```

Expected PASS: all five core tests green.

**Step 5 — Commit** (@gitbutler):

```bash
but status -fv
but commit step-06-quantization -m "feat(domain): Quantizer core — snap, rest-fill, single measure" --changes <ids from but status -fv> --status-after
```

---

## Task 8: Barline splitting, multi-measure packing, and tempo-as-input (R6.4, R6.3)

Two behaviours the core already implements but must be pinned by tests: a note crossing a barline splits into tied segments and every measure sums to one bar (R6.4), and the declared tempo governs the result (R6.3).

**Files:**
- Test: `tests/AudioClaudio.Tests/Domain/QuantizerTests.cs` (add cases; no production change expected)

**Step 1 — Write the failing test** (append to `QuantizerTests`):

```csharp
    [Fact]
    [Trait("Category", "Fast")]
    public void Quantize_splits_note_across_barline_with_tie()
    {
        // Quarter (4 ticks) starting at tick 14 crosses the barline at 16 -> 2 + 2, tied.
        var events = new[] { Event(60, onsetTicks: 14, durationTicks: 4) };

        Score score = Quantizer.Quantize(events, Grid48);

        var expected = new Score(Grid48.Tempo, Grid48.TimeSignature, Grid48.Subdivision, new[]
        {
            new Measure(new[]
            {
                ScoreElement.Rest(14),
                ScoreElement.Note(new Pitch(60), 100, 2, tiedToNext: true),
            }),
            new Measure(new[]
            {
                ScoreElement.Note(new Pitch(60), 100, 2, tiedToNext: false),
                ScoreElement.Rest(14),
            }),
        });

        Assert.Equal(expected, score);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Quantize_measures_each_sum_to_one_bar()
    {
        var events = new[]
        {
            Event(60, onsetTicks: 1, durationTicks: 4),
            Event(64, onsetTicks: 9, durationTicks: 8),
            Event(67, onsetTicks: 20, durationTicks: 4),
        };

        Score score = Quantizer.Quantize(events, Grid48);

        Assert.NotEmpty(score.Measures);
        Assert.All(score.Measures, m => Assert.Equal(Grid48.TicksPerMeasure, m.TotalTicks));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Quantize_honours_declared_tempo_same_events_two_tempos_differ()
    {
        // R6.3: tempo is a declared input, never estimated. The identical event stream
        // quantized at two tempos lands on different grids and yields different scores.
        var onsetHalfSecond = new SamplePosition(24000, Rate); // 0.5 s at 48 kHz
        var quarterSecond = new SampleDuration(12000, Rate);   // 0.25 s
        var events = new[] { new NoteEvent(new Pitch(60), onsetHalfSecond, quarterSecond, 100) };

        var grid120 = new QuantizationGrid(Rate, new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth);
        var grid60 = new QuantizationGrid(Rate, new Tempo(60), TimeSignature.FourFour, Subdivision.Sixteenth);

        Score at120 = Quantizer.Quantize(events, grid120);
        Score at60 = Quantizer.Quantize(events, grid60);

        Assert.NotEqual(at120, at60);
    }
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Domain.QuantizerTests"
```

Expected outcome: these three cases fail **only if** the core packing is wrong. If Task 7's implementation is correct they may already pass — that is acceptable for behaviour-locking tests. If `Quantize_splits_note_across_barline_with_tie` fails, debug with @superpowers:systematic-debugging (check the barline-split `crossesBarline` flag and the padded-total measure count) and fix the code, not the test (§1 rule 8).

**Step 3 — Minimal implementation:** none expected — Task 7's `Quantizer` already handles barline splitting, multi-measure packing and tempo-as-grid-input. If a test is red, correct `Quantizer.Quantize` until green; do not weaken the assertion.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Domain.QuantizerTests"
```

Expected PASS: all QuantizerTests green (Task 7 + Task 8 cases).

**Step 5 — Commit** (@gitbutler):

```bash
but status -fv
but commit step-06-quantization -m "test(domain): barline split, bar conservation, tempo-as-input" --changes <ids from but status -fv> --status-after
```

---

## Task 9: Idempotence and determinism properties (R6.2, R6 Verify)

The headline invariant: quantizing an already-quantized score changes nothing. Expressed with the signature `[NoteEvent] -> Score`, this means: quantize arbitrary constrained events → `Score1`; reify `Score1` back to on-grid events; quantize again → `Score2`; assert `Score1 == Score2`. The reifier is a deterministic test helper (a Domain `Score → NoteEvent` reifier will be wanted by Step 7 — that seam is left for later; here it stays in the test project). Plus a plain determinism check (non-negotiable 3).

**Files:**
- Create: `tests/AudioClaudio.Tests/Domain/QuantizationTestHelpers.cs`
- Create: `tests/AudioClaudio.Tests/Domain/QuantizationProperties.cs`

**Step 1 — Write the failing test** (helper + property):

`tests/AudioClaudio.Tests/Domain/QuantizationTestHelpers.cs`:

```csharp
using System;
using System.Collections.Generic;
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.Domain;

/// <summary>
/// Reconstructs on-grid <see cref="NoteEvent"/>s from a <see cref="Score"/> by
/// merging tied note runs and mapping tick positions back to samples. Used to
/// express score-level idempotence. Chosen test grids have an integer
/// SamplesPerTick so the round-trip is exact.
/// </summary>
internal static class QuantizationTestHelpers
{
    public static IReadOnlyList<NoteEvent> ReifyOnGridEvents(Score score, QuantizationGrid grid)
    {
        var events = new List<NoteEvent>();
        long absoluteTick = 0;

        bool inNote = false;
        long noteStartTick = 0;
        long noteLengthTicks = 0;
        Pitch notePitch = default;
        int noteVelocity = 0;

        foreach (Measure measure in score.Measures)
        {
            foreach (ScoreElement element in measure.Elements)
            {
                if (element.Kind == ElementKind.Note)
                {
                    if (!inNote)
                    {
                        inNote = true;
                        noteStartTick = absoluteTick;
                        noteLengthTicks = 0;
                        notePitch = element.Pitch!.Value;
                        noteVelocity = element.Velocity;
                    }

                    noteLengthTicks += element.LengthTicks;
                    if (!element.TiedToNext)
                    {
                        events.Add(MakeEvent(noteStartTick, noteLengthTicks, notePitch, noteVelocity, grid));
                        inNote = false;
                    }
                }
                else
                {
                    inNote = false; // a rest cannot appear mid-tie
                }

                absoluteTick += element.LengthTicks;
            }
        }

        if (inNote)
        {
            events.Add(MakeEvent(noteStartTick, noteLengthTicks, notePitch, noteVelocity, grid));
        }

        return events;
    }

    private static NoteEvent MakeEvent(long startTick, long lengthTicks, Pitch pitch, int velocity, QuantizationGrid grid)
    {
        long onsetSamples = (long)Math.Round(startTick * grid.SamplesPerTick, MidpointRounding.AwayFromZero);
        long durationSamples = (long)Math.Round(lengthTicks * grid.SamplesPerTick, MidpointRounding.AwayFromZero);
        return new NoteEvent(
            pitch,
            new SamplePosition(onsetSamples, grid.SampleRate),
            new SampleDuration(durationSamples, grid.SampleRate),
            velocity);
    }
}
```

`tests/AudioClaudio.Tests/Domain/QuantizationProperties.cs`:

```csharp
using System.Collections.Generic;
using AudioClaudio.Domain;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class QuantizationProperties
{
    private static readonly SampleRate Rate = new(48000);
    private static readonly QuantizationGrid Grid =
        new(Rate, new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth); // SamplesPerTick = 6000

    [Fact]
    [Trait("Category", "Fast")]
    public void Quantize_is_idempotent_on_the_constrained_corpus()
    {
        IReadOnlyList<int> standard = Grid.StandardValueTicks; // [1,2,3,4,6,8,12,16]

        // A note = (pitch, duration index, leading gap >= 1 tick, sub-tick onset jitter).
        // |jitter| < half a tick (3000 samples) so the onset snaps back to its tick.
        var noteGen = Gen.Select(
            Gen.Int[33, 96],
            Gen.Int[0, standard.Count - 1],
            Gen.Int[1, 4],
            Gen.Int[-2000, 2000],
            (midi, durationIndex, gapTicks, jitter) =>
                (midi, durationTicks: standard[durationIndex], gapTicks, jitter));

        var sequenceGen = noteGen.List[1, 6];

        sequenceGen.Sample(sequence =>
        {
            var events = new List<NoteEvent>();
            long tick = 0;
            foreach (var (midi, durationTicks, gapTicks, jitter) in sequence)
            {
                tick += gapTicks; // a rest of >= 1 tick before each note
                long onsetSamples = tick * 6000 + jitter;
                if (onsetSamples < 0)
                {
                    onsetSamples = 0;
                }

                events.Add(new NoteEvent(
                    new Pitch(midi),
                    new SamplePosition(onsetSamples, Rate),
                    new SampleDuration(durationTicks * 6000, Rate),
                    100));
                tick += durationTicks;
            }

            Score score1 = Quantizer.Quantize(events, Grid);
            IReadOnlyList<NoteEvent> onGrid = QuantizationTestHelpers.ReifyOnGridEvents(score1, Grid);
            Score score2 = Quantizer.Quantize(onGrid, Grid);

            Assert.Equal(score1, score2);
        }, iter: 1000);
        // Reproducibility: CsCheck is deterministic per seed and prints a seed on any
        // failure; pass it as seed: "<value>" to replay (see @superpowers:systematic-debugging).
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Quantize_is_deterministic()
    {
        var events = new[]
        {
            new NoteEvent(new Pitch(60), new SamplePosition(0, Rate), new SampleDuration(24000, Rate), 100),
            new NoteEvent(new Pitch(64), new SamplePosition(36000, Rate), new SampleDuration(12000, Rate), 100),
        };

        Assert.Equal(Quantizer.Quantize(events, Grid), Quantizer.Quantize(events, Grid));
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Domain.QuantizationProperties"
```

Expected FAILURE first run: compile error until `QuantizationTestHelpers` exists; once it compiles, the property must pass. If it fails on a generated case, the reduced counterexample points at a snapping or packing defect — fix `Quantizer`, not the property (§1 rule 8), using @superpowers:systematic-debugging.

**Step 3 — Minimal implementation:** none in production — both files are tests. The property exercises the `Quantizer` from Tasks 7–8. If red, correct `Quantizer.Quantize`.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioClaudio.Tests.Domain.QuantizationProperties"
```

Expected PASS: idempotence over 1000 generated sequences, plus determinism.

Then run the whole fast suite and the formatter to confirm nothing regressed:

```bash
dotnet format
dotnet test --filter Category=Fast
```

Expected PASS: entire fast suite green; `dotnet format` reports no changes.

**Step 5 — Commit** (@gitbutler):

```bash
but status -fv
but commit step-06-quantization -m "test(domain): quantization idempotence and determinism properties" --changes <ids from but status -fv> --status-after
```

---

## Verify (step exit criteria)

- [ ] **Example:** an onset 40 ms late at 120 BPM sixteenths snaps to its intended grid line (`SamplesToTick_snaps_onset_40ms_late_to_intended_grid_line`, Task 4).
- [ ] **Property (idempotence):** quantizing an already-quantized score changes nothing (`Quantize_is_idempotent_on_the_constrained_corpus`, Task 9).
- [ ] **Property:** for events generated exactly on a grid, the score reproduces them exactly (`Quantize_reproduces_on_grid_events_exactly_and_fills_gaps_with_rests`, Task 7; reinforced by the idempotence reify round-trip, Task 9).

## Definition of Done

- [ ] `dotnet build` succeeds with warnings-as-errors (dependency rule intact: `AudioClaudio.Domain` still references only the BCL).
- [ ] `dotnet format` reports no changes.
- [ ] All new tests green, and `dotnet test --filter Category=Fast` is green.
- [ ] The requirement-coverage table is fully satisfied — every R6.x is proven by a named test.
- [ ] Non-negotiables asserted where touched: integer-tick time (Score stores ticks), rejection of mismatched sample rates (`Quantize_rejects_sample_rate_mismatch`), determinism (`Quantize_is_deterministic`).
- [ ] Work committed via the @gitbutler skill on branch `step-06-quantization`; the fine-grained commits roll up to the spec message `feat(domain): grid quantization to Score`.
- [ ] `DECISIONS.md` carries the DECISION GATE outcome — the cross-barline tie handling (option 1 structural split confirmed, or option 2 corpus constraint added) — recorded before Task 8 is implemented. No NuGet package is added. (The two narrower scope calls in *Approach* — tick-run storage vs. Step 11 notation spelling, and monophonic overlap truncation — are recorded in this plan, not in `DECISIONS.md`.)
