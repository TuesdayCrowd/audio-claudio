# Step 2 — The Audio Source Port and the WAV Adapter — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Build-spec step:** Section 6 Step 2 (R2.1, R2.2, R2.3, R2.4)
**Goal:** Make "the microphone is just an adapter" real: an `IAudioSource` port that yields `Frame`s, a hand-rolled 16/24-bit PCM WAV adapter that downmixes to mono, and a deterministic signal generator (pure sine + harmonic stack) in the test utilities.
**Architecture:** `Frame`, `FrameParameters`, and the pure `Framing` splitter live in **Domain** (they are vocabulary and math, no I/O). The `IAudioSource` **port** lives in **Application** under `AudioClaudio.Application.Ports` (references Domain only). The hand-rolled `WavAudioSource` **adapter** lives in **Infrastructure** under `AudioClaudio.Infrastructure.Audio` (references Application + Domain). The deterministic `SignalGenerator` (namespace `AudioClaudio.Tests.Signals`) and `WavWriter` are **test utilities** in `tests/AudioClaudio.Tests`, with shared helpers under `AudioClaudio.Tests.TestSupport`. No wiring happens in the Cli yet — composition arrives in later steps.
**Tech Stack:** Pure BCL only — `System.IO` (`Stream`, `MemoryStream`, `BinaryWriter`), `System.Buffers.Binary.BinaryPrimitives` for little-endian reads, `System.Math`. Tests: xUnit (Apache-2.0) + CsCheck (MIT). **No new NuGet package is added in this step** (RIFF parsing is hand-rolled per R2.2), so `DECISIONS.md` gains no license row — only the frame-delivery decision below.
**Prerequisites:** Step 0 (scaffold + dependency-rule project references + CI) and Step 1 (`Pitch`, `SampleRate`, `SamplePosition`, `SampleDuration`, `NoteEvent`) both green and committed — Section 1 rule 3. This plan assumes the Step 1 API exactly as the Foundation fixes it: `SampleRate { int Hz }` (`new SampleRate(44100)`), `SamplePosition { long Samples; SampleRate Rate }` (`new SamplePosition(0, rate)`), and `Pitch { int MidiNumber }` with `double Frequency()`. If Step 1 named a member differently, reconcile there — do not fork it here.
**Commit (spec):** `feat: IAudioSource port, WAV adapter, deterministic signal generator`

---

## ⚠ DECISION GATE (Cornelius owns this — Section 1 rule 2)

**The fork: how does `IAudioSource` deliver frames — PULL or PUSH?**

Step 2's spec lists a *Design decision* on the frame-delivery model. Do not silently pick a side.

- **PULL — `IEnumerable<Frame> Frames { get; }`.** The consumer drives; the source yields successive frames on demand.
  - *For:* Dead simple and lazy for files; the natural shape for tests (`source.Frames.ToList()`); no threads, no callbacks, deterministic ordering by construction; back-pressure is implicit (nothing is produced until pulled). It is the smallest thing that satisfies R2.1.
  - *Against:* A live audio device (Step 10) *pushes* buffers from its own callback thread on its own schedule. A pull source cannot model that directly, so Step 10 must add a **bounded-channel bridge**: the PortAudio callback writes each buffer into a bounded `Channel<Frame>`, and the pull side drains that channel as an `IEnumerable`/`IAsyncEnumerable`. The bridge is real code, deferred to Step 10.

- **PUSH — a callback or `Channel<Frame>` the source writes to.**
  - *For:* Matches the live device model directly — no bridge needed in Step 10; the device callback becomes the producer.
  - *Against:* For files and tests you must run a producer loop and collect the results anyway, so the "natural for live" advantage does not pay off until Step 10; determinism and ordering now need explicit care; threading concerns leak into every unit test that just wants a list of frames.

**Spec recommendation:** **PULL now**, with the bounded-channel bridge added for the mic in Step 10. Files and tests — everything Steps 2–9 exercise — are pull-shaped; the one push producer (the device) arrives last and is adapted to the proven pull pipeline rather than the reverse.

**Resolution:** record in DECISIONS.md before implementing; do not silently pick a side.

**How this plan isolates the decision behind a seam.** Only two things differ between the two options:
1. The **signature** of `IAudioSource` (Task 3).
2. The **one line** inside the test helper `AudioSources.Collect(IAudioSource)` that turns a source into a `List<Frame>` (Task 3).

Everything else — `Frame`, `FrameParameters`, `Framing`, the WAV parser, the signal generator, and every assertion on frame *content* — is identical under both options, because every test collects frames through `AudioSources.Collect(...)` rather than touching the port shape directly. The tasks below are written for the **recommended PULL** shape; each place that would change under PUSH is called out inline with an `Under PUSH:` note. If Cornelius picks PUSH, only Task 3's interface and that one helper body change; Tasks 1, 2, 4–9 are untouched.

---

## Requirements coverage

| Requirement | Task(s) | Proven by (test) |
|---|---|---|
| **R2.1** — `IAudioSource` yields mono float `[-1,1]` frames at a declared rate, each with its starting `SamplePosition` | Task 3 (port + `InMemoryAudioSource`), Task 6 (`WavAudioSource` yields positioned frames) | `AudioSourcePortTests.InMemory_source_yields_positioned_in_range_frames`; `WavAudioSource16Tests.Parses_a_known_mono_16bit_wav_into_expected_samples` |
| **R2.2** — hand-rolled 16/24-bit PCM WAV adapter, multichannel downmix to mono | Task 6 (16-bit parse + round-trip, `FromFile` + `IDisposable`), Task 7 (24-bit), Task 8 (stereo downmix = mean), Task 9 (chunk-order robustness incl. data-before-fmt + reject unsupported) | `WavAudioSource16Tests.*`, `WavAudioSource24Tests.*`, `WavAudioSourceStereoTests.*`, `WavAudioSourceRobustnessTests.*` |
| **R2.3** — deterministic signal generator: pure sine + harmonic stack (`1/k^p` decay), rendered to frames and to WAV | Task 4 (`SignalGenerator`), Task 5 (`WavWriter` renders to WAV bytes and files via `WriteMonoFile`), Task 6 (round-trip renders buffer → frames) | `SignalGeneratorTests.*`; `WavWriterTests.*`; `WavAudioSource16Tests.Sine_round_trips_through_16bit_wav_exactly` |
| **R2.4** — frame size N and hop H are pipeline **parameters**, not scattered constants | Task 1 (`FrameParameters` value type), Task 2 (`Framing` tiles by the declared hop) | `FrameTests.FrameParameters_*`; `FramingProperties.*` |

---

## Approach

Two small pieces of substance sit under all the I/O in this step; understand them first.

**Framing.** Audio is processed in overlapping windows. Given a mono buffer of length `L`, a frame **size** `N`, and a **hop** `H`, frame `i` covers samples `[i·H, i·H + N)` and its starting position is sample `i·H`. We emit a frame for every hop position whose start is still inside the buffer (`i·H < L`); the final frame is **zero-padded** if it runs past the end, so every input sample lands in at least one frame. When `H ≤ N` there are no gaps between consecutive frames (they abut when `H = N`, overlap when `H < N`). This is the only place framing math lives — R2.4 made concrete: `N` and `H` are the fields of a `FrameParameters` value, passed in, never hard-coded. Framing is pure, deterministic, no I/O, so it belongs in **Domain**, where both the Infrastructure adapter and the test utilities can reuse the one implementation.

**The WAV/RIFF format.** A canonical PCM `.wav` is a RIFF container: the 12-byte header `"RIFF" <int32 size> "WAVE"`, then a sequence of **chunks**, each `<4-byte id> <int32 size> <size bytes of body>` (bodies are word-aligned — an odd size is followed by one pad byte). We care about two chunks: `"fmt "` (audio format, channel count, sample rate, bits per sample) and `"data"` (the interleaved PCM samples). A robust reader **walks the chunk list** rather than assuming a fixed layout, because encoders legally insert `LIST`, `fact`, and other chunks in any order. All multi-byte integers are **little-endian**. 16-bit samples are signed `int16`; 24-bit samples are signed 3-byte little-endian integers (sign-extend the top bit). Multichannel audio interleaves one sample per channel per frame; we **downmix to mono by averaging** the channels.

**The PCM quantisation convention (why round-trips are exact).** Encoding a float in `[-1,1)` to an integer loses precision, so a naive "read back equals what I wrote" is impossible in general. We make it *exact on the quantisation grid* by choosing the writer and reader to be inverses on that grid: the writer computes `q = round(x · 2^(bits-1))`, clamped to the signed range; the reader computes `x' = q / 2^(bits-1)` (same scale `2^15 = 32768` for 16-bit, `2^23 = 8388608` for 24-bit). Then for any already-quantised value, `read(write(x')) == x'` bit-for-bit. The round-trip tests compute their *expected* frames by putting the generated buffer through the **same** quantisation and then framing it, so equality is bit-exact — and it meaningfully exercises the adapter, because the adapter parses the RIFF bytes with its own independent parser (a wrong offset, wrong endianness, wrong channel stride, or off-by-one in framing all break the equality). Because expected and actual are both computed in the *same test run* from the *same* generated buffer, `Math.Sin`'s cross-platform ULP behaviour never enters — Step 2 commits no `Math.Sin`-derived golden bytes.

Work the tasks in order with @superpowers:test-driven-development (red → green → commit). If a property test surprises you, reach for @superpowers:systematic-debugging before touching the assertion — Section 1 rule 8, the test is presumed right.

---

### Task 1: `Frame` and `FrameParameters` (Domain)

**Files:**
- Create: `src/AudioClaudio.Domain/Frame.cs`
- Create: `src/AudioClaudio.Domain/FrameParameters.cs`
- Test: `tests/AudioClaudio.Tests/Domain/FrameTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class FrameTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Frame_exposes_samples_start_and_rate()
    {
        var rate = new SampleRate(44100);
        var start = new SamplePosition(2048, rate);
        var frame = new Frame(new float[] { 0.1f, -0.2f }, start);

        Assert.Equal(new float[] { 0.1f, -0.2f }, frame.Samples);
        Assert.Equal(2048, frame.Start.Samples);
        Assert.Equal(44100, frame.Rate.Hz); // Rate is derived from Start — one source of truth
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void FrameParameters_carries_size_and_hop()
    {
        var p = new FrameParameters(2048, 512);
        Assert.Equal(2048, p.Size);
        Assert.Equal(512, p.Hop);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(-1, 4)]
    [InlineData(4, -1)]
    public void FrameParameters_rejects_non_positive_size_or_hop(int size, int hop)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameParameters(size, hop));
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~FrameTests"
```

Expected FAILURE: compile error — `Frame` and `FrameParameters` do not exist yet in `AudioClaudio.Domain`.

**Step 3 — Minimal implementation:**

`src/AudioClaudio.Domain/Frame.cs`:

```csharp
namespace AudioClaudio.Domain;

/// <summary>
/// One analysis window: mono PCM samples (float, nominally in [-1, 1]) together with
/// the <see cref="SamplePosition"/> of the window's first sample. The sample rate is
/// carried by <see cref="Start"/> (a position without its rate is a bug), so
/// <see cref="Rate"/> is derived rather than duplicated.
/// </summary>
public sealed class Frame
{
    /// <summary>The window's samples. Treat as read-only; downstream DSP reads it in place.</summary>
    public float[] Samples { get; }

    /// <summary>Position (in samples, at <see cref="Rate"/>) of this frame's first sample.</summary>
    public SamplePosition Start { get; }

    /// <summary>The declared sample rate, taken from <see cref="Start"/>.</summary>
    public SampleRate Rate => Start.Rate;

    public Frame(float[] samples, SamplePosition start)
    {
        Samples = samples ?? throw new ArgumentNullException(nameof(samples));
        Start = start;
    }
}
```

`src/AudioClaudio.Domain/FrameParameters.cs`:

```csharp
namespace AudioClaudio.Domain;

/// <summary>
/// The two pipeline parameters of framing: the window <see cref="Size"/> (N samples) and the
/// <see cref="Hop"/> (H samples) between successive windows. R2.4: these are parameters, never
/// constants scattered through the code. Hop &lt;= Size is the gap-free regime.
/// </summary>
public readonly record struct FrameParameters
{
    public int Size { get; }
    public int Hop { get; }

    public FrameParameters(int size, int hop)
    {
        if (size < 1)
            throw new ArgumentOutOfRangeException(nameof(size), size, "Frame size must be >= 1.");
        if (hop < 1)
            throw new ArgumentOutOfRangeException(nameof(hop), hop, "Hop must be >= 1.");
        Size = size;
        Hop = hop;
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~FrameTests"
```

Expected PASS: all four cases green.

**Step 5 — Commit** (gitbutler skill — CLAUDE.md §1.4; create and mark the step branch here, then commit):

```bash
but status -fv                                   # inspect state; read the change IDs
but branch new step-02-audio-source
but mark step-02-audio-source
but commit step-02-audio-source \
  -m "feat(domain): Frame and FrameParameters value types" \
  --changes <ids from but status -fv> --status-after
```

---

### Task 2: `Framing.Split` — tiling by the declared hop (Domain)

**Files:**
- Create: `src/AudioClaudio.Domain/Framing.cs`
- Test: `tests/AudioClaudio.Tests/Domain/FramingProperties.cs`

**Step 1 — Write the failing test:**

```csharp
using System.Linq;
using AudioClaudio.Domain;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.Domain;

public class FramingProperties
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Split_with_hop_equal_to_size_tiles_the_buffer_exactly()
    {
        var rate = new SampleRate(8000);
        var samples = Enumerable.Range(0, 12).Select(i => i / 100f).ToArray();

        var frames = Framing.Split(samples, rate, new FrameParameters(4, 4));

        Assert.Equal(3, frames.Count);
        Assert.Equal(new long[] { 0, 4, 8 }, frames.Select(f => f.Start.Samples).ToArray());
        Assert.Equal(samples, frames.SelectMany(f => f.Samples).ToArray()); // no gaps, no overlap
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Split_zero_pads_a_short_final_frame()
    {
        var rate = new SampleRate(8000);
        var samples = new float[] { 1, 2, 3, 4, 5 };

        var frames = Framing.Split(samples, rate, new FrameParameters(4, 4));

        Assert.Equal(2, frames.Count);                          // starts 0 and 4
        Assert.Equal(new float[] { 1, 2, 3, 4 }, frames[0].Samples);
        Assert.Equal(new float[] { 5, 0, 0, 0 }, frames[1].Samples); // padded with zeros
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Frame_starts_advance_by_exactly_the_hop_and_cover_the_buffer()
    {
        var rate = new SampleRate(44100);

        // Property: in the gap-free regime (hop <= size), successive frame starts differ by
        // exactly the hop, the count is ceil(length/hop), and every frame has N samples.
        Gen.Select(Gen.Int[1, 64], Gen.Int[1, 64], Gen.Int[0, 400])
           .Where(t => t.Item2 <= t.Item1) // hop <= size
           .Sample(t =>
           {
               var (size, hop, length) = t;
               var samples = new float[length];
               var frames = Framing.Split(samples, rate, new FrameParameters(size, hop));

               long expectedCount = length == 0 ? 0 : (length - 1) / hop + 1; // ceil(length/hop)
               Assert.Equal(expectedCount, frames.Count);
               for (int i = 0; i < frames.Count; i++)
               {
                   Assert.Equal((long)i * hop, frames[i].Start.Samples);
                   Assert.Equal(size, frames[i].Samples.Length);
               }
           }, iter: 1000, seed: "0N0XnlbeE0O5");
        // Foundation testing convention "Fix CsCheck seeds for reproducibility": the run is pinned to
        // a fixed seed up front so every CI run is deterministic from the start. If a genuine failure
        // surfaces, CsCheck prints a reproduction seed — replace the value here with it to lock the case.
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~FramingProperties"
```

Expected FAILURE: compile error — `Framing` does not exist.

**Step 3 — Minimal implementation:**

`src/AudioClaudio.Domain/Framing.cs`:

```csharp
using System.Collections.Generic;

namespace AudioClaudio.Domain;

/// <summary>
/// Pure framing: slice a mono buffer into overlapping windows by the declared
/// <see cref="FrameParameters"/>. Deterministic, no I/O — the single place framing lives (R2.4).
/// </summary>
public static class Framing
{
    /// <param name="samples">Mono buffer (float, nominally [-1, 1]).</param>
    /// <param name="rate">Declared sample rate; stamped onto every frame's start position.</param>
    /// <param name="parameters">Window size N and hop H.</param>
    /// <param name="startSample">Sample index of <paramref name="samples"/>[0] in the wider stream.</param>
    /// <returns>
    /// One frame per hop position with start &lt; length. The final frame is zero-padded if it
    /// runs past the buffer, so every input sample appears in at least one frame.
    /// </returns>
    public static IReadOnlyList<Frame> Split(
        float[] samples, SampleRate rate, FrameParameters parameters, long startSample = 0)
    {
        if (samples is null) throw new ArgumentNullException(nameof(samples));
        if (startSample < 0)
            throw new ArgumentOutOfRangeException(nameof(startSample), startSample, "Start sample must be >= 0.");

        int n = parameters.Size;
        int h = parameters.Hop;
        long length = samples.LongLength;

        var frames = new List<Frame>();
        for (long start = 0; start < length; start += h)
        {
            var window = new float[n];
            long remaining = length - start;
            int count = (int)(remaining < n ? remaining : n);
            Array.Copy(samples, start, window, 0, count); // rest of `window` stays zero (padding)
            frames.Add(new Frame(window, new SamplePosition(startSample + start, rate)));
        }

        return frames;
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~FramingProperties"
```

Expected PASS: three tests green, including 1000 CsCheck iterations.

**Step 5 — Commit:**

```bash
but status -fv
but commit step-02-audio-source \
  -m "feat(domain): pure Framing splitter with hop/tiling property" \
  --changes <ids from but status -fv> --status-after
```

---

### Task 3: `IAudioSource` port + test seam (Application + test support) — R2.1

**Files:**
- Create: `src/AudioClaudio.Application/Ports/IAudioSource.cs`
- Create: `tests/AudioClaudio.Tests/TestSupport/InMemoryAudioSource.cs`
- Create: `tests/AudioClaudio.Tests/TestSupport/AudioSources.cs` *(the decision seam)*
- Test: `tests/AudioClaudio.Tests/Application/AudioSourcePortTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Application;

public class AudioSourcePortTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void InMemory_source_yields_positioned_in_range_frames()
    {
        var rate = new SampleRate(16000);
        var samples = new float[4096];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = MathF.Sin(i * 0.1f) * 0.5f; // deterministic, within [-0.5, 0.5]

        IAudioSource source = new InMemoryAudioSource(samples, rate, new FrameParameters(1024, 1024));

        var frames = AudioSources.Collect(source); // decision-agnostic collection seam
        Assert.Equal(4, frames.Count);
        Assert.Equal(16000, frames[0].Rate.Hz); // "declared sample rate" (R2.1) — carried on each frame
        Assert.Equal(0, frames[0].Start.Samples);
        Assert.Equal(1024, frames[1].Start.Samples); // each frame carries its starting SamplePosition
        foreach (var f in frames)
        {
            Assert.Equal(16000, f.Rate.Hz);
            foreach (var s in f.Samples)
                Assert.InRange(s, -1f, 1f); // mono float in [-1, 1]
        }
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~AudioSourcePortTests"
```

Expected FAILURE: compile error — `IAudioSource`, `InMemoryAudioSource`, and `AudioSources` do not exist.

**Step 3 — Minimal implementation:**

`src/AudioClaudio.Application/Ports/IAudioSource.cs` *(PULL shape — the recommended option; see the DECISION GATE):*

```csharp
using System.Collections.Generic;
using AudioClaudio.Domain;

namespace AudioClaudio.Application.Ports;

/// <summary>
/// A source of successive audio <see cref="Frame"/>s at a declared sample rate (R2.1).
/// The WAV file adapter is the first implementation; the live microphone (Step 10) is another,
/// behind this same port. "The microphone is just an adapter." The sample rate is not a separate
/// member: each <see cref="Frame"/> already carries its <see cref="Frame.Rate"/> (a position without
/// its rate is a bug), so consumers read the declared rate from the frames they pull.
/// </summary>
public interface IAudioSource
{
    /// <summary>Successive analysis frames, in order, each carrying its starting position and rate.</summary>
    IEnumerable<Frame> Frames { get; }
}
```

> **Under PUSH:** replace `IEnumerable<Frame> Frames { get; }` with a producer method such as `void Read(Action<Frame> onFrame);` (or a `Channel<Frame> Frames { get; }`). Only this interface and the `AudioSources.Collect` body below change; no test above or below touches the port shape directly.

`tests/AudioClaudio.Tests/TestSupport/InMemoryAudioSource.cs`:

```csharp
using System.Collections.Generic;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.TestSupport;

/// <summary>Trivial in-memory <see cref="IAudioSource"/>: frames a buffer via the Domain splitter.</summary>
public sealed class InMemoryAudioSource : IAudioSource
{
    private readonly IReadOnlyList<Frame> _frames;

    public IEnumerable<Frame> Frames => _frames;

    public InMemoryAudioSource(float[] samples, SampleRate rate, FrameParameters parameters)
    {
        _frames = Framing.Split(samples, rate, parameters);
    }
}
```

`tests/AudioClaudio.Tests/TestSupport/AudioSources.cs` *(the seam every test collects through):*

```csharp
using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.TestSupport;

/// <summary>
/// Collects an <see cref="IAudioSource"/> into a materialised list. This is the ONLY place the
/// frame-delivery decision (PULL vs PUSH) leaks into tests: under PULL it enumerates; under PUSH
/// it would run the producer and gather callback frames. Every WAV test collects through here so
/// the decision stays isolated behind this one method (DECISION GATE).
/// </summary>
public static class AudioSources
{
    public static IReadOnlyList<Frame> Collect(IAudioSource source) => source.Frames.ToList();

    // Under PUSH:
    //   var collected = new List<Frame>();
    //   source.Read(collected.Add);   // (or drain the Channel<Frame> to completion)
    //   return collected;
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~AudioSourcePortTests"
```

Expected PASS: the port, the in-memory adapter, and the collection seam compile and the frame contract holds.

**Step 5 — Commit:**

```bash
but status -fv
but commit step-02-audio-source \
  -m "feat(application): IAudioSource port with pull delivery seam" \
  --changes <ids from but status -fv> --status-after
```

---

### Task 4: `SignalGenerator` — pure sine + harmonic stack (test utility) — R2.3

**Files:**
- Create: `tests/AudioClaudio.Tests/Signals/SignalGenerator.cs`
- Test: `tests/AudioClaudio.Tests/Signals/SignalGeneratorTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Signals;

public class SignalGeneratorTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Sine_is_deterministic_bounded_and_correctly_shaped()
    {
        var rate = new SampleRate(48000);

        var a = SignalGenerator.Sine(440.0, 48000, rate, amplitude: 0.8);
        var b = SignalGenerator.Sine(440.0, 48000, rate, amplitude: 0.8);

        Assert.Equal(a, b);          // deterministic: identical output for identical input
        Assert.Equal(48000, a.Length);
        Assert.Equal(0f, a[0]);      // sin(0) == 0
        foreach (var s in a) Assert.InRange(s, -0.8f, 0.8f);

        int quarterPeriod = (int)(rate.Hz / 440.0 / 4); // ~ first peak
        Assert.True(a[quarterPeriod] > 0.7f);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void HarmonicStack_is_deterministic_bounded_and_differs_from_a_pure_sine()
    {
        var rate = new SampleRate(48000);

        var stack1 = SignalGenerator.HarmonicStack(220.0, 4800, rate, partials: 6, decay: 1.0, amplitude: 0.8);
        var stack2 = SignalGenerator.HarmonicStack(220.0, 4800, rate, partials: 6, decay: 1.0, amplitude: 0.8);
        var sine = SignalGenerator.Sine(220.0, 4800, rate, amplitude: 0.8);

        Assert.Equal(stack1, stack2);   // deterministic
        Assert.Equal(4800, stack1.Length);
        foreach (var s in stack1) Assert.InRange(s, -1f, 1f); // normalised to stay in range

        bool differsFromFundamental = false;
        for (int i = 0; i < stack1.Length; i++)
            if (MathF.Abs(stack1[i] - sine[i]) > 1e-4f) { differsFromFundamental = true; break; }
        Assert.True(differsFromFundamental); // partials genuinely change the waveform
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void HarmonicStack_can_be_rendered_to_frames()
    {
        var rate = new SampleRate(44100);
        var buffer = SignalGenerator.HarmonicStack(261.63, 2048, rate); // "rendered to frames" (R2.3)
        var frames = Framing.Split(buffer, rate, new FrameParameters(1024, 512));
        Assert.NotEmpty(frames);
        Assert.Equal(0, frames[0].Start.Samples);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~SignalGeneratorTests"
```

Expected FAILURE: compile error — `SignalGenerator` does not exist.

**Step 3 — Minimal implementation:**

`tests/AudioClaudio.Tests/Signals/SignalGenerator.cs`:

```csharp
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.Signals;

/// <summary>
/// Deterministic test signals (R2.3). No randomness: identical arguments produce identical samples,
/// so any fixture built on top is reproducible. Amplitudes are kept &lt; 1 so output stays in [-1, 1).
/// </summary>
public static class SignalGenerator
{
    /// <summary>A pure sine at <paramref name="frequencyHz"/>: x[i] = A·sin(2π f i / rate).</summary>
    public static float[] Sine(double frequencyHz, int sampleCount, SampleRate rate, double amplitude = 0.8)
    {
        if (frequencyHz <= 0) throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        if (sampleCount < 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));
        if (amplitude is < 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(amplitude), amplitude, "Keep amplitude in [0, 1) to stay within [-1, 1].");

        var buffer = new float[sampleCount];
        double step = 2.0 * Math.PI * frequencyHz / rate.Hz;
        for (int i = 0; i < sampleCount; i++)
            buffer[i] = (float)(amplitude * Math.Sin(step * i));
        return buffer;
    }

    /// <summary>
    /// A harmonic stack: the fundamental plus <paramref name="partials"/> overtones at integer
    /// multiples of the fundamental, the k-th partial scaled by 1/k^<paramref name="decay"/>
    /// (piano-like roll-off). Normalised by the sum of coefficients so the peak stays &lt; amplitude &lt; 1.
    /// </summary>
    public static float[] HarmonicStack(
        double fundamentalHz, int sampleCount, SampleRate rate,
        int partials = 6, double decay = 1.0, double amplitude = 0.8)
    {
        if (fundamentalHz <= 0) throw new ArgumentOutOfRangeException(nameof(fundamentalHz));
        if (sampleCount < 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));
        if (partials < 1) throw new ArgumentOutOfRangeException(nameof(partials));
        if (amplitude is < 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(amplitude), amplitude, "Keep amplitude in [0, 1) to stay within [-1, 1].");

        double norm = 0.0;
        for (int k = 1; k <= partials; k++) norm += 1.0 / Math.Pow(k, decay);

        var buffer = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            double acc = 0.0;
            for (int k = 1; k <= partials; k++)
            {
                double coeff = 1.0 / Math.Pow(k, decay);
                double phase = 2.0 * Math.PI * (k * fundamentalHz) * i / rate.Hz;
                acc += coeff * Math.Sin(phase);
            }
            buffer[i] = (float)(amplitude * acc / norm); // |acc/norm| <= 1  =>  |sample| <= amplitude
        }
        return buffer;
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~SignalGeneratorTests"
```

Expected PASS: sine and stack are deterministic, bounded, and correctly shaped.

**Step 5 — Commit:**

```bash
but status -fv
but commit step-02-audio-source \
  -m "test(signals): deterministic sine and harmonic-stack generator" \
  --changes <ids from but status -fv> --status-after
```

---

### Task 5: `WavWriter` — render buffers to PCM WAV bytes (test utility) — R2.3

This completes "rendered ... to WAV" (R2.3) and gives the WAV adapter something to read. It lives in the test utilities because Step 2 needs WAV *writing* only for fixtures; the production WAV writer (`WavFileWriter`, Step 8, Infrastructure) is a distinct later concern (a seam left for Step 8's `render` command). The `WriteMonoFile` convenience added here is what Steps 9/10 use to write generated fixtures straight to disk.

**Files:**
- Create: `tests/AudioClaudio.Tests/Signals/WavWriter.cs`
- Test: `tests/AudioClaudio.Tests/Signals/WavWriterTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Signals;

public class WavWriterTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Write16_emits_a_canonical_pcm_riff_header()
    {
        var rate = new SampleRate(44100);
        byte[] bytes = WavWriter.Write16(new float[] { 0f, 0.5f, -0.5f, 0.25f }, rate);

        Assert.Equal("RIFF", Ascii(bytes, 0));
        Assert.Equal("WAVE", Ascii(bytes, 8));
        Assert.Equal("fmt ", Ascii(bytes, 12));
        Assert.Equal(16, I32(bytes, 16));     // PCM fmt chunk size
        Assert.Equal(1, I16(bytes, 20));      // audio format 1 == PCM
        Assert.Equal(1, I16(bytes, 22));      // channels
        Assert.Equal(44100, I32(bytes, 24));  // sample rate
        Assert.Equal(16, I16(bytes, 34));     // bits per sample
        Assert.Equal("data", Ascii(bytes, 36));
        Assert.Equal(4 * 2, I32(bytes, 40));  // data bytes: 4 samples * 2 bytes
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Write16Stereo_records_two_channels_and_interleaved_data()
    {
        var rate = new SampleRate(48000);
        byte[] bytes = WavWriter.Write16Stereo(new float[] { 0.5f, 0.25f }, new float[] { -0.5f, 0f }, rate);

        Assert.Equal(2, I16(bytes, 22));      // channels
        Assert.Equal(2 * 2, I16(bytes, 32));  // block align: channels * bytesPerSample
        Assert.Equal(2 * 2 * 2, I32(bytes, 40)); // data bytes: 2 frames * 2 ch * 2 bytes
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void WriteMonoFile_writes_the_same_bytes_as_Write16()
    {
        var rate = new SampleRate(22050);
        var mono = new float[] { 0f, 0.5f, -0.5f, 0.25f };
        string path = Path.Combine(Path.GetTempPath(), $"audioclaudio-{Guid.NewGuid():N}.wav");
        try
        {
            WavWriter.WriteMonoFile(path, mono, rate);
            Assert.Equal(WavWriter.Write16(mono, rate), File.ReadAllBytes(path)); // thin wrapper over Write16
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string Ascii(byte[] b, int off) => Encoding.ASCII.GetString(b, off, 4);
    private static int I32(byte[] b, int off) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(off, 4));
    private static short I16(byte[] b, int off) => BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(off, 2));
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~WavWriterTests"
```

Expected FAILURE: compile error — `WavWriter` does not exist.

**Step 3 — Minimal implementation:**

`tests/AudioClaudio.Tests/Signals/WavWriter.cs`:

```csharp
using System.IO;
using System.Text;
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.Signals;

/// <summary>
/// Serialises mono/stereo float buffers to canonical little-endian PCM WAV bytes (R2.3).
/// PCM convention (see plan Approach): q = round(x · 2^(bits-1)), clamped to the signed range;
/// the reader divides by the same 2^(bits-1), so the round-trip is exact on the quantisation grid.
/// </summary>
public static class WavWriter
{
    public static byte[] Write16(float[] mono, SampleRate rate) => Write(new[] { mono }, rate, 16);

    public static byte[] Write24(float[] mono, SampleRate rate) => Write(new[] { mono }, rate, 24);

    public static byte[] Write16Stereo(float[] left, float[] right, SampleRate rate)
    {
        if (left.Length != right.Length) throw new ArgumentException("Channel lengths must match.");
        return Write(new[] { left, right }, rate, 16);
    }

    /// <summary>Convenience for Steps 9/10: render a mono buffer straight to a .wav file on disk —
    /// a thin <c>File.WriteAllBytes</c> over <see cref="Write16"/>.</summary>
    public static void WriteMonoFile(string path, float[] mono, SampleRate rate)
        => File.WriteAllBytes(path, Write16(mono, rate));

    private static byte[] Write(float[][] channels, SampleRate rate, int bitsPerSample)
    {
        int channelCount = channels.Length;
        int frameCount = channels[0].Length;
        int bytesPerSample = bitsPerSample / 8;
        int blockAlign = channelCount * bytesPerSample;
        int dataBytes = frameCount * blockAlign;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms); // BinaryWriter is little-endian on every platform

        w.Write(Ascii("RIFF"));
        w.Write(36 + dataBytes);        // "WAVE"(4) + fmt chunk(8+16) + data header(8) + data
        w.Write(Ascii("WAVE"));

        w.Write(Ascii("fmt "));
        w.Write(16);                    // PCM fmt chunk size
        w.Write((short)1);              // audio format 1 == PCM
        w.Write((short)channelCount);
        w.Write(rate.Hz);
        w.Write(rate.Hz * blockAlign); // byte rate
        w.Write((short)blockAlign);
        w.Write((short)bitsPerSample);

        w.Write(Ascii("data"));
        w.Write(dataBytes);

        double scale = System.Math.Pow(2, bitsPerSample - 1); // 32768 or 8388608
        int max = (int)scale - 1;
        int min = -(int)scale;

        for (int i = 0; i < frameCount; i++)
            for (int c = 0; c < channelCount; c++)
            {
                int q = (int)System.Math.Round(channels[c][i] * scale);
                if (q > max) q = max;
                if (q < min) q = min;

                if (bitsPerSample == 16)
                {
                    w.Write((short)q);
                }
                else // 24-bit: low three bytes, little-endian
                {
                    w.Write((byte)(q & 0xFF));
                    w.Write((byte)((q >> 8) & 0xFF));
                    w.Write((byte)((q >> 16) & 0xFF));
                }
            }

        w.Flush();
        return ms.ToArray();
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~WavWriterTests"
```

Expected PASS: header fields and data sizes are correct for mono and stereo.

**Step 5 — Commit:**

```bash
but status -fv
but commit step-02-audio-source \
  -m "test(signals): WavWriter renders buffers to PCM WAV bytes" \
  --changes <ids from but status -fv> --status-after
```

---

### Task 6: `WavAudioSource` — hand-rolled RIFF reader, mono 16-bit (Infrastructure) — R2.2

**Files:**
- Create: `src/AudioClaudio.Infrastructure/Audio/WavAudioSource.cs`
- Create: `tests/AudioClaudio.Tests/TestSupport/FrameAssert.cs`
- Test: `tests/AudioClaudio.Tests/Infrastructure/WavAudioSource16Tests.cs`

**Step 1 — Write the failing test:**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Tests.Signals;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class WavAudioSource16Tests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Parses_a_known_mono_16bit_wav_into_expected_samples()
    {
        var rate = new SampleRate(8000);
        // +0.5 and -0.5 quantise to q = round(x*32768) = 16384 and -16384.
        byte[] bytes = WavWriter.Write16(new float[] { 0.5f, -0.5f }, rate);

        using var ms = new MemoryStream(bytes);
        var source = new WavAudioSource(ms, new FrameParameters(2, 2));

        var frames = AudioSources.Collect(source);
        Assert.Single(frames);
        Assert.Equal(8000, frames[0].Rate.Hz); // declared sample rate, carried on each frame
        Assert.Equal(16384 / 32768f, frames[0].Samples[0]);
        Assert.Equal(-16384 / 32768f, frames[0].Samples[1]);
        Assert.Equal(0, frames[0].Start.Samples);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Sine_round_trips_through_16bit_wav_exactly()
    {
        var rate = new SampleRate(44100);
        int n = 2048;
        var buffer = SignalGenerator.Sine(440.0, n * 20, rate); // exact multiple of N: no padding
        byte[] bytes = WavWriter.Write16(buffer, rate);

        // Expected = the same buffer put through the PCM convention, then framed.
        var expected = Framing.Split(Requantize16(buffer), rate, new FrameParameters(n, n));

        using var ms = new MemoryStream(bytes);
        var actual = AudioSources.Collect(new WavAudioSource(ms, new FrameParameters(n, n)));

        FrameAssert.Equal(expected, actual); // bit-exact
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void FromFile_reads_the_same_frames_and_releases_the_file_on_dispose()
    {
        var rate = new SampleRate(8000);
        byte[] bytes = WavWriter.Write16(new float[] { 0.5f, -0.5f }, rate);
        string path = Path.Combine(Path.GetTempPath(), $"audioclaudio-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, bytes);
        try
        {
            IReadOnlyList<Frame> frames;
            using (var source = WavAudioSource.FromFile(path, new FrameParameters(2, 2)))
            {
                frames = AudioSources.Collect(source);
            }

            Assert.Single(frames);
            Assert.Equal(16384 / 32768f, frames[0].Samples[0]);
            Assert.Equal(-16384 / 32768f, frames[0].Samples[1]);

            // FromFile OWNS the stream it opened and closes it on Dispose, so the file is now deletable
            // (load-bearing on Windows, where a leaked handle would lock the file; harmless elsewhere).
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Dispose_leaves_a_caller_supplied_stream_open()
    {
        var rate = new SampleRate(8000);
        byte[] bytes = WavWriter.Write16(new float[] { 0.5f, -0.5f }, rate);

        var ms = new MemoryStream(bytes);
        var source = new WavAudioSource(ms, new FrameParameters(2, 2));
        source.Dispose();

        Assert.True(ms.CanRead); // a stream passed to the ctor stays the caller's to close
    }

    private static float[] Requantize16(float[] x)
    {
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            int q = (int)System.Math.Round(x[i] * 32768.0);
            if (q > 32767) q = 32767;
            if (q < -32768) q = -32768;
            y[i] = (float)(q / 32768.0);
        }
        return y;
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~WavAudioSource16Tests"
```

Expected FAILURE: compile error — `WavAudioSource` and `FrameAssert` do not exist.

**Step 3 — Minimal implementation:**

`tests/AudioClaudio.Tests/TestSupport/FrameAssert.cs`:

```csharp
using System.Collections.Generic;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.TestSupport;

/// <summary>Structural, bit-exact frame comparison (Frame has no value equality by design).</summary>
public static class FrameAssert
{
    public static void Equal(Frame expected, Frame actual)
    {
        Assert.Equal(expected.Start.Samples, actual.Start.Samples);
        Assert.Equal(expected.Rate.Hz, actual.Rate.Hz);
        Assert.Equal(expected.Samples, actual.Samples); // element-wise, exact for float[]
    }

    public static void Equal(IReadOnlyList<Frame> expected, IReadOnlyList<Frame> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++) Equal(expected[i], actual[i]);
    }
}
```

`src/AudioClaudio.Infrastructure/Audio/WavAudioSource.cs`:

```csharp
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;

namespace AudioClaudio.Infrastructure.Audio;

/// <summary>
/// Hand-rolled 16/24-bit PCM WAV adapter (R2.2). Walks the RIFF chunk list (chunks may appear in
/// any order), downmixes multichannel to mono by averaging, and frames the result via the Domain
/// splitter. All reads are little-endian. No dependency is taken for parsing. Disposable: it owns
/// (and closes) the stream only when it opened it itself via <see cref="FromFile"/>.
/// </summary>
public sealed class WavAudioSource : IAudioSource, IDisposable
{
    private readonly IReadOnlyList<Frame> _frames;
    private readonly Stream? _ownedStream;

    public IEnumerable<Frame> Frames => _frames;

    public WavAudioSource(Stream wav, FrameParameters parameters)
        : this(wav, parameters, ownsStream: false)
    {
    }

    private WavAudioSource(Stream wav, FrameParameters parameters, bool ownsStream)
    {
        if (wav is null) throw new ArgumentNullException(nameof(wav));
        var (mono, rate) = Decode(wav);
        _frames = Framing.Split(mono, rate, parameters);
        _ownedStream = ownsStream ? wav : null; // a caller-passed stream stays the caller's to close
    }

    /// <summary>Convenience for the composition root: open a file and read it. The returned source
    /// OWNS the opened stream and closes it on <see cref="Dispose"/>.</summary>
    public static WavAudioSource FromFile(string path, FrameParameters parameters)
        => new WavAudioSource(File.OpenRead(path), parameters, ownsStream: true);

    /// <summary>Closes the stream this source opened itself (via <see cref="FromFile"/>); a stream
    /// handed to the public constructor is owned by the caller and left open.</summary>
    public void Dispose() => _ownedStream?.Dispose();

    private static (float[] mono, SampleRate rate) Decode(Stream stream)
    {
        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            stream.CopyTo(ms);       // MVP WAV fixtures are small; read whole then parse
            bytes = ms.ToArray();
        }
        var span = new ReadOnlySpan<byte>(bytes);

        if (span.Length < 12 || !Matches(span.Slice(0, 4), "RIFF") || !Matches(span.Slice(8, 4), "WAVE"))
            throw new InvalidDataException("Not a RIFF/WAVE file.");

        int audioFormat = 0, channels = 0, sampleRate = 0, bitsPerSample = 0;
        ReadOnlySpan<byte> data = default;
        bool haveFmt = false, haveData = false;

        int pos = 12; // past "RIFF" <size> "WAVE"
        while (pos + 8 <= span.Length)
        {
            var id = span.Slice(pos, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 4, 4));
            int body = pos + 8;
            if (size < 0 || body + size > span.Length)
                size = span.Length - body; // tolerate a bad/streaming RIFF size on the final chunk

            if (Matches(id, "fmt "))
            {
                var f = span.Slice(body, size);
                audioFormat   = BinaryPrimitives.ReadInt16LittleEndian(f.Slice(0, 2));
                channels      = BinaryPrimitives.ReadInt16LittleEndian(f.Slice(2, 2));
                sampleRate    = BinaryPrimitives.ReadInt32LittleEndian(f.Slice(4, 4));
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(f.Slice(14, 2));
                haveFmt = true;
            }
            else if (Matches(id, "data"))
            {
                data = span.Slice(body, size);
                haveData = true;
            }
            // else: skip unknown chunk (LIST, fact, ...)

            pos = body + size + (size & 1); // chunks are word-aligned: pad byte after an odd size
        }

        if (!haveFmt) throw new InvalidDataException("WAV is missing its 'fmt ' chunk.");
        if (!haveData) throw new InvalidDataException("WAV is missing its 'data' chunk.");
        if (audioFormat != 1)
            throw new NotSupportedException($"Only PCM (format 1) is supported; got audio format {audioFormat}.");
        if (bitsPerSample != 16 && bitsPerSample != 24)
            throw new NotSupportedException($"Only 16-bit and 24-bit PCM are supported; got {bitsPerSample}-bit.");
        if (channels < 1)
            throw new InvalidDataException($"Channel count must be >= 1; got {channels}.");

        int bytesPerSample = bitsPerSample / 8;
        int blockAlign = channels * bytesPerSample;
        int frameCount = data.Length / blockAlign;
        double scale = System.Math.Pow(2, bitsPerSample - 1); // 32768 or 8388608

        var mono = new float[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            int baseOffset = i * blockAlign;
            double sum = 0.0;
            for (int c = 0; c < channels; c++)
            {
                int off = baseOffset + c * bytesPerSample;
                int sample = bitsPerSample == 16
                    ? BinaryPrimitives.ReadInt16LittleEndian(data.Slice(off, 2))
                    : ReadInt24LittleEndian(data.Slice(off, 3));
                sum += sample / scale;
            }
            mono[i] = (float)(sum / channels); // downmix = arithmetic mean of channels
        }

        return (mono, new SampleRate(sampleRate));
    }

    private static int ReadInt24LittleEndian(ReadOnlySpan<byte> b)
    {
        int v = b[0] | (b[1] << 8) | (b[2] << 16);
        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000); // sign-extend the 24th bit
        return v;
    }

    private static bool Matches(ReadOnlySpan<byte> b, string ascii)
        => b.Length == ascii.Length
        && b[0] == ascii[0] && b[1] == ascii[1] && b[2] == ascii[2] && b[3] == ascii[3];
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~WavAudioSource16Tests"
```

Expected PASS: the known WAV parses to `±0.5`, and the 20-frame sine round-trips bit-exactly.

**Step 5 — Commit:**

```bash
but status -fv
but commit step-02-audio-source \
  -m "feat(infra): hand-rolled WAV adapter, mono 16-bit PCM" \
  --changes <ids from but status -fv> --status-after
```

---

### Task 7: 24-bit PCM support — R2.2

The 24-bit read path already exists in Task 6's adapter; this task proves it with an exact round-trip.

**Files:**
- Test: `tests/AudioClaudio.Tests/Infrastructure/WavAudioSource24Tests.cs`
- Modify (only if the property below is red): `src/AudioClaudio.Infrastructure/Audio/WavAudioSource.cs`

**Step 1 — Write the failing test:**

```csharp
using System.IO;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Tests.Signals;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class WavAudioSource24Tests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void HarmonicStack_round_trips_through_24bit_wav_exactly()
    {
        var rate = new SampleRate(44100);
        int n = 1024;
        var buffer = SignalGenerator.HarmonicStack(220.0, n * 16, rate, partials: 5, decay: 1.0);
        byte[] bytes = WavWriter.Write24(buffer, rate);

        var expected = Framing.Split(Requantize24(buffer), rate, new FrameParameters(n, n));

        using var ms = new MemoryStream(bytes);
        var actual = AudioSources.Collect(new WavAudioSource(ms, new FrameParameters(n, n)));

        FrameAssert.Equal(expected, actual);
    }

    private static float[] Requantize24(float[] x)
    {
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            int q = (int)System.Math.Round(x[i] * 8388608.0);
            if (q > 8388607) q = 8388607;
            if (q < -8388608) q = -8388608;
            y[i] = (float)(q / 8388608.0);
        }
        return y;
    }
}
```

**Step 2 — Run to verify it fails (or confirm the path):**

```bash
dotnet test --filter "FullyQualifiedName~WavAudioSource24Tests"
```

Expected: RED if the 24-bit read/sign-extension is wrong. If Task 6's adapter is already correct this is green immediately — that is fine; the test's job is to *lock in* 24-bit behaviour against regression (Section 5). If it is red, debug the sign-extension with @superpowers:systematic-debugging; do not weaken the assertion.

**Step 3 — Minimal implementation:**

No new code is expected — the 24-bit branch and `ReadInt24LittleEndian` were written in Task 6. Only touch `WavAudioSource.cs` if this test is red.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~WavAudioSource24Tests"
```

Expected PASS: 16 frames of the harmonic stack round-trip bit-exactly through 24-bit PCM.

**Step 5 — Commit:**

```bash
but status -fv
but commit step-02-audio-source \
  -m "test(infra): 24-bit PCM round-trip for the WAV adapter" \
  --changes <ids from but status -fv> --status-after
```

---

### Task 8: Stereo downmix is the sample mean — R2.2

**Files:**
- Test: `tests/AudioClaudio.Tests/Infrastructure/WavAudioSourceStereoTests.cs`
- Modify (only if red): `src/AudioClaudio.Infrastructure/Audio/WavAudioSource.cs`

**Step 1 — Write the failing test:**

```csharp
using System.IO;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Tests.Signals;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class WavAudioSourceStereoTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Stereo_downmix_is_the_sample_mean()
    {
        var rate = new SampleRate(44100);
        var left  = new float[] { 0.5f, 0.25f, -0.75f, 0f };
        var right = new float[] { -0.5f, 0.75f, 0.5f, 0.2f };
        byte[] bytes = WavWriter.Write16Stereo(left, right, rate);

        using var ms = new MemoryStream(bytes);
        var frames = AudioSources.Collect(new WavAudioSource(ms, new FrameParameters(4, 4)));
        Assert.Single(frames);

        for (int i = 0; i < 4; i++)
        {
            double ql = QuantizeDequantize16(left[i]);
            double qr = QuantizeDequantize16(right[i]);
            float expected = (float)((ql + qr) / 2); // adapter computes sum/channels
            Assert.Equal(expected, frames[0].Samples[i]);
        }
    }

    private static double QuantizeDequantize16(float x)
    {
        int q = (int)System.Math.Round(x * 32768.0);
        if (q > 32767) q = 32767;
        if (q < -32768) q = -32768;
        return q / 32768.0;
    }
}
```

**Step 2 — Run to verify it fails (or confirm the path):**

```bash
dotnet test --filter "FullyQualifiedName~WavAudioSourceStereoTests"
```

Expected: RED if downmix is wrong (e.g. taking only the first channel, or summing without dividing). Green immediately if Task 6's mean is already correct — the test locks the "downmix is the mean" invariant from the Verify section.

**Step 3 — Minimal implementation:**

No new code expected — `mono[i] = (float)(sum / channels)` in Task 6 already averages. Touch the adapter only if red.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~WavAudioSourceStereoTests"
```

Expected PASS: every mono sample equals the mean of the two quantised channels, bit-exactly.

**Step 5 — Commit:**

```bash
but status -fv
but commit step-02-audio-source \
  -m "test(infra): stereo downmix equals the sample mean" \
  --changes <ids from but status -fv> --status-after
```

---

### Task 9: RIFF robustness — unordered/extra chunks and rejecting unsupported formats — R2.2

**Files:**
- Test: `tests/AudioClaudio.Tests/Infrastructure/WavAudioSourceRobustnessTests.cs`
- Modify (only if red): `src/AudioClaudio.Infrastructure/Audio/WavAudioSource.cs`

**Step 1 — Write the failing test:**

```csharp
using System.IO;
using System.Text;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class WavAudioSourceRobustnessTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Skips_unknown_chunks_between_fmt_and_data()
    {
        var rate = new SampleRate(8000);
        // data for 16384 (+0.5) and -16384 (-0.5) as little-endian int16.
        byte[] data = { 0x00, 0x40, 0x00, 0xC0 };
        byte[] listBody = { (byte)'I', (byte)'N', (byte)'F', (byte)'O', 1, 2, 3, 4 };
        byte[] wav = BuildWav(channels: 1, rate: 8000, bits: 16, data: data,
                              extra: ("LIST", listBody));

        using var ms = new MemoryStream(wav);
        var frames = AudioSources.Collect(new WavAudioSource(ms, new FrameParameters(2, 2)));

        Assert.Single(frames);
        Assert.Equal(16384 / 32768f, frames[0].Samples[0]);
        Assert.Equal(-16384 / 32768f, frames[0].Samples[1]);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Parses_when_the_data_chunk_precedes_the_fmt_chunk()
    {
        // Proves the "any order" claim: the reader walks the chunk list, so data-before-fmt parses.
        var rate = new SampleRate(8000);
        byte[] data = { 0x00, 0x40, 0x00, 0xC0 }; // 16384 (+0.5), -16384 (-0.5) as little-endian int16
        byte[] wav = BuildWav(channels: 1, rate: 8000, bits: 16, data: data, dataFirst: true);

        using var ms = new MemoryStream(wav);
        var frames = AudioSources.Collect(new WavAudioSource(ms, new FrameParameters(2, 2)));

        Assert.Single(frames);
        Assert.Equal(16384 / 32768f, frames[0].Samples[0]);
        Assert.Equal(-16384 / 32768f, frames[0].Samples[1]);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Rejects_non_riff_input()
    {
        using var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 });
        Assert.Throws<InvalidDataException>(() => new WavAudioSource(ms, new FrameParameters(2, 2)));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Rejects_unsupported_bit_depth()
    {
        // 8-bit PCM is valid RIFF but unsupported by this adapter — fail fast, descriptively.
        byte[] wav = BuildWav(channels: 1, rate: 8000, bits: 8, data: new byte[] { 128, 128 });
        using var ms = new MemoryStream(wav);
        Assert.Throws<NotSupportedException>(() => new WavAudioSource(ms, new FrameParameters(2, 2)));
    }

    /// <summary>Builds a minimal WAV with fmt, an optional extra chunk, then data; patches the RIFF size.
    /// With <paramref name="dataFirst"/> the data chunk is emitted before fmt, to prove chunk-order robustness.</summary>
    private static byte[] BuildWav(int channels, int rate, int bits, byte[] data,
                                   (string id, byte[] body)? extra = null, bool dataFirst = false)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int blockAlign = channels * (bits / 8);

        void Chunk(string id, byte[] body)
        {
            w.Write(Encoding.ASCII.GetBytes(id));
            w.Write(body.Length);
            w.Write(body);
            if ((body.Length & 1) == 1) w.Write((byte)0); // word-align
        }

        byte[] fmtBody;
        using (var fmt = new MemoryStream())
        using (var fw = new BinaryWriter(fmt))
        {
            fw.Write((short)1);              // PCM
            fw.Write((short)channels);
            fw.Write(rate);
            fw.Write(rate * blockAlign);
            fw.Write((short)blockAlign);
            fw.Write((short)bits);
            fw.Flush();
            fmtBody = fmt.ToArray();
        }

        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        long sizePos = ms.Position;
        w.Write(0); // patched below
        w.Write(Encoding.ASCII.GetBytes("WAVE"));

        if (dataFirst)
        {
            Chunk("data", data);   // data before fmt: legal RIFF, must still parse
            Chunk("fmt ", fmtBody);
        }
        else
        {
            Chunk("fmt ", fmtBody);
            if (extra is { } e) Chunk(e.id, e.body);
            Chunk("data", data);
        }

        w.Flush();
        long end = ms.Position;
        ms.Position = sizePos;
        w.Write((int)(end - 8));
        w.Flush();
        return ms.ToArray();
    }
}
```

**Step 2 — Run to verify it fails (or confirm the path):**

```bash
dotnet test --filter "FullyQualifiedName~WavAudioSourceRobustnessTests"
```

Expected: the chunk-walking loop and the format guards from Task 6 should make these green. If the reader assumed a fixed fmt-then-data layout, `Skips_unknown_chunks_between_fmt_and_data` goes RED — fix by walking chunks (already done in Task 6). If any guard is missing, the rejection tests go RED.

**Step 3 — Minimal implementation:**

No new code expected — chunk-walking and the `InvalidDataException`/`NotSupportedException` guards were written in Task 6. Touch the adapter only if a case is red.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~WavAudioSourceRobustnessTests"
```

Expected PASS: an interleaved `LIST` chunk is skipped; non-RIFF and 8-bit inputs are rejected with descriptive exceptions.

**Step 5 — Commit (this completes Step 2 — use the spec's umbrella message):**

```bash
dotnet build && dotnet test               # full suite green
dotnet test --filter Category=Fast        # fast filter still green
dotnet format                             # no formatting diff
but status -fv
but commit step-02-audio-source \
  -m "feat: IAudioSource port, WAV adapter, deterministic signal generator" \
  --changes <ids from but status -fv> --status-after
```

Then record the frame-delivery resolution in `DECISIONS.md` (see Definition of Done) and update the "Where the project is right now" note in `CLAUDE.md` to point at Step 3.

---

## Verify (step exit criteria)

Restating Section 6 Step 2's *Verify* bullets as checkable outcomes:

- [ ] **A generated 1 s (multiple-of-N) sine WAV read back through the adapter equals the generator's frames exactly** — `WavAudioSource16Tests.Sine_round_trips_through_16bit_wav_exactly` (and the 24-bit analogue in `WavAudioSource24Tests`).
- [ ] **Downmix of a stereo file is the sample mean** — `WavAudioSourceStereoTests.Stereo_downmix_is_the_sample_mean`.
- [ ] **Frames tile the input with the declared hop and no gaps** — `FramingProperties.Split_with_hop_equal_to_size_tiles_the_buffer_exactly` and `FramingProperties.Frame_starts_advance_by_exactly_the_hop_and_cover_the_buffer` (1000 CsCheck cases).
- [ ] Deterministic signal generator (pure sine + harmonic stack) exists in test utilities and renders to frames and to WAV — `SignalGeneratorTests.*`, `WavWriterTests.*`.

## Definition of Done

- [ ] `dotnet build` succeeds; `dotnet test` is fully green; `dotnet test --filter Category=Fast` is green (all Step 2 tests are `Fast`).
- [ ] `dotnet format` reports no changes.
- [ ] Every requirement in the coverage table (R2.1–R2.4) is satisfied by the mapped, passing tests.
- [ ] **Dependency rule intact:** `Frame`/`FrameParameters`/`Framing` reference only the BCL (Domain); `IAudioSource` references Domain only (Application); `WavAudioSource` references Application + Domain (Infrastructure); no clock is read and no I/O occurs in Domain.
- [ ] **`DECISIONS.md` updated** with the frame-delivery decision (PULL vs PUSH) once Cornelius rules — the option chosen, the reason, and (if PULL) the note that Step 10 adds a bounded-channel bridge. No NuGet package was added this step, so there is no new license row; optionally record the PCM quantisation convention (scale `2^(bits-1)`, little-endian, round-to-nearest with clamp) as an implementation note.
- [ ] Committed via GitButler on branch `step-02-audio-source`; the completing commit carries the spec message `feat: IAudioSource port, WAV adapter, deterministic signal generator` (finer per-task commits roll up to it).
- [ ] `CLAUDE.md` "Where the project is right now" note advanced to Step 3.
