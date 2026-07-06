# Step 1 — Domain Primitives — Pitch Math and Sample Time — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Build-spec step:** Section 6 Step 1 (R1.1, R1.2, R1.3, R1.4, R1.5)
**Goal:** Build the pure value types the whole project speaks in — `Pitch` (MIDI↔Hz, exact round-trips), `PitchMath.CentsBetween`, `SampleRate`, `SamplePosition`/`SampleDuration` (integer time, mixed-rate rejected), and `NoteEvent` — with property tests proving the non-negotiables.
**Architecture:** Everything here lives in the innermost layer, `src/AudioClaudio.Domain` (Section 3), which references nothing but the BCL. These types have no ports and no adapters; they are the vocabulary every later port (`IAudioSource`, `ITranscriber`, …) and adapter will exchange. This is the "Money step": get the primitives exact and immutable now, and every layer above inherits their guarantees.
**Tech Stack:** C# / .NET 10 (`net10.0`), `readonly record struct` value types, `System.Math` (`Pow`, `Log2`, `Round`); tests in xUnit + CsCheck (both already referenced by `tests/AudioClaudio.Tests` from Step 0).
**Prerequisites:** Step 0 — Scaffold and the Dependency Rule — must be green and committed first (Section 1 rule 3). Specifically: the four `src/` projects and `tests/AudioClaudio.Tests` exist, project references encode Section 3, the Domain project references nothing beyond the BCL (R0.2), `Nullable`/`ImplicitUsings`/warnings-as-errors are enabled solution-wide, and the test project references xUnit **and CsCheck** (see the pre-flight check below).
**Commit (spec):** `feat(domain): pitch math and integer sample time`

---

## Approach

The mathematics (Section 4) is small and exact, and every line of it must be honored to the letter.

**Pitch and frequency.** Equal temperament maps a MIDI note number `n` to a frequency `f(n) = 440 · 2^((n−69)/12)` Hz. `n = 69` is A4 = 440 Hz by definition; the piano runs `n = 21` (A0, 27.5 Hz) to `n = 108` (C8, ≈4186 Hz). The inverse is `n(f) = 69 + 12·log₂(f/440)`. `FromFrequency` computes that continuous `n`, then rounds to the nearest integer note. Rounding is where determinism (non-negotiable 3) bites: the tie-break at exactly ±50 cents must be *defined*, not left to whatever `Math.Round` defaults to on a given run, so we pin `MidpointRounding.AwayFromZero`. Because `Frequency()` is a pure function of an integer and its inverse rounds back to that same integer, `FromFrequency(p.Frequency()) == p` holds exactly for all 88 pitches — the round-trip property (R1.1).

**Cents.** The perceptual distance between two frequencies is `1200·log₂(f₂/f₁)` cents (Section 4). It is antisymmetric (`cents(f₁,f₂) = −cents(f₂,f₁)`) and zero when the frequencies are equal. Tolerances everywhere in this project are quoted in cents, never Hz (non-negotiable 4), so this one function is load-bearing for every later detection test. Only positive, finite frequencies are meaningful; a non-positive argument is a bug and fails fast.

**Integer sample time.** Time in the domain is an integer count of samples carried *with* its `SampleRate` — a position without its rate is a bug (non-negotiable 1). `SamplePosition` and `SampleDuration` are the two integer types; `SampleRate` is the unit they are denominated in. Arithmetic across differing sample rates is the "currency-mismatch" case: it throws, it never coerces (R1.3). Seconds exist only as a *display* conversion (`ToSeconds()`) at the edge; the domain never accumulates floating time.

**No I/O, no clock.** None of these types read a file, a device, or the wall clock (R1.5). Structurally that is already guaranteed by Step 0's dependency rule (the Domain project references nothing that could); behaviorally we pin it with a determinism test (same input → bit-identical output) and a reflection test asserting no public member traffics in a clock or stream type.

Every type is a `readonly record struct`: immutable, value-equal (so `NoteEvent`s and positions compare structurally in later steps), and allocation-cheap.

**Accepted limitation — `default` construction.** C# always synthesizes a public parameterless constructor for a `struct`, and it cannot be suppressed. So `default(Pitch)`, `new Pitch()`, `default(SampleRate)`, and their siblings bypass every validating constructor here and yield the zero value: `Pitch` with `MidiNumber == 0` (outside 21..108, so `Frequency()` returns ≈8.18 Hz for a "note" that is not on the keyboard), and `SampleRate` with `Hz == 0`. This is an intrinsic property of value types, not a defect to be "fixed": suppressing it would mean turning these into classes or threading a validity sentinel through every accessor — neither of which the cross-step contract (`docs/plans/CONTRACTS.md` §1) permits for these canonical shapes. The mitigation is discipline, not machinery: every construction path in the codebase goes through the validating constructors, the boundary adapters (Steps 2+) never manufacture a primitive via `default`, and any `default` value therefore signals deliberate misuse. This limitation is recorded here so it is a documented, accepted decision rather than a silent gap.

---

## Requirements coverage

| Requirement | Task(s) | Proven by (test) |
|---|---|---|
| **R1.1** `Pitch` MIDI↔frequency, exact round-trips | Task 1, Task 2 | `PitchTests.Frequency_MatchesKnownAnchors`, `PitchTests.Constructor_RejectsOutOfRangeMidi`, `PitchTests.FromFrequency_RoundTripsEvery88Pitches`, `PitchTests.FromFrequency_MapsToNearestNoteWithin49Cents`, `PitchTests.FromFrequency_MidpointTie_RoundsAwayFromZero` |
| **R1.2** cents-distance `1200·log₂(f₂/f₁)` | Task 3 | `PitchMathTests.CentsBetween_MatchesKnownIntervals`, `PitchMathTests.CentsBetween_SameFrequencyIsExactlyZero`, `PitchMathTests.CentsBetween_IsAntisymmetric`, `PitchMathTests.CentsBetween_RejectsNonPositiveFrequencies` |
| **R1.3** integer `SampleRate`/`SamplePosition`/`SampleDuration`, mixed-rate rejected | Task 4, Task 5 | `SampleTimeTests.SampleRate_RejectsNonPositive`, `SampleTimeTests.SameRateArithmetic_AddsAndSubtracts`, `SampleTimeTests.MixedRateArithmetic_Throws`, `SampleTimeTests.ToSeconds_IsEdgeDisplayConversion` |
| **R1.4** `NoteEvent` carries pitch, onset, duration, velocity (0–127) | Task 6 | `NoteEventTests.Constructor_CarriesAllFields`, `NoteEventTests.Constructor_RejectsVelocityOutOfRange`, `NoteEventTests.Constructor_RejectsOnsetDurationRateMismatch`, `NoteEventTests.DefaultVelocity_IsConstant` |
| **R1.5** no I/O, no clock | Task 7 | `DomainPurityTests.Frequency_IsDeterministicAcrossRepeatedCalls`, `DomainPurityTests.DomainPrimitives_ExposeNoClockOrIoTypes` |

---

## Pre-flight: confirm the test project can run CsCheck

Step 0 was to leave `tests/AudioClaudio.Tests` referencing xUnit **and CsCheck** (Foundation: the test project holds the property suites). Confirm before writing any test.

```bash
cd /Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio
grep -n "CsCheck" tests/AudioClaudio.Tests/AudioClaudio.Tests.csproj
```

Expected: a `<PackageReference Include="CsCheck" ... />` line. If it is **missing** (Step 0 deferred it), add it and record the license, because Section 1 rule 7 requires every NuGet package logged in `DECISIONS.md`:

```bash
dotnet add tests/AudioClaudio.Tests/AudioClaudio.Tests.csproj package CsCheck
```

Then append to `DECISIONS.md` under a NuGet-license heading: `CsCheck — MIT — property-based testing (Section 3 pinned stack)`. If CsCheck is already present (the expected case), this step is a no-op and `DECISIONS.md` is untouched — Step 1 introduces no new package and no design decision.

## GitButler branch setup (do once, before Task 1)

This repo uses the GitButler CLI, never raw `git` (Section 1 rule 5). Create and mark the branch so edits auto-stage to it, using the **gitbutler** skill:

```bash
but branch new step-01-domain-primitives
but mark step-01-domain-primitives
```

Each task below ends with a `but commit` to this branch. The `<ids>` in those commands are the fresh change IDs from `but status -fv` (or `but diff`) at execution time — read them then, do not guess. Finer-grained per-task commit messages are fine (Section 1 rule 5); they roll up to the spec message `feat(domain): pitch math and integer sample time`.

---

## Task 1: `Pitch` — construction, range validation, and `Frequency()`

Use @superpowers:test-driven-development for the red-green loop.

**Files:**
- Create: `src/AudioClaudio.Domain/Pitch.cs`
- Test: `tests/AudioClaudio.Tests/Domain/PitchTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class PitchTests
{
    // R1.1 — the three anchors from Section 6 Step 1 "Verify".
    [Fact]
    [Trait("Category", "Fast")]
    public void Frequency_MatchesKnownAnchors()
    {
        Assert.True(System.Math.Abs(new Pitch(69).Frequency() - 440.0) <= 0.5, "A4 = 440 Hz");
        Assert.True(System.Math.Abs(new Pitch(21).Frequency() - 27.5) <= 0.5, "A0 = 27.5 Hz");
        Assert.True(System.Math.Abs(new Pitch(108).Frequency() - 4186.0) <= 0.5, "C8 ≈ 4186 Hz");
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(20)]   // below A0
    [InlineData(109)]  // above C8
    [InlineData(0)]
    public void Constructor_RejectsOutOfRangeMidi(int midi)
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new Pitch(midi));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Constructor_AcceptsFullPianoRange()
    {
        var low = new Pitch(Pitch.MinMidi);
        var high = new Pitch(Pitch.MaxMidi);
        Assert.Equal(21, low.MidiNumber);
        Assert.Equal(108, high.MidiNumber);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
cd /Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio
dotnet test --filter "FullyQualifiedName~PitchTests"
```

Expected FAILURE: compile error — `Pitch` does not exist yet in `AudioClaudio.Domain`. That is the red state.

**Step 3 — Minimal implementation:**

```csharp
namespace AudioClaudio.Domain;

/// <summary>
/// An equal-temperament pitch identified by its MIDI note number.
/// Valid for the 88-key piano: 21 (A0) through 108 (C8).
/// </summary>
public readonly record struct Pitch
{
    /// <summary>Lowest MIDI note number on an 88-key piano (A0).</summary>
    public const int MinMidi = 21;

    /// <summary>Highest MIDI note number on an 88-key piano (C8).</summary>
    public const int MaxMidi = 108;

    private const double A4Frequency = 440.0; // Hz, the tuning anchor
    private const int A4Midi = 69;

    /// <summary>The MIDI note number, in <see cref="MinMidi"/>..<see cref="MaxMidi"/>.</summary>
    public int MidiNumber { get; }

    public Pitch(int midiNumber)
    {
        if (midiNumber < MinMidi || midiNumber > MaxMidi)
            throw new ArgumentOutOfRangeException(
                nameof(midiNumber), midiNumber,
                $"MIDI note number must be in {MinMidi}..{MaxMidi} (A0..C8).");
        MidiNumber = midiNumber;
    }

    /// <summary>Frequency in Hz: 440 · 2^((n−69)/12).</summary>
    public double Frequency() => A4Frequency * Math.Pow(2.0, (MidiNumber - A4Midi) / 12.0);
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~PitchTests"
```

Expected PASS: all `PitchTests` (from this task) green; anchors within ±0.5 Hz, out-of-range MIDI rejected.

**Step 5 — Commit** (gitbutler skill):

```bash
but status -fv   # read the change IDs for Pitch.cs and PitchTests.cs
but commit step-01-domain-primitives -m "feat(domain): Pitch type with MIDI->Hz frequency" --changes <ids> --status-after
```

---

## Task 2: `Pitch.FromFrequency` — nearest note and exact round-trip (R1.1 property)

**Files:**
- Modify: `src/AudioClaudio.Domain/Pitch.cs` (add `FromFrequency`)
- Modify: `tests/AudioClaudio.Tests/Domain/PitchTests.cs` (add round-trip + nearest-note tests)

**Step 1 — Write the failing test:** append these methods to the `PitchTests` class:

```csharp
    // R1.1 — the headline round-trip, exhaustive over all 88 pitches (deterministic).
    [Fact]
    [Trait("Category", "Fast")]
    public void FromFrequency_RoundTripsEvery88Pitches()
    {
        for (int midi = Pitch.MinMidi; midi <= Pitch.MaxMidi; midi++)
        {
            var p = new Pitch(midi);
            var back = Pitch.FromFrequency(p.Frequency());
            Assert.Equal(p, back);
        }
    }

    // R1.1 — nearest-note mapping is correct for any frequency within ±49 cents of a piano pitch.
    [Fact]
    [Trait("Category", "Fast")]
    public void FromFrequency_MapsToNearestNoteWithin49Cents()
    {
        // For every pitch, and any detuning strictly inside ±49 cents, FromFrequency
        // must return that same pitch. 49 (not 50) stays clear of the exact tie.
        CsCheck.Gen.Select(
                CsCheck.Gen.Int[Pitch.MinMidi, Pitch.MaxMidi],
                CsCheck.Gen.Double[-49.0, 49.0])
            .Sample((midi, cents) =>
            {
                double f0 = new Pitch(midi).Frequency();
                double detuned = f0 * System.Math.Pow(2.0, cents / 1200.0);
                return Pitch.FromFrequency(detuned).MidiNumber == midi;
            }, iter: 10_000, seed: "0N0XvlID3sJ2");
        // The seed is pinned up front — the Foundation convention is "Fix CsCheck seeds
        // for reproducibility," so every CI run explores the same 10 000 cases bit-for-bit
        // rather than seeding a fresh sequence each time. If CsCheck ever reports a
        // counterexample it prints a replacement `seed:` string; paste it in to reproduce,
        // per @superpowers:systematic-debugging. (Any CsCheck-generated seed literal is valid.)
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(0.0)]
    [InlineData(-100.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void FromFrequency_RejectsNonPositiveOrNonFinite(double hz)
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => Pitch.FromFrequency(hz));
    }

    // R1.1 / non-negotiable 3 — the tie-break the Approach singles out as load-bearing.
    // A frequency exactly ±50 cents from a pitch lands on the midpoint between two notes,
    // and MidpointRounding.AwayFromZero resolves it deterministically toward the larger
    // magnitude — i.e. upward, since MIDI numbers are positive. So +50 cents from note n is
    // the midpoint of (n, n+1) and rounds to n+1; −50 cents from note n is the midpoint of
    // (n−1, n) and rounds to n. This exact-tie behavior is what makes the mapping reproducible
    // on every machine; the ±49-cent property above deliberately stays clear of the tie, so
    // the tie itself is pinned here (otherwise the one behavior the prose calls load-bearing
    // for determinism would never be asserted).
    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(69, +50.0, 70)]  // A4 sharp by a half-semitone → A♯4 (midpoint 69↔70 rounds up)
    [InlineData(69, -50.0, 69)]  // A4 flat  by a half-semitone → still A4 (midpoint 68↔69 rounds up)
    [InlineData(60, +50.0, 61)]  // middle C sharp by a half-semitone → C♯4
    [InlineData(60, -50.0, 60)]  // middle C flat  by a half-semitone → still C4
    public void FromFrequency_MidpointTie_RoundsAwayFromZero(int midi, double cents, int expectedMidi)
    {
        double detuned = new Pitch(midi).Frequency() * System.Math.Pow(2.0, cents / 1200.0);
        Assert.Equal(expectedMidi, Pitch.FromFrequency(detuned).MidiNumber);
    }
```

Add `using CsCheck;` is *not* required because the calls above are fully qualified; if you prefer, add `using CsCheck;` at the top and drop the `CsCheck.` prefixes.

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~PitchTests.FromFrequency"
```

Expected FAILURE: compile error — `Pitch.FromFrequency` does not exist yet.

**Step 3 — Minimal implementation:** add to `Pitch` (inside the struct, after `Frequency()`):

```csharp
    /// <summary>
    /// The nearest piano pitch to a positive frequency: round(69 + 12·log2(f/440)).
    /// Ties (exactly ±50 cents) round away from zero — a defined tie-break, for determinism.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// If <paramref name="hertz"/> is non-positive/non-finite, or the nearest note falls
    /// outside the 88-key range.
    /// </exception>
    public static Pitch FromFrequency(double hertz)
    {
        if (hertz <= 0.0 || double.IsNaN(hertz) || double.IsInfinity(hertz))
            throw new ArgumentOutOfRangeException(
                nameof(hertz), hertz, "Frequency must be a positive, finite value.");

        double exact = A4Midi + 12.0 * Math.Log2(hertz / A4Frequency);
        int nearest = (int)Math.Round(exact, MidpointRounding.AwayFromZero);
        return new Pitch(nearest);
    }
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~PitchTests"
```

Expected PASS: round-trip holds for all 88 pitches; 10 000 detuned samples all map to the intended note; non-positive/non-finite inputs rejected.

**Step 5 — Commit** (gitbutler skill):

```bash
but status -fv
but commit step-01-domain-primitives -m "feat(domain): Pitch.FromFrequency with exact round-trip property" --changes <ids> --status-after
```

---

## Task 3: `PitchMath.CentsBetween` (R1.2)

**Files:**
- Create: `src/AudioClaudio.Domain/PitchMath.cs`
- Test: `tests/AudioClaudio.Tests/Domain/PitchMathTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Domain;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class PitchMathTests
{
    // R1.2 — known intervals: an octave is 1200 cents, a semitone is 100.
    [Fact]
    [Trait("Category", "Fast")]
    public void CentsBetween_MatchesKnownIntervals()
    {
        Assert.True(System.Math.Abs(PitchMath.CentsBetween(440.0, 880.0) - 1200.0) < 1e-9, "octave up");
        Assert.True(System.Math.Abs(PitchMath.CentsBetween(880.0, 440.0) + 1200.0) < 1e-9, "octave down");
        double semitone = PitchMath.CentsBetween(new Pitch(69).Frequency(), new Pitch(70).Frequency());
        Assert.True(System.Math.Abs(semitone - 100.0) < 1e-9, "one semitone = 100 cents");
    }

    // R1.2 — distance from a frequency to itself is exactly zero.
    [Fact]
    [Trait("Category", "Fast")]
    public void CentsBetween_SameFrequencyIsExactlyZero()
    {
        Assert.Equal(0.0, PitchMath.CentsBetween(261.63, 261.63));
        Assert.Equal(0.0, PitchMath.CentsBetween(27.5, 27.5));
    }

    // R1.2 — antisymmetry: cents(f1,f2) == -cents(f2,f1) across the piano range.
    [Fact]
    [Trait("Category", "Fast")]
    public void CentsBetween_IsAntisymmetric()
    {
        Gen.Select(Gen.Double[20.0, 5000.0], Gen.Double[20.0, 5000.0])
            .Sample((f1, f2) =>
            {
                double forward = PitchMath.CentsBetween(f1, f2);
                double backward = PitchMath.CentsBetween(f2, f1);
                return System.Math.Abs(forward + backward) < 1e-7;
            }, iter: 10_000, seed: "0N0XvlID3sJ2");
        // Seed pinned up front for reproducible CI (Foundation: "Fix CsCheck seeds for
        // reproducibility"); replace with any CsCheck-reported seed to reproduce a failure.
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(0.0, 440.0)]
    [InlineData(440.0, 0.0)]
    [InlineData(-1.0, 440.0)]
    [InlineData(440.0, double.NaN)]
    public void CentsBetween_RejectsNonPositiveFrequencies(double f1, double f2)
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => PitchMath.CentsBetween(f1, f2));
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~PitchMathTests"
```

Expected FAILURE: compile error — `PitchMath` does not exist yet.

**Step 3 — Minimal implementation:**

```csharp
namespace AudioClaudio.Domain;

/// <summary>Pure pitch/frequency helpers. No state, no I/O, no clock.</summary>
public static class PitchMath
{
    /// <summary>
    /// Perceptual distance in cents (1/100 of a semitone): 1200 · log2(f2 / f1).
    /// Positive when <paramref name="f2"/> is higher than <paramref name="f1"/>.
    /// </summary>
    /// <param name="f1">Reference frequency in Hz (must be positive and finite).</param>
    /// <param name="f2">Target frequency in Hz (must be positive and finite).</param>
    public static double CentsBetween(double f1, double f2)
    {
        RequirePositiveFinite(f1, nameof(f1));
        RequirePositiveFinite(f2, nameof(f2));
        return 1200.0 * Math.Log2(f2 / f1);
    }

    private static void RequirePositiveFinite(double f, string name)
    {
        if (f <= 0.0 || double.IsNaN(f) || double.IsInfinity(f))
            throw new ArgumentOutOfRangeException(name, f, "Frequency must be a positive, finite value.");
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~PitchMathTests"
```

Expected PASS: known intervals within 1e-9; self-distance exactly 0; antisymmetry over 10 000 pairs; non-positive frequencies rejected.

**Step 5 — Commit** (gitbutler skill):

```bash
but status -fv
but commit step-01-domain-primitives -m "feat(domain): PitchMath.CentsBetween with antisymmetry property" --changes <ids> --status-after
```

---

## Task 4: `SampleRate` (R1.3, part 1)

> **Cross-step contract (Step 1 is the definer).** The accessor is `Hz` (an `int`),
> **never `Hertz`**. Steps 2–11 consume `SampleRate.Hz` verbatim per
> `docs/plans/CONTRACTS.md` §0; any consumer plan still reading `.Hertz` is wrong and
> must change to `.Hz`, not the other way round. Do not rename this property.

**Files:**
- Create: `src/AudioClaudio.Domain/SampleRate.cs`
- Test: `tests/AudioClaudio.Tests/Domain/SampleTimeTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class SampleTimeTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void SampleRate_CarriesHzAndComparesByValue()
    {
        var a = new SampleRate(44100);
        var b = new SampleRate(44100);
        var c = new SampleRate(48000);
        Assert.Equal(44100, a.Hz);
        Assert.Equal(a, b);        // value equality
        Assert.NotEqual(a, c);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(0)]
    [InlineData(-1)]
    public void SampleRate_RejectsNonPositive(int hz)
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new SampleRate(hz));
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~SampleTimeTests.SampleRate"
```

Expected FAILURE: compile error — `SampleRate` does not exist yet.

**Step 3 — Minimal implementation:**

```csharp
namespace AudioClaudio.Domain;

/// <summary>
/// A sampling rate in Hz (e.g. 44100). The unit that a <see cref="SamplePosition"/>
/// or <see cref="SampleDuration"/> is denominated in — a sample count without its
/// rate is a bug (non-negotiable 1).
/// </summary>
public readonly record struct SampleRate
{
    /// <summary>Samples per second. Always positive.</summary>
    public int Hz { get; }

    public SampleRate(int hz)
    {
        if (hz <= 0)
            throw new ArgumentOutOfRangeException(nameof(hz), hz, "Sample rate must be positive.");
        Hz = hz;
    }

    public override string ToString() => $"{Hz} Hz";
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~SampleTimeTests.SampleRate"
```

Expected PASS: `Hz` carried, value equality holds, non-positive rejected.

**Step 5 — Commit** (gitbutler skill):

```bash
but status -fv
but commit step-01-domain-primitives -m "feat(domain): SampleRate value type" --changes <ids> --status-after
```

---

## Task 5: `SamplePosition` and `SampleDuration` — integer time, mixed-rate rejected (R1.3, part 2)

This is the currency-mismatch rule made physical: arithmetic across differing sample rates throws, never coerces.

**Files:**
- Create: `src/AudioClaudio.Domain/SampleDuration.cs`
- Create: `src/AudioClaudio.Domain/SamplePosition.cs`
- Modify: `tests/AudioClaudio.Tests/Domain/SampleTimeTests.cs` (add arithmetic + mismatch tests)

**Step 1 — Write the failing test:** append to the `SampleTimeTests` class:

```csharp
    private static readonly SampleRate R44 = new(44100);
    private static readonly SampleRate R48 = new(48000);

    [Fact]
    [Trait("Category", "Fast")]
    public void Position_And_Duration_CarrySamplesWithRate()
    {
        var pos = new SamplePosition(1000, R44);
        var dur = new SampleDuration(500, R44);
        Assert.Equal(1000, pos.Samples);
        Assert.Equal(R44, pos.Rate);
        Assert.Equal(500, dur.Samples);
        Assert.Equal(R44, dur.Rate);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(-1)]
    public void Position_And_Duration_RejectNegativeSamples(long samples)
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new SamplePosition(samples, R44));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new SampleDuration(samples, R44));
    }

    // R1.3 — same-rate arithmetic: position + duration -> position; position - position -> duration.
    [Fact]
    [Trait("Category", "Fast")]
    public void SameRateArithmetic_AddsAndSubtracts()
    {
        var pos = new SamplePosition(1000, R44);
        SamplePosition later = pos + new SampleDuration(500, R44);
        Assert.Equal(1500, later.Samples);
        Assert.Equal(R44, later.Rate);

        SampleDuration between = later - pos;
        Assert.Equal(500, between.Samples);
        Assert.Equal(R44, between.Rate);

        SampleDuration total = new SampleDuration(200, R44) + new SampleDuration(300, R44);
        Assert.Equal(500, total.Samples);
    }

    // R1.3 — the currency-mismatch rule: mixing 44.1 kHz and 48 kHz throws, never coerces.
    [Fact]
    [Trait("Category", "Fast")]
    public void MixedRateArithmetic_Throws()
    {
        var pos44 = new SamplePosition(1000, R44);
        var dur48 = new SampleDuration(500, R48);
        var pos48 = new SamplePosition(1000, R48);

        Assert.Throws<System.InvalidOperationException>(() => { var _ = pos44 + dur48; });
        Assert.Throws<System.InvalidOperationException>(() => { var _ = pos44 - pos48; });
        Assert.Throws<System.InvalidOperationException>(() =>
        {
            var _ = new SampleDuration(1, R44) + new SampleDuration(1, R48);
        });
    }

    // Section 4 — seconds are a DISPLAY conversion at the edge only.
    [Fact]
    [Trait("Category", "Fast")]
    public void ToSeconds_IsEdgeDisplayConversion()
    {
        var oneSecond = new SamplePosition(44100, R44);
        Assert.True(System.Math.Abs(oneSecond.ToSeconds() - 1.0) < 1e-12);
        var halfSecond = new SampleDuration(22050, R44);
        Assert.True(System.Math.Abs(halfSecond.ToSeconds() - 0.5) < 1e-12);
    }
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~SampleTimeTests"
```

Expected FAILURE: compile errors — `SamplePosition` and `SampleDuration` do not exist yet.

**Step 3 — Minimal implementation:**

`src/AudioClaudio.Domain/SampleDuration.cs`:

```csharp
namespace AudioClaudio.Domain;

/// <summary>
/// A duration as an integer count of samples at a declared <see cref="SampleRate"/>.
/// Integer time only — seconds are a display conversion at the edge (non-negotiable 1).
/// </summary>
public readonly record struct SampleDuration
{
    /// <summary>Number of samples. Always non-negative.</summary>
    public long Samples { get; }

    /// <summary>The rate these samples are counted at.</summary>
    public SampleRate Rate { get; }

    public SampleDuration(long samples, SampleRate rate)
    {
        if (samples < 0)
            throw new ArgumentOutOfRangeException(nameof(samples), samples, "Sample duration must be non-negative.");
        Samples = samples;
        Rate = rate;
    }

    /// <summary>Display conversion to seconds. Never used for domain arithmetic.</summary>
    public double ToSeconds() => (double)Samples / Rate.Hz;

    public static SampleDuration operator +(SampleDuration a, SampleDuration b)
    {
        RequireSameRate(a.Rate, b.Rate);
        return new SampleDuration(a.Samples + b.Samples, a.Rate);
    }

    /// <summary>
    /// The currency-mismatch guard (R1.3): differing rates throw, never coerce.
    /// Internal so <see cref="SamplePosition"/> shares one definition.
    /// </summary>
    internal static void RequireSameRate(SampleRate x, SampleRate y)
    {
        if (x != y)
            throw new InvalidOperationException(
                $"Sample-rate mismatch: {x.Hz} Hz vs {y.Hz} Hz. Rates must match; values are never coerced.");
    }
}
```

`src/AudioClaudio.Domain/SamplePosition.cs`:

```csharp
namespace AudioClaudio.Domain;

/// <summary>
/// A position in an audio stream as an integer count of samples from the start,
/// at a declared <see cref="SampleRate"/>. A position without its rate is a bug
/// (non-negotiable 1).
/// </summary>
public readonly record struct SamplePosition
{
    /// <summary>Samples from the start of the stream. Always non-negative.</summary>
    public long Samples { get; }

    /// <summary>The rate these samples are counted at.</summary>
    public SampleRate Rate { get; }

    public SamplePosition(long samples, SampleRate rate)
    {
        if (samples < 0)
            throw new ArgumentOutOfRangeException(nameof(samples), samples, "Sample position must be non-negative.");
        Samples = samples;
        Rate = rate;
    }

    /// <summary>Display conversion to seconds. Never used for domain arithmetic.</summary>
    public double ToSeconds() => (double)Samples / Rate.Hz;

    /// <summary>Advance a position by a duration. Rates must match.</summary>
    public static SamplePosition operator +(SamplePosition p, SampleDuration d)
    {
        SampleDuration.RequireSameRate(p.Rate, d.Rate);
        return new SamplePosition(p.Samples + d.Samples, p.Rate);
    }

    /// <summary>
    /// Elapsed duration between two positions. Rates must match; an earlier-minus-later
    /// result is negative and rejected by <see cref="SampleDuration"/>'s constructor.
    /// </summary>
    public static SampleDuration operator -(SamplePosition a, SamplePosition b)
    {
        SampleDuration.RequireSameRate(a.Rate, b.Rate);
        return new SampleDuration(a.Samples - b.Samples, a.Rate);
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~SampleTimeTests"
```

Expected PASS: same-rate arithmetic works, mixed-rate throws `InvalidOperationException`, negatives rejected, `ToSeconds` correct.

**Step 5 — Commit** (gitbutler skill):

```bash
but status -fv
but commit step-01-domain-primitives -m "feat(domain): integer SamplePosition/SampleDuration with mixed-rate rejection" --changes <ids> --status-after
```

---

## Task 6: `NoteEvent` (R1.4)

**Files:**
- Create: `src/AudioClaudio.Domain/NoteEvent.cs`
- Test: `tests/AudioClaudio.Tests/Domain/NoteEventTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class NoteEventTests
{
    private static readonly SampleRate R44 = new(44100);

    [Fact]
    [Trait("Category", "Fast")]
    public void Constructor_CarriesAllFields()
    {
        var pitch = new Pitch(60); // middle C
        var onset = new SamplePosition(1000, R44);
        var duration = new SampleDuration(22050, R44);
        var note = new NoteEvent(pitch, onset, duration, velocity: 100);

        Assert.Equal(pitch, note.Pitch);
        Assert.Equal(onset, note.Onset);
        Assert.Equal(duration, note.Duration);
        Assert.Equal(100, note.Velocity);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(-1)]
    [InlineData(128)]
    public void Constructor_RejectsVelocityOutOfRange(int velocity)
    {
        var pitch = new Pitch(60);
        var onset = new SamplePosition(0, R44);
        var duration = new SampleDuration(100, R44);
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => new NoteEvent(pitch, onset, duration, velocity));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Constructor_RejectsOnsetDurationRateMismatch()
    {
        var pitch = new Pitch(60);
        var onset = new SamplePosition(0, new SampleRate(44100));
        var duration = new SampleDuration(100, new SampleRate(48000));
        Assert.Throws<System.InvalidOperationException>(
            () => new NoteEvent(pitch, onset, duration, 64));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void DefaultVelocity_IsConstant()
    {
        var note = new NoteEvent(new Pitch(60), new SamplePosition(0, R44), new SampleDuration(100, R44));
        Assert.Equal(NoteEvent.DefaultVelocity, note.Velocity);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void NoteEvent_ComparesByValue()
    {
        var a = new NoteEvent(new Pitch(60), new SamplePosition(0, R44), new SampleDuration(100, R44), 64);
        var b = new NoteEvent(new Pitch(60), new SamplePosition(0, R44), new SampleDuration(100, R44), 64);
        Assert.Equal(a, b);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~NoteEventTests"
```

Expected FAILURE: compile error — `NoteEvent` does not exist yet.

**Step 3 — Minimal implementation:**

```csharp
namespace AudioClaudio.Domain;

/// <summary>
/// A single detected/quantized note: a pitch sounding from an onset for a duration,
/// at a given MIDI velocity. Immutable and value-equal. The unit the whole pipeline
/// exchanges once audio has become notes.
/// </summary>
public readonly record struct NoteEvent
{
    /// <summary>Minimum MIDI velocity.</summary>
    public const int MinVelocity = 0;

    /// <summary>Maximum MIDI velocity.</summary>
    public const int MaxVelocity = 127;

    /// <summary>Constant velocity the MVP may emit when it does not estimate dynamics (R1.4).</summary>
    public const int DefaultVelocity = 64;

    public Pitch Pitch { get; }
    public SamplePosition Onset { get; }
    public SampleDuration Duration { get; }

    /// <summary>MIDI velocity in 0..127.</summary>
    public int Velocity { get; }

    public NoteEvent(Pitch pitch, SamplePosition onset, SampleDuration duration, int velocity = DefaultVelocity)
    {
        if (velocity < MinVelocity || velocity > MaxVelocity)
            throw new ArgumentOutOfRangeException(
                nameof(velocity), velocity, $"Velocity must be in {MinVelocity}..{MaxVelocity}.");
        if (onset.Rate != duration.Rate)
            throw new InvalidOperationException(
                $"Sample-rate mismatch between onset ({onset.Rate.Hz} Hz) and duration ({duration.Rate.Hz} Hz).");
        Pitch = pitch;
        Onset = onset;
        Duration = duration;
        Velocity = velocity;
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~NoteEventTests"
```

Expected PASS: all fields carried, velocity range enforced, onset/duration rate mismatch rejected, default velocity constant, value equality holds.

**Step 5 — Commit** (gitbutler skill):

```bash
but status -fv
but commit step-01-domain-primitives -m "feat(domain): NoteEvent value type" --changes <ids> --status-after
```

---

## Task 7: Purity — determinism and no clock/I/O (R1.5)

R1.5 says none of these types perform I/O or read a clock. Structurally that is already guaranteed by Step 0's dependency rule (the Domain project references nothing that could). Here we pin it behaviorally with a determinism test and a reflection test over the public surface.

**Files:**
- Create: `tests/AudioClaudio.Tests/Domain/DomainPurityTests.cs`
- (No production code — this task proves an existing property; it must pass without any new implementation.)

**Step 1 — Write the failing test:**

```csharp
using System;
using System.IO;
using System.Reflection;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class DomainPurityTests
{
    // R1.5 / non-negotiable 3 — same input, bit-identical output on every call.
    [Fact]
    [Trait("Category", "Fast")]
    public void Frequency_IsDeterministicAcrossRepeatedCalls()
    {
        for (int midi = Pitch.MinMidi; midi <= Pitch.MaxMidi; midi++)
        {
            var p = new Pitch(midi);
            double first = p.Frequency();
            for (int i = 0; i < 5; i++)
                Assert.Equal(first, p.Frequency()); // exact double equality
        }
    }

    // R1.5 — no public member of a domain primitive accepts or returns a clock or stream type.
    [Fact]
    [Trait("Category", "Fast")]
    public void DomainPrimitives_ExposeNoClockOrIoTypes()
    {
        Type[] primitives =
        {
            typeof(Pitch), typeof(PitchMath), typeof(SampleRate),
            typeof(SamplePosition), typeof(SampleDuration), typeof(NoteEvent),
        };
        Type[] forbidden =
        {
            typeof(DateTime), typeof(DateTimeOffset), typeof(TimeProvider),
            typeof(Stream), typeof(TextReader), typeof(TextWriter), typeof(FileStream),
        };

        foreach (Type t in primitives)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance |
                                       BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (MethodInfo m in t.GetMethods(flags))
            {
                Assert.DoesNotContain(m.ReturnType, forbidden);
                foreach (ParameterInfo p in m.GetParameters())
                    Assert.DoesNotContain(p.ParameterType, forbidden);
            }
            foreach (ConstructorInfo ctor in t.GetConstructors())
                foreach (ParameterInfo p in ctor.GetParameters())
                    Assert.DoesNotContain(p.ParameterType, forbidden);
        }
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~DomainPurityTests"
```

Expected FAILURE: compile error until the file is added; if a hidden clock/stream member ever crept into a primitive, `DomainPrimitives_ExposeNoClockOrIoTypes` would also fail. (With Tasks 1–6 in place and clean, this file compiles and both tests pass immediately — the red here is the missing test file.)

**Step 3 — Minimal implementation:** none. This task asserts a property the earlier tasks already satisfy; if either test fails, the fix is in the offending primitive, not the test (Section 1 rule 8). Use @superpowers:systematic-debugging if it does.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~DomainPurityTests"
```

Expected PASS: `Frequency()` bit-identical across repeated calls for all 88 pitches; no primitive exposes a clock or stream type.

**Step 5 — Commit** (gitbutler skill):

```bash
but status -fv
but commit step-01-domain-primitives -m "test(domain): determinism and no-clock/IO purity guards" --changes <ids> --status-after
```

---

## Task 8: Format, full suite, and finalize the step

**Files:** none new — this task verifies the whole step is clean and green together. Use @superpowers:verification-before-completion (run the commands, read the output; do not claim green without it).

**Step 1 — Format:**

```bash
cd /Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio
dotnet format
```

Expected: no changes, or whitespace-only fixes. Re-run until clean.

**Step 2 — Build with warnings-as-errors (the dependency rule and analyzers must bite):**

```bash
dotnet build
```

Expected PASS: solution builds, zero warnings (warnings are errors, per Step 0).

**Step 3 — Full suite, then the fast filter:**

```bash
dotnet test
dotnet test --filter Category=Fast
```

Expected PASS both times: every Step 1 test green. (Step 1 introduces no Slow-category tests, so the two runs cover the same set here.)

**Step 4 — Confirm the dependency rule is intact** (Domain still references nothing beyond the BCL — the primitives added no using of another project or package):

```bash
grep -c "ProjectReference\|PackageReference" src/AudioClaudio.Domain/AudioClaudio.Domain.csproj
```

Expected: `0` (or only framework references). If any project/package reference appears in the Domain csproj, STOP — an inward-pointing dependency was introduced; raise it per Section 1 rule 6 rather than break the boundary.

**Step 5 — Commit any format-only changes** (gitbutler skill). If `dotnet format` changed nothing, this is a no-op and Tasks 1–7 already carry the step:

```bash
but status -fv
but commit step-01-domain-primitives -m "style(domain): dotnet format" --changes <ids> --status-after
```

If you prefer a single roll-up commit for the step instead of the seven finer commits above, the spec message is `feat(domain): pitch math and integer sample time` — either satisfies Section 1 rule 5.

---

## Verify (step exit criteria)

Restating Section 6 Step 1 "Verify" as checks — all must be green:

- [ ] **Example:** A4 = MIDI 69 = 440 Hz (`PitchTests.Frequency_MatchesKnownAnchors`).
- [ ] **Example:** A0 = 27.5 Hz (same test).
- [ ] **Example:** C8 = 4186 Hz within ±0.5 Hz (same test).
- [ ] **Property:** `FromFrequency(p.Frequency()) == p` for all 88 pitches (`PitchTests.FromFrequency_RoundTripsEvery88Pitches`).
- [ ] **Property:** `centsBetween(f, f) == 0` (`PitchMathTests.CentsBetween_SameFrequencyIsExactlyZero`).
- [ ] **Property:** cents distance is antisymmetric (`PitchMathTests.CentsBetween_IsAntisymmetric`).
- [ ] **Property:** nearest-note mapping correct for any frequency within ±49 cents of a piano pitch (`PitchTests.FromFrequency_MapsToNearestNoteWithin49Cents`).
- [ ] **Property:** the exact ±50-cent tie resolves deterministically via `MidpointRounding.AwayFromZero` (`PitchTests.FromFrequency_MidpointTie_RoundsAwayFromZero`).
- [ ] **Property:** mixed-sample-rate arithmetic is rejected (`SampleTimeTests.MixedRateArithmetic_Throws`).

## Definition of Done

- [ ] `dotnet build` succeeds with zero warnings (warnings-as-errors).
- [ ] `dotnet format` reports no changes.
- [ ] All new tests green under `dotnet test`, and `dotnet test --filter Category=Fast` is green.
- [ ] Dependency rule intact: `src/AudioClaudio.Domain` references nothing beyond the BCL (no `ProjectReference`/`PackageReference` added).
- [ ] Every R1.x row in the Requirements-coverage table is satisfied by a passing test.
- [ ] Accepted limitation recorded (Approach): `default`/parameterless `struct` construction bypasses the validating constructors — an intrinsic C# property of value types, deliberately not guarded, since the cross-step contract fixes these shapes.
- [ ] Work committed via GitButler on branch `step-01-domain-primitives` (finer commits rolling up to `feat(domain): pitch math and integer sample time`).
- [ ] `DECISIONS.md` updated **only if** CsCheck had to be added in the pre-flight check (Step 1 has no design decision and adds no other package; otherwise `DECISIONS.md` is unchanged).
