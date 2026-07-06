# Step 10 — Live Microphone Capture — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Build-spec step:** Section 6 Step 10 (R10.1, R10.2, R10.3, R10.4)
**Goal:** Add the microphone as one more `IAudioSource` adapter over PortAudioSharp2 — delivering the exact same `Frame` contract as the WAV adapter — and a `listen` CLI command that prints notes live and writes the session's output on stop, with no transcription logic living in the capture code.
**Architecture:** The capture adapter lives in `AudioClaudio.Infrastructure` behind the Application-layer `IAudioSource` port; its device-independent core (downmix + reframing + a bounded-channel bridge) is a testable Infrastructure class, and only a thin PortAudio P/Invoke shell is untestable-in-CI. The `listen` orchestration is an Application use case wired to concrete adapters in the `AudioClaudio.Cli` composition root — the one place adapters are constructed.
**Tech Stack:** PortAudioSharp2 (MIT) for device capture; `System.Threading.Channels` (BCL) for the real-time-safe producer/consumer bridge; the already-built domain pipeline (Steps 3–5), quantizer (Step 6), MIDI writer (Step 7) and transcriber (Step 9); xUnit + CsCheck for tests.
**Prerequisites:** Steps 0–9 green and committed (Section 1 rule 3). Specifically: Step 2's `IAudioSource` port (the `Frames` property) + `Frame` type + `WavAudioSource`; Step 6's `Quantizer`/`QuantizationGrid`/`TimeSignature`/`Score`; Step 7's `IScoreWriter`/`INoteEventWriter` + `DryWetMidiWriter`; Step 9's `TranscriptionPipeline` (`ITranscriber` + `StreamNotes`).
**Commit (spec):** `feat(infra): live capture adapter and listen command`

---

## ⚠ Flagged spec tension — MusicXML in R10.3 depends on Step 11

R10.3 says `listen` SHALL, on stop, write **raw MIDI, quantized MIDI, and MusicXML**. But the MusicXML writer is a **Step 11** deliverable, and Section 1 rule 3 forbids implementing a future step's work now. This is an internal ordering tension in the build spec (Step 10 precedes Step 11 yet lists Step 11's output). The constitution wins, so we do not skip ahead.

**Resolution (no new scope):** In Step 10, `listen` writes `raw.mid` and `score.mid` (fully supported by Step 7) and is structurally wired to write `score.musicxml` through an **optional injected `IScoreWriter`** that is `null` until Step 11 registers the real MusicXML adapter. The MusicXML *wiring and its test* land here (proving the seam with a spy writer); the MusicXML *adapter* lands in Step 11, at which point the composition root passes it and `listen` emits the file automatically — with **zero change to `listen`**. This is the "one line noting a seam for a named future step" the Foundation explicitly permits. R10.3's MIDI outputs and the live-print behavior are fully satisfied and tested in this step.

## Prior-step surfaces (pinned by CONTRACTS.md)

These shapes are fixed by `docs/plans/CONTRACTS.md` (the authoritative cross-step contract); this step consumes them verbatim.

```csharp
// Application ports — namespace AudioClaudio.Application.Ports
public interface IAudioSource   { IEnumerable<Frame> Frames { get; } }                 // PULL model — a PROPERTY, not a method
public interface ITranscriber   { TranscriptionResult Transcribe(IAudioSource source); }
public interface IScoreWriter    { void Write(Score score, Stream destination); }
public interface INoteEventWriter { void Write(IReadOnlyList<NoteEvent> events, Tempo tempo, Stream destination); }

// Application services — namespace AudioClaudio.Application
public sealed record TranscriptionSettings
{ public int FrameSize { get; init; } public int Hop { get; init; }
  public static TranscriptionSettings ForTempo(double bpm); /* + YIN/onset/segmenter knobs */ }
public sealed record TranscriptionResult(Score Score, IReadOnlyList<NoteEvent> RawEvents);
public sealed class TranscriptionPipeline : ITranscriber
{
    public TranscriptionPipeline(TranscriptionSettings settings, IFourierTransform fft);  // FFT injected
    public TranscriptionResult Transcribe(IAudioSource source);
    public IEnumerable<NoteEvent> StreamNotes(IAudioSource source);                        // incremental live feed
}

// Domain — namespace AudioClaudio.Domain (FFT in AudioClaudio.Domain.Spectral)
public sealed class Frame { public float[] Samples { get; } public SamplePosition Start { get; }
                            public SampleRate Rate => Start.Rate; public Frame(float[] samples, SamplePosition start); }  // 2-arg; Rate derived
public readonly record struct FrameParameters { public FrameParameters(int size, int hop); }
public static class Quantizer { public static Score Quantize(IReadOnlyList<NoteEvent> events, QuantizationGrid grid); }
// QuantizationGrid(SampleRate, Tempo, TimeSignature, Subdivision); Score.Tempo is a Tempo (Tempo.BeatsPerMinute).

// Infrastructure — AudioClaudio.Infrastructure.Audio / .Midi / .MusicXml
public sealed class WavAudioSource : IAudioSource, IDisposable
{ public static WavAudioSource FromFile(string path, FrameParameters parameters); public IEnumerable<Frame> Frames { get; } }
public sealed class DryWetMidiWriter : IScoreWriter, INoteEventWriter { }   // Stream-based (Step 7)
public sealed class MusicXmlScoreWriter : IScoreWriter { }                  // Stream-based (Step 11)
```

There is **no** `TranscriberFactory` and **no** instance `new Quantizer()`: the `listen` command builds the pipeline directly (Task 6). Frame-tail convention (matches Step 2): an adapter emits only **complete** frames of size `N`; a trailing run of fewer than `N` samples is not emitted. The live core follows the same rule so the two are identical.

---

## Approach

The microphone is a real-time device: PortAudio calls a callback on a high-priority audio thread and hands it a pointer to a block of interleaved float samples whose length we do not control. Three jobs turn that into the pipeline's `Frame` contract, and each is deliberately separated so the risky part is tiny:

1. **Downmix** — average the interleaved channels to one mono channel (the same rule as the WAV adapter's multichannel downmix, R2.2).
2. **Reframe** — buffer the mono stream and cut it into overlapping frames of size `N` advanced by hop `H`, stamping each frame with its starting `SamplePosition`. The position is a **running sample count from the stream** (0, H, 2H, …) — never a wall-clock read (non-negotiable 2). This makes live framing byte-identical to file framing for the same audio.
3. **Bridge** — hand finished frames from the audio thread to the pipeline thread through a **bounded channel**. The audio thread must never block or allocate on its hot path, so it uses non-blocking `TryWrite`; if the consumer ever falls behind and the buffer fills, `TryWrite` returns `false` and we **count the drop** rather than silently swallowing it (Error Handling: never swallow). At MVP frame rates (~86 frames/s at 44.1 kHz, hop 256) the consumer keeps up easily, so the drop count stays zero (R10.1).

Jobs 1–3 are pure of the device: `FrameAccumulator` (reframing) and `CaptureFrameStream` (downmix + bridge, exposing the `Frames` property) are ordinary classes we drive from tests by calling `Submit(...)` directly. The **only** device code is `PortAudioAudioSource`, which opens the PortAudio stream, marshals the callback's `IntPtr` into a managed buffer, and calls `Submit` — a few lines that CI cannot exercise and that manual/loopback acceptance covers (this step's Verify). Because the same `CaptureFrameStream` produces the frames in both the test path and the real path, "if live behavior differs from file behavior, the bug is in the adapter" (R10.4) is reduced to those few marshalling lines.

`listen` then reuses the **already-proven** pipeline unchanged: it feeds the live `IAudioSource` into `TranscriptionPipeline.StreamNotes`, prints each `NoteEvent` as it is finalized, and on stop quantizes the collected events (`Quantizer.Quantize` over a `QuantizationGrid`) and writes the output files. No detection code is duplicated — the capture path only produces frames.

---

## Requirements coverage

| Requirement | Task(s) | Proven by (test) |
|---|---|---|
| **R10.1** — capture delivers the same frame contract as the WAV adapter, without drops at MVP rates | 1, 2, 3 | `FrameAccumulatorTests.ChunkingIntoArbitraryBlocksYieldsIdenticalFrames`; `CaptureFrameStreamTests.DeliversAllFramesInOrderWithoutDropsUnderLoad` + `SignalsDroppedFramesWhenConsumerStalls`; `CaptureWavEquivalenceTests.CaptureFramesAreByteIdenticalToWavAdapterFrames` |
| **R10.2** — key-strike→NoteEvent latency SHOULD be <~150 ms, measured and documented | 7, 8 | `LatencyBudgetTests.DefaultParametersMeetSub150msBudget` + README "Live capture" latency figure and manual-measurement procedure |
| **R10.3** — `listen` prints notes live and writes raw MIDI, quantized MIDI, MusicXML on stop. **MusicXML emission is deferred to Step 11; only the injected-writer seam is proven here** (see flag above). | 5, 6 | `LiveTranscriptionSessionTests.*`; `ListenCommandTests.PrintsEachNoteAndWritesMidiTrio` + `WritesMusicXmlOnlyWhenWriterProvided` (MusicXML behind the Step 11 seam) |
| **R10.4** — capture code contains no transcription logic; live == file on the same audio | 3, 4 | `CaptureWavEquivalenceTests.CaptureFramesAreByteIdenticalToWavAdapterFrames` + `TranscriptionOfLivePathEqualsFilePath`; `PortAudioAudioSourceTests.OnAudioBlockProducesDownmixedFrames` (adapter only produces frames) |

---

## Task 1: `FrameAccumulator` — reframe a mono stream into overlapping frames

Use @superpowers:test-driven-development for the red-green loop. This is a pure, device-free class: it accepts mono samples in arbitrary-sized pushes and emits complete frames with a running `SamplePosition`. It is the shared reframing rule that makes live framing identical to file framing.

**Files:**
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Infrastructure/Capture/FrameAccumulator.cs`
- Test:   `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/Infrastructure/FrameAccumulatorTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Capture;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class FrameAccumulatorTests
{
    private const int N = 1024;
    private const int H = 256;
    private static readonly SampleRate Rate = new SampleRate(44100);

    // The reference definition every correct adapter must match: full frames only,
    // tiled by hop H, first sample of frame i is mono[i*H], start position is i*H.
    private static List<Frame> Reference(float[] mono)
    {
        var frames = new List<Frame>();
        long start = 0;
        for (int pos = 0; pos + N <= mono.Length; pos += H)
        {
            var s = new float[N];
            Array.Copy(mono, pos, s, 0, N);
            frames.Add(new Frame(s, new SamplePosition(start, Rate)));   // 2-arg ctor; Rate derived from Start
            start += H;
        }
        return frames;
    }

    private static void AssertFramesEqual(IReadOnlyList<Frame> expected, IReadOnlyList<Frame> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Start.Samples, actual[i].Start.Samples);
            Assert.Equal(expected[i].Samples.Length, actual[i].Samples.Length);
            for (int j = 0; j < expected[i].Samples.Length; j++)
                Assert.Equal(expected[i].Samples[j], actual[i].Samples[j]);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void EmitsFramesWithCorrectPositionsAndOverlap()
    {
        var mono = new float[N + 4 * H];
        for (int i = 0; i < mono.Length; i++) mono[i] = i; // distinct marker values

        var acc = new FrameAccumulator(N, H, Rate);
        var emitted = new List<Frame>();
        acc.Append(mono, emitted);

        AssertFramesEqual(Reference(mono), emitted);
        Assert.Equal(5, emitted.Count);
        Assert.Equal(0, emitted[0].Start.Samples);
        Assert.Equal(H, emitted[1].Start.Samples);
        Assert.Equal((float)H, emitted[1].Samples[0]); // frame 1 starts at sample H
    }

    // R10.1: however the device chops the stream into blocks, the frames are identical.
    [Fact]
    [Trait("Category", "Slow")]
    public void ChunkingIntoArbitraryBlocksYieldsIdenticalFrames()
    {
        var gen =
            from k in Gen.Int[1, 16]
            from mono in Gen.Single[-1f, 1f].Array[N + k * H]   // CsCheck names generators by CLR type: Gen.Single, not Gen.Float
            from blocks in Gen.Int[1, 700].Array[1, 60]
            select (mono, blocks);

        gen.Sample(t =>
        {
            var (mono, blocks) = t;
            var acc = new FrameAccumulator(N, H, Rate);
            var got = new List<Frame>();
            int idx = 0, b = 0;
            while (idx < mono.Length)
            {
                int size = Math.Min(blocks[b % blocks.Length], mono.Length - idx);
                acc.Append(mono.AsSpan(idx, size), got);
                idx += size;
                b++;
            }
            AssertFramesEqual(Reference(mono), got);
        }, iter: 1000);
        // On failure CsCheck prints a minimal case + seed; pin it here as seed: "<value>".
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~FrameAccumulatorTests"
```

Expected FAILURE: compile error — `FrameAccumulator` does not exist yet.

**Step 3 — Minimal implementation:**

```csharp
using AudioClaudio.Domain;

namespace AudioClaudio.Infrastructure.Capture;

/// <summary>
/// Reframes a continuous mono sample stream into overlapping frames of
/// <c>frameSize</c> samples advanced by <c>hop</c> samples. Pure and
/// device-free: samples arrive via <see cref="Append"/> in arbitrary-sized
/// pushes; complete frames are appended to the caller's list. The running
/// start position is a sample count from the stream start (never a clock read),
/// so the same audio always produces bit-identical frames (non-negotiables 1-3).
/// </summary>
public sealed class FrameAccumulator
{
    private readonly int _frameSize;
    private readonly int _hop;
    private readonly SampleRate _rate;
    private readonly List<float> _buffer = new();
    private long _nextStart; // sample index of the next frame's first sample

    public FrameAccumulator(int frameSize, int hop, SampleRate rate)
    {
        if (frameSize < 1) throw new ArgumentOutOfRangeException(nameof(frameSize));
        if (hop < 1) throw new ArgumentOutOfRangeException(nameof(hop));
        _frameSize = frameSize;
        _hop = hop;
        _rate = rate;
    }

    /// <summary>Buffers <paramref name="mono"/> and appends every full frame it completes.</summary>
    public void Append(ReadOnlySpan<float> mono, List<Frame> output)
    {
        foreach (var s in mono) _buffer.Add(s);

        int pos = 0;
        while (_buffer.Count - pos >= _frameSize)
        {
            var samples = new float[_frameSize];
            _buffer.CopyTo(pos, samples, 0, _frameSize);
            output.Add(new Frame(samples, new SamplePosition(_nextStart, _rate)));   // 2-arg ctor; Rate derived from Start
            _nextStart += _hop;
            pos += _hop;
        }

        // Drop the consumed prefix; samples before the next frame start are never reused.
        if (pos > 0) _buffer.RemoveRange(0, pos);
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~FrameAccumulatorTests"
```

Expected PASS: both tests green; the CsCheck property holds over 1000 random chunkings.

**Step 5 — Commit** (via the gitbutler skill — create the step branch first, then commit):

```bash
but branch new feat/step-10-live-capture
but mark feat/step-10-live-capture
but status -fv                       # read the fresh change IDs for the two files
but commit feat/step-10-live-capture \
  -m "feat(infra): frame accumulator for live capture reframing" \
  --changes <ids> --status-after
```

---

## Task 2: `CaptureFrameStream` — downmix + the bounded-channel bridge

Use @superpowers:test-driven-development. This class implements `IAudioSource`, downmixes interleaved blocks to mono, feeds the `FrameAccumulator`, and bridges the real-time producer to the pull consumer through a bounded channel — counting any drop instead of hiding it.

**Files:**
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Infrastructure/Capture/CaptureFrameStream.cs`
- Test:   `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/Infrastructure/CaptureFrameStreamTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System.Threading.Tasks;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Capture;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class CaptureFrameStreamTests
{
    private const int N = 1024, H = 256;
    private static readonly SampleRate Rate = new SampleRate(44100);

    [Fact]
    [Trait("Category", "Fast")]
    public void DownmixesInterleavedChannelsToMean()
    {
        var s = new CaptureFrameStream(N, H, Rate, channelCapacity: 256);

        int frames = 2 * N;                       // enough for several output frames
        var block = new float[frames * 2];        // stereo: L = 0.8, R = 0.2 -> mono 0.5
        for (int i = 0; i < frames; i++) { block[2 * i] = 0.8f; block[2 * i + 1] = 0.2f; }

        s.Submit(block, 2);
        s.Complete();

        var outFrames = s.Frames.ToList();
        Assert.NotEmpty(outFrames);
        foreach (var f in outFrames)
            foreach (var v in f.Samples)
                Assert.Equal(0.5f, v, 6);
    }

    // R10.1: total frames < channel capacity, so no drop is possible regardless of
    // thread timing; proves ordering and completeness across the thread boundary.
    [Fact]
    [Trait("Category", "Fast")]
    public void DeliversAllFramesInOrderWithoutDropsUnderLoad()
    {
        var s = new CaptureFrameStream(N, H, Rate, channelCapacity: 512);
        int totalSamples = 200 * H + N;           // 201 output frames, < capacity 512

        var producer = Task.Run(() =>
        {
            var mono = new float[totalSamples];
            for (int i = 0; i < totalSamples; i++) mono[i] = i; // marker = sample index
            var rng = new Random(12345);
            int idx = 0;
            while (idx < totalSamples)
            {
                int size = Math.Min(rng.Next(1, 400), totalSamples - idx);
                s.Submit(mono.AsSpan(idx, size), 1);
                idx += size;
            }
            s.Complete();
        });

        var received = s.Frames.ToList();         // blocks until producer completes
        producer.Wait();

        Assert.Equal(0, s.DroppedFrameCount);
        int expected = (totalSamples - N) / H + 1;
        Assert.Equal(expected, received.Count);
        for (int i = 0; i < received.Count; i++)
        {
            Assert.Equal((long)i * H, received[i].Start.Samples);
            Assert.Equal((float)(i * H), received[i].Samples[0]);
        }
    }

    // The audio thread must never silently swallow: an overflow is counted.
    [Fact]
    [Trait("Category", "Fast")]
    public void SignalsDroppedFramesWhenConsumerStalls()
    {
        var s = new CaptureFrameStream(N, H, Rate, channelCapacity: 2);
        var mono = new float[N + 50 * H];         // 51 frames into a capacity-2 channel
        s.Submit(mono, 1);
        Assert.True(s.DroppedFrameCount > 0);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~CaptureFrameStreamTests"
```

Expected FAILURE: compile error — `CaptureFrameStream` does not exist.

**Step 3 — Minimal implementation:**

```csharp
using System.Threading;
using System.Threading.Channels;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;

namespace AudioClaudio.Infrastructure.Capture;

/// <summary>
/// Device-independent core of live capture. Downmixes interleaved PCM blocks to
/// mono, reframes them via <see cref="FrameAccumulator"/>, and bridges the
/// real-time producer thread to the pull-based pipeline through a bounded
/// channel exposed as the <see cref="Frames"/> property (the <c>IAudioSource</c>
/// pull contract). Contains NO transcription logic (R10.4). The audio thread calls
/// <see cref="Submit"/> only; it never blocks or allocates on the steady-state
/// hot path, and a full buffer is reported via <see cref="DroppedFrameCount"/>
/// rather than swallowed.
/// </summary>
public sealed class CaptureFrameStream : IAudioSource
{
    private readonly FrameAccumulator _accumulator;
    private readonly Channel<Frame> _channel;
    private readonly List<Frame> _emitted = new();
    private float[] _mono = new float[4096];
    private long _dropped;

    public SampleRate SampleRate { get; }

    public CaptureFrameStream(int frameSize, int hop, SampleRate rate, int channelCapacity)
    {
        SampleRate = rate;
        _accumulator = new FrameAccumulator(frameSize, hop, rate);
        _channel = Channel.CreateBounded<Frame>(new BoundedChannelOptions(channelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            // Wait mode + non-blocking TryWrite: TryWrite returns false when full,
            // so we detect and count the overflow instead of dropping silently.
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public long DroppedFrameCount => Interlocked.Read(ref _dropped);

    /// <summary>Called on the real-time audio thread with one interleaved block.</summary>
    public void Submit(ReadOnlySpan<float> interleaved, int channels)
    {
        int frames = interleaved.Length / channels;
        if (_mono.Length < frames) _mono = new float[frames];

        for (int i = 0; i < frames; i++)
        {
            float sum = 0f;
            int baseIdx = i * channels;
            for (int c = 0; c < channels; c++) sum += interleaved[baseIdx + c];
            _mono[i] = sum / channels;
        }

        _emitted.Clear();
        _accumulator.Append(_mono.AsSpan(0, frames), _emitted);
        foreach (var f in _emitted)
            if (!_channel.Writer.TryWrite(f))
                Interlocked.Increment(ref _dropped);
    }

    /// <summary>Signals end-of-stream so <see cref="Frames"/> completes.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    public IEnumerable<Frame> Frames
    {
        get
        {
            var reader = _channel.Reader;
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                while (reader.TryRead(out var frame))
                    yield return frame;
        }
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~CaptureFrameStreamTests"
```

Expected PASS: downmix mean, in-order lossless delivery, and drop detection all green.

**Step 5 — Commit:**

```bash
but status -fv
but commit feat/step-10-live-capture \
  -m "feat(infra): downmix + bounded-channel capture bridge" \
  --changes <ids> --status-after
```

---

## Task 3: capture ≡ WAV equivalence (the R10.4 / R10.1 linchpin)

Use @superpowers:test-driven-development; if the equivalence ever fails, reach for @superpowers:systematic-debugging (the bug is in the adapter path by definition, R10.4). This device-free test proves the capture path yields byte-identical frames to the WAV adapter for the same audio, and that transcribing the two paths yields identical `NoteEvent`s.

The trick for getting "the same audio" without assuming Step 2's internal PCM decode: read the WAV once with **hop = frameSize** (non-overlapping frames tile the signal), and concatenate those frames to recover the adapter's own decoded mono samples exactly. Feed those samples through `CaptureFrameStream` at the real hop and compare against a normal overlapped read of the same WAV. Use a signal length that is a whole multiple of `N` so the hop = N read tiles cleanly.

**Files:**
- Test: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/Infrastructure/CaptureWavEquivalenceTests.cs`

(No production code — this task only adds tests over Tasks 1–2 plus the existing WAV adapter and transcriber.)

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Application;             // TranscriptionPipeline, TranscriptionSettings
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;         // Radix2Fft
using AudioClaudio.Infrastructure.Audio;    // WavAudioSource
using AudioClaudio.Infrastructure.Capture;  // CaptureFrameStream
using AudioClaudio.Tests.Signals;           // SignalGenerator, WavWriter
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class CaptureWavEquivalenceTests
{
    private const int N = 1024, H = 256, SR = 44100;
    private static readonly SampleRate Rate = new SampleRate(SR);

    // Uses the Step 2 deterministic signal generator + WAV writer (test utilities).
    // Length 8*N so a hop = N read tiles the signal with no partial tail.
    private static string WriteTempSineWav()
    {
        float[] samples = SignalGenerator.Sine(new Pitch(69).Frequency(), 8 * N, Rate);
        string path = Path.Combine(Path.GetTempPath(), $"claudio_eq_{Guid.NewGuid():N}.wav");
        WavWriter.WriteMonoFile(path, samples, Rate);
        return path;
    }

    // Recover the WAV adapter's exact decoded mono by reading with hop == frameSize.
    private static float[] ReconstructMono(string wavPath)
    {
        using var src = WavAudioSource.FromFile(wavPath, new FrameParameters(N, N));
        var mono = new List<float>();
        foreach (var f in src.Frames) mono.AddRange(f.Samples);
        return mono.ToArray();
    }

    private static List<Frame> PushThroughCapture(float[] mono, int blockSize)
    {
        var cap = new CaptureFrameStream(N, H, new SampleRate(SR), channelCapacity: 8192);
        int idx = 0;
        while (idx < mono.Length)
        {
            int size = Math.Min(blockSize, mono.Length - idx);
            cap.Submit(mono.AsSpan(idx, size), 1);
            idx += size;
        }
        cap.Complete();
        return cap.Frames.ToList();
    }

    // R10.1 + R10.4: identical frames from the file adapter and the capture path.
    [Fact]
    [Trait("Category", "Fast")]
    public void CaptureFramesAreByteIdenticalToWavAdapterFrames()
    {
        string wav = WriteTempSineWav();
        try
        {
            using var fileSource = WavAudioSource.FromFile(wav, new FrameParameters(N, H));
            var fileFrames = fileSource.Frames.ToList();
            var liveFrames = PushThroughCapture(ReconstructMono(wav), blockSize: 333);

            Assert.Equal(fileFrames.Count, liveFrames.Count);
            for (int i = 0; i < fileFrames.Count; i++)
            {
                Assert.Equal(fileFrames[i].Start.Samples, liveFrames[i].Start.Samples);
                for (int j = 0; j < N; j++)
                    Assert.Equal(fileFrames[i].Samples[j], liveFrames[i].Samples[j]);
            }
        }
        finally { File.Delete(wav); }
    }

    // R10.4: the whole transcription is identical through both sources.
    [Fact]
    [Trait("Category", "Slow")]
    public void TranscriptionOfLivePathEqualsFilePath()
    {
        string wav = WriteTempSineWav();
        try
        {
            var settings = TranscriptionSettings.ForTempo(120) with { FrameSize = N, Hop = H };
            var transcriber = new TranscriptionPipeline(settings, new Radix2Fft());

            using var fileSource = WavAudioSource.FromFile(wav, new FrameParameters(N, H));
            var fromFile = transcriber.Transcribe(fileSource).RawEvents;

            var cap = new CaptureFrameStream(N, H, Rate, channelCapacity: 8192);
            var mono = ReconstructMono(wav);
            int idx = 0;
            while (idx < mono.Length) { int sz = Math.Min(257, mono.Length - idx); cap.Submit(mono.AsSpan(idx, sz), 1); idx += sz; }
            cap.Complete();
            var fromLive = transcriber.Transcribe(cap).RawEvents;

            Assert.Equal(fromFile.Count, fromLive.Count);
            for (int i = 0; i < fromFile.Count; i++)
            {
                Assert.Equal(fromFile[i].Pitch.MidiNumber, fromLive[i].Pitch.MidiNumber);
                Assert.Equal(fromFile[i].Onset.Samples, fromLive[i].Onset.Samples);
                Assert.Equal(fromFile[i].Duration.Samples, fromLive[i].Duration.Samples);
            }
        }
        finally { File.Delete(wav); }
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~CaptureWavEquivalenceTests"
```

Expected FAILURE first pass until Tasks 1–2 compile. The Step 2/9 symbols used here are the CONTRACTS-pinned ones (`SignalGenerator.Sine(freqHz, count, rate)`, `WavWriter.WriteMonoFile`, `WavAudioSource.FromFile(path, new FrameParameters(N, H))`, `new TranscriptionPipeline(TranscriptionSettings.ForTempo(120) with { FrameSize = N, Hop = H }, new Radix2Fft())`, `Transcribe(...).RawEvents`); once Tasks 1–2 are in place the test compiles and — because it reframes identically — passes.

**Step 3 — Minimal implementation:** none. If `CaptureFramesAreByteIdenticalToWavAdapterFrames` fails after names are aligned, the defect is in Task 1/2's reframing or downmix, not here — fix it there (the failing test is presumed right, §1 rule 8).

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~CaptureWavEquivalenceTests"
```

Expected PASS: frames byte-identical; transcription identical through both sources.

**Step 5 — Commit:**

```bash
but status -fv
but commit feat/step-10-live-capture \
  -m "test(infra): capture path is frame- and transcription-identical to WAV" \
  --changes <ids> --status-after
```

---

## Task 4: `PortAudioAudioSource` — the device adapter

Use @superpowers:test-driven-development. This is the only class that touches PortAudio. The constructor is **device-free** (it builds the `CaptureFrameStream` and buffers only); the device is opened lazily in `Start()`. That lets CI construct the source and drive its callback seam `OnAudioBlock` directly, so the sole untested lines are the PortAudio `Start()`/`OnPortAudioCallback` marshalling — covered by manual/loopback acceptance (Task 8).

Add the NuGet dependency and record its license (§1 rule 7).

**Files:**
- Modify: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Infrastructure/AudioClaudio.Infrastructure.csproj` (add package + `InternalsVisibleTo`)
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Infrastructure/Audio/PortAudioAudioSource.cs` (CONTRACTS §10 places the public adapter in `AudioClaudio.Infrastructure.Audio`)
- Modify: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/DECISIONS.md` (record PortAudioSharp2 + version + MIT)
- Test:   `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/Infrastructure/PortAudioAudioSourceTests.cs`

**Step 0 — Add the package and expose internals to the test project:**

```bash
dotnet add src/AudioClaudio.Infrastructure/AudioClaudio.Infrastructure.csproj package PortAudioSharp2
```

Then add to `AudioClaudio.Infrastructure.csproj` (so the test project can call the internal `OnAudioBlock` seam):

```xml
<ItemGroup>
  <InternalsVisibleTo Include="AudioClaudio.Tests" />
</ItemGroup>
```

Record in `DECISIONS.md`: `PortAudioSharp2 <resolved-version> — MIT — live microphone capture (Step 10); bundles native PortAudio for macOS/Windows/Linux; no copyleft in graph.`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Audio;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class PortAudioAudioSourceTests
{
    private const int N = 1024, H = 256;

    // Device-free: construct (no Start), push a block through the callback seam,
    // Complete, and read frames. Proves the adapter only downmixes+reframes
    // (no transcription logic, R10.4) and honours the IAudioSource contract.
    [Fact]
    [Trait("Category", "Fast")]
    public void OnAudioBlockProducesDownmixedFrames()
    {
        using var src = new PortAudioAudioSource(
            sampleRateHz: 44100, frameSize: N, hop: H, channels: 2, channelCapacity: 1024);

        int frames = 2 * N;                       // several output frames
        var block = new float[frames * 2];        // stereo L = 1.0, R = 0.0 -> mono 0.5
        for (int i = 0; i < frames; i++) { block[2 * i] = 1.0f; block[2 * i + 1] = 0.0f; }

        src.OnAudioBlock(block, 2);
        src.Stop();                               // not started -> just completes the stream

        var outFrames = src.Frames.ToList();
        Assert.NotEmpty(outFrames);
        Assert.Equal(44100, src.SampleRate.Hz);   // CONTRACTS: SampleRate.Hz, never .Hertz
        Assert.Equal(44100, outFrames[0].Rate.Hz); // Frame.Rate is derived from Start.Rate
        foreach (var f in outFrames)
            foreach (var v in f.Samples)
                Assert.Equal(0.5f, v, 6);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~PortAudioAudioSourceTests"
```

Expected FAILURE: `PortAudioAudioSource` does not exist.

**Step 3 — Minimal implementation:**

```csharp
using System.Runtime.InteropServices;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Capture;   // FrameAccumulator / CaptureFrameStream (device-free core)
using PortAudioSharp;

namespace AudioClaudio.Infrastructure.Audio;

/// <summary>
/// Live microphone <see cref="IAudioSource"/> over PortAudioSharp2. The device
/// is opened lazily in <see cref="Start"/>; construction is device-free so the
/// downmix/reframe path (delegated to <see cref="CaptureFrameStream"/>) is fully
/// testable. Frame positions are sample counts from the stream, never clock
/// reads (non-negotiable 2). No transcription logic lives here (R10.4).
/// </summary>
public sealed class PortAudioAudioSource : IAudioSource, IDisposable
{
    private readonly int _sampleRateHz;
    private readonly int _channels;
    private readonly float[] _scratch;
    private readonly CaptureFrameStream _stream;
    private readonly Stream.Callback _callback; // held to keep the delegate alive across native calls
    private Stream? _paStream;
    private bool _started;

    public SampleRate SampleRate { get; }

    public PortAudioAudioSource(int sampleRateHz, int frameSize, int hop,
                                int channels = 1, int channelCapacity = 256)
    {
        _sampleRateHz = sampleRateHz;
        _channels = channels;
        SampleRate = new SampleRate(sampleRateHz);
        _stream = new CaptureFrameStream(frameSize, hop, SampleRate, channelCapacity);
        _scratch = new float[8192 * channels];
        _callback = OnPortAudioCallback;
    }

    public long DroppedFrameCount => _stream.DroppedFrameCount;

    public IEnumerable<Frame> Frames => _stream.Frames;

    /// <summary>Opens the default input device and begins capture. Device-only; not run in CI.</summary>
    public void Start()
    {
        if (_started) return;
        PortAudio.Initialize();
        int device = PortAudio.DefaultInputDevice;
        if (device == PortAudio.NoDevice)
        {
            PortAudio.Terminate();
            throw new InvalidOperationException("No default audio input device is available.");
        }
        var info = PortAudio.GetDeviceInfo(device);
        var inParams = new StreamParameters
        {
            device = device,
            channelCount = _channels,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };
        _paStream = new Stream(inParams, null, _sampleRateHz, framesPerBuffer: 0,
                               StreamFlags.ClipOff, _callback, IntPtr.Zero);
        _paStream.Start();
        _started = true;
    }

    /// <summary>Stops capture (if started) and completes the frame stream so consumers finish.</summary>
    public void Stop()
    {
        if (_started && _paStream is not null) { _paStream.Stop(); _started = false; }
        _stream.Complete();
    }

    // The device-independent seam exercised by tests. The real callback marshals
    // then calls this; keeping it separate means only the marshalling is untested.
    internal void OnAudioBlock(ReadOnlySpan<float> interleaved, int channels)
        => _stream.Submit(interleaved, channels);

    private StreamCallbackResult OnPortAudioCallback(
        IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
    {
        int count = (int)frameCount * _channels;
        if (input != IntPtr.Zero && count > 0 && count <= _scratch.Length)
        {
            Marshal.Copy(input, _scratch, 0, count);
            OnAudioBlock(_scratch.AsSpan(0, count), _channels);
        }
        return StreamCallbackResult.Continue;
    }

    public void Dispose()
    {
        try { Stop(); } catch { /* best effort on teardown */ }
        _paStream?.Dispose();
        if (_started) PortAudio.Terminate();
    }
}
```

> Note: the exact PortAudioSharp2 symbol names (`PortAudio.NoDevice`, `StreamParameters` fields, `Stream.Callback` signature) may differ slightly by wrapper version — align to the installed package's public API. None of this affects the tested seam.

**Step 4 — Run to verify it passes:**

```bash
dotnet build
dotnet test --filter "FullyQualifiedName~PortAudioAudioSourceTests"
```

Expected PASS: the device-free callback seam produces downmixed frames; the build links PortAudioSharp2. (The real `Start()` path is verified manually in Task 8.)

**Step 5 — Commit:**

```bash
but status -fv
but commit feat/step-10-live-capture \
  -m "feat(infra): PortAudioSharp2 microphone IAudioSource adapter" \
  --changes <ids> --status-after
```

---

## Task 5: `LiveTranscriptionSession` — the Application use case

Use @superpowers:test-driven-development. This orchestrates the streaming feed: it pulls `NoteEvent`s from an injected `Func<IAudioSource, IEnumerable<NoteEvent>>` (production binds it to `TranscriptionPipeline.StreamNotes` — `StreamNotes` lives on the concrete pipeline, not the `ITranscriber` port), invokes an `onNote` callback for each (the live print), collects them, quantizes on stop via `Quantizer.Quantize(events, new QuantizationGrid(...))`, and returns the raw events plus the `Score`. It touches only Domain + Application — no I/O, no device, no file paths — so it is trivially testable with a fake streaming func. File writing stays in the composition root (Task 6).

**Files:**
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Application/UseCases/LiveTranscriptionSession.cs`
- Test:   `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/Application/LiveTranscriptionSessionTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System.Threading;
using AudioClaudio.Application.Ports;
using AudioClaudio.Application.UseCases;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Application;

public class LiveTranscriptionSessionTests
{
    private static readonly SampleRate Rate = new SampleRate(44100);

    // IAudioSource is a Frames-property pull port; the note feed is faked below,
    // so the frames themselves are never consumed here.
    private sealed class FakeSource : IAudioSource
    {
        public IEnumerable<Frame> Frames { get { yield break; } }
    }

    private static NoteEvent Note(int midi, long onset, long dur) =>
        new NoteEvent(new Pitch(midi),
                      new SamplePosition(onset, Rate),
                      new SampleDuration(dur, Rate),
                      velocity: 100);

    [Fact]
    [Trait("Category", "Fast")]
    public void EmitsEachNoteLiveThenReturnsCollectedEventsAndQuantizedScore()
    {
        var notes = new[]
        {
            Note(60, 0,     22050),
            Note(62, 22050, 22050),
            Note(64, 44100, 22050),
        };
        // The session's streaming seam is a Func<IAudioSource, IEnumerable<NoteEvent>>;
        // production binds it to TranscriptionPipeline.StreamNotes (see Task 6).
        IEnumerable<NoteEvent> Stream(IAudioSource s) { foreach (var n in notes) yield return n; }
        var session = new LiveTranscriptionSession(Stream, Rate, TimeSignature.FourFour);

        var printed = new List<int>();
        var result = session.Run(new FakeSource(), tempoBpm: 120,
                                 onNote: n => printed.Add(n.Pitch.MidiNumber), ct: default);

        Assert.Equal(new[] { 60, 62, 64 }, printed);                     // printed live, in order
        Assert.Equal(3, result.Events.Count);                             // raw events collected
        Assert.Equal(60, result.Events[0].Pitch.MidiNumber);
        Assert.Equal(120.0, result.Score.Tempo.BeatsPerMinute);          // quantized Score carries the grid tempo
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void StopsCollectingWhenCancellationRequested()
    {
        var notes = new[] { Note(60, 0, 100), Note(62, 100, 100), Note(64, 200, 100) };
        IEnumerable<NoteEvent> Stream(IAudioSource s) { foreach (var n in notes) yield return n; }
        var session = new LiveTranscriptionSession(Stream, Rate, TimeSignature.FourFour);
        using var cts = new CancellationTokenSource();

        var result = session.Run(new FakeSource(), 120,
                                 onNote: _ => cts.Cancel(), ct: cts.Token); // cancel after first note

        Assert.Single(result.Events);
        Assert.Equal(60, result.Events[0].Pitch.MidiNumber);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~LiveTranscriptionSessionTests"
```

Expected FAILURE: `LiveTranscriptionSession` / `LiveSessionResult` do not exist.

**Step 3 — Minimal implementation:**

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;

namespace AudioClaudio.Application.UseCases;

/// <summary>The result of a live session: the raw performance and its quantized score.</summary>
public sealed record LiveSessionResult(IReadOnlyList<NoteEvent> Events, Score Score);

/// <summary>
/// Runs a live transcription: pulls NoteEvents from the injected streaming feed
/// (production binds it to <c>TranscriptionPipeline.StreamNotes</c>, so the exact
/// same detection code as file transcription runs — R10.4), reports each one via
/// the <c>onNote</c> callback as it is finalized, and on stop quantizes the
/// collected events into a <see cref="Score"/>. No I/O, no device, no clock reads.
/// </summary>
public sealed class LiveTranscriptionSession
{
    private readonly Func<IAudioSource, IEnumerable<NoteEvent>> _streamNotes;
    private readonly SampleRate _rate;
    private readonly TimeSignature _timeSignature;
    private readonly Subdivision _subdivision;

    public LiveTranscriptionSession(Func<IAudioSource, IEnumerable<NoteEvent>> streamNotes,
                                    SampleRate rate,
                                    TimeSignature timeSignature,
                                    Subdivision subdivision = Subdivision.Sixteenth)
    {
        _streamNotes = streamNotes;
        _rate = rate;
        _timeSignature = timeSignature;
        _subdivision = subdivision;
    }

    public LiveSessionResult Run(IAudioSource source, int tempoBpm,
                                 Action<NoteEvent> onNote, CancellationToken ct = default)
    {
        var events = new List<NoteEvent>();
        foreach (var note in _streamNotes(source))
        {
            if (ct.IsCancellationRequested) break;
            onNote(note);
            events.Add(note);
        }
        var grid = new QuantizationGrid(_rate, new Tempo(tempoBpm), _timeSignature, _subdivision);
        var score = Quantizer.Quantize(events, grid);
        return new LiveSessionResult(events, score);
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~LiveTranscriptionSessionTests"
```

Expected PASS: live emission order, event collection, quantized score, and cancellation all green.

**Step 5 — Commit:**

```bash
but status -fv
but commit feat/step-10-live-capture \
  -m "feat(app): live transcription session use case" \
  --changes <ids> --status-after
```

---

## Task 6: `listen` command + composition-root wiring (R10.3)

Use @superpowers:test-driven-development. `ListenCommand` holds the testable behavior — run the session, print each note, and on stop write `raw.mid`, `score.mid`, and (behind the Step 11 seam) `score.musicxml`. `Program.cs` is the thin composition root that constructs the real adapters and wires Ctrl+C to stop.

**Files:**
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Cli/Commands/ListenCommand.cs`
- Modify: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Cli/Program.cs` (add the `listen` case)
- Test:   `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/Cli/ListenCommandTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Application.Ports;
using AudioClaudio.Application.UseCases;
using AudioClaudio.Cli.Commands;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Cli;

public class ListenCommandTests
{
    private static readonly SampleRate Rate = new SampleRate(44100);

    private sealed class FakeSource : IAudioSource
    {
        public IEnumerable<Frame> Frames { get { yield break; } }
    }

    // CONTRACTS: writers are Stream-based. DryWetMidiWriter implements both ports,
    // so one spy stands in for the raw-event + score MIDI writer.
    private sealed class SpyMidiWriter : IScoreWriter, INoteEventWriter
    {
        public int ScoreWrites;
        public int EventWrites;
        public void Write(Score score, Stream destination) { ScoreWrites++; destination.WriteByte(1); }
        public void Write(IReadOnlyList<NoteEvent> events, Tempo tempo, Stream destination)
            { EventWrites++; destination.WriteByte(1); }
    }

    private sealed class SpyScoreWriter : IScoreWriter
    {
        public int Writes;
        public void Write(Score score, Stream destination) { Writes++; destination.WriteByte(1); }
    }

    private static NoteEvent Note(int midi, long onset) =>
        new NoteEvent(new Pitch(midi), new SamplePosition(onset, Rate), new SampleDuration(22050, Rate), 100);

    private static LiveTranscriptionSession Session(IReadOnlyList<NoteEvent> notes)
    {
        IEnumerable<NoteEvent> Stream(IAudioSource s) { foreach (var n in notes) yield return n; }
        return new LiveTranscriptionSession(Stream, Rate, TimeSignature.FourFour);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void PrintsEachNoteAndWritesMidiTrio()
    {
        var notes = new[] { Note(60, 0), Note(62, 22050), Note(64, 44100) };
        var midi = new SpyMidiWriter();
        var printed = new List<string>();
        string dir = Path.Combine(Path.GetTempPath(), $"claudio_listen_{Guid.NewGuid():N}");
        try
        {
            var cmd = new ListenCommand(Session(notes), midi, midi, printed.Add, musicXmlWriter: null);
            var result = cmd.Run(new FakeSource(), tempoBpm: 120, outDir: dir);

            Assert.Equal(3, printed.Count(l => l.StartsWith("note ")));      // one line per note
            Assert.True(File.Exists(Path.Combine(dir, "raw.mid")));          // raw performance written
            Assert.True(File.Exists(Path.Combine(dir, "score.mid")));        // quantized score written
            Assert.False(File.Exists(Path.Combine(dir, "score.musicxml")));  // no MusicXML writer -> no file
            Assert.Equal(1, midi.EventWrites);
            Assert.Equal(1, midi.ScoreWrites);
            Assert.Equal(3, result.Events.Count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    // The MusicXML seam: emitted iff a writer is supplied (real writer arrives in Step 11).
    [Fact]
    [Trait("Category", "Fast")]
    public void WritesMusicXmlOnlyWhenWriterProvided()
    {
        var notes = new[] { Note(60, 0) };
        var midi = new SpyMidiWriter();
        var xml = new SpyScoreWriter();
        string dirA = Path.Combine(Path.GetTempPath(), $"claudio_listen_{Guid.NewGuid():N}");
        string dirB = Path.Combine(Path.GetTempPath(), $"claudio_listen_{Guid.NewGuid():N}");
        try
        {
            new ListenCommand(Session(notes), midi, midi, _ => { }, musicXmlWriter: null)
                .Run(new FakeSource(), 120, dirA);
            Assert.False(File.Exists(Path.Combine(dirA, "score.musicxml")));

            new ListenCommand(Session(notes), midi, midi, _ => { }, musicXmlWriter: xml)
                .Run(new FakeSource(), 120, dirB);
            Assert.True(File.Exists(Path.Combine(dirB, "score.musicxml")));
            Assert.Equal(1, xml.Writes);
        }
        finally
        {
            if (Directory.Exists(dirA)) Directory.Delete(dirA, recursive: true);
            if (Directory.Exists(dirB)) Directory.Delete(dirB, recursive: true);
        }
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~ListenCommandTests"
```

Expected FAILURE: `ListenCommand` does not exist.

**Step 3 — Minimal implementation:**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AudioClaudio.Application.Ports;
using AudioClaudio.Application.UseCases;
using AudioClaudio.Domain;

namespace AudioClaudio.Cli.Commands;

/// <summary>
/// The `listen` command: run a live transcription, print each detected note as
/// it occurs, and on stop write the session's raw MIDI, quantized MIDI, and
/// (when a writer is supplied — Step 11) MusicXML. Writers are Stream-based
/// (CONTRACTS §7/§11); this composition-layer command opens the FileStreams and
/// calls them. Capture and detection code stay untouched (R10.3/R10.4).
/// </summary>
public sealed class ListenCommand
{
    private readonly LiveTranscriptionSession _session;
    private readonly INoteEventWriter _rawWriter;   // DryWetMidiWriter (raw performance)
    private readonly IScoreWriter _scoreWriter;     // DryWetMidiWriter (quantized MIDI)
    private readonly IScoreWriter? _musicXmlWriter; // MusicXmlScoreWriter; null until Step 11 registers it
    private readonly Action<string> _print;

    public ListenCommand(LiveTranscriptionSession session,
                         INoteEventWriter rawWriter, IScoreWriter scoreWriter,
                         Action<string> print, IScoreWriter? musicXmlWriter = null)
    {
        _session = session;
        _rawWriter = rawWriter;
        _scoreWriter = scoreWriter;
        _print = print;
        _musicXmlWriter = musicXmlWriter;
    }

    public LiveSessionResult Run(IAudioSource source, int tempoBpm, string outDir,
                                 CancellationToken ct = default)
    {
        Directory.CreateDirectory(outDir);
        _print($"Listening at {tempoBpm} BPM. Press Ctrl+C to stop.");

        var result = _session.Run(source, tempoBpm, n => _print(FormatNote(n)), ct);
        var tempo = new Tempo(tempoBpm);

        string rawPath = Path.Combine(outDir, "raw.mid");
        string scorePath = Path.Combine(outDir, "score.mid");
        using (var raw = File.Create(rawPath))
            _rawWriter.Write(result.Events, tempo, raw);
        using (var score = File.Create(scorePath))
            _scoreWriter.Write(result.Score, score);
        _print($"Wrote {rawPath} and {scorePath}.");

        if (_musicXmlWriter is not null)
        {
            string xmlPath = Path.Combine(outDir, "score.musicxml");
            using (var xml = File.Create(xmlPath))
                _musicXmlWriter.Write(result.Score, xml);
            _print($"Wrote {xmlPath}.");
        }
        return result;
    }

    private static string FormatNote(NoteEvent n) =>
        $"note {n.Pitch.MidiNumber,3}  onset {n.Onset.Samples,10}  dur {n.Duration.Samples,8}";
}
```

Add the `listen` case to `Program.cs` (the only place adapters are constructed and Ctrl+C is wired — Section 7). Illustrative shape; align option parsing and adapter names to the existing CLI:

```csharp
// in the command switch of AudioClaudio.Cli/Program.cs
// usings: AudioClaudio.Application, .Application.Ports, .Application.UseCases,
//         AudioClaudio.Domain, AudioClaudio.Domain.Spectral,
//         AudioClaudio.Infrastructure.Audio, AudioClaudio.Infrastructure.Midi
case "listen":
{
    int tempo = Args.RequireInt(args, "--tempo");
    string outDir = Args.Optional(args, "--out-dir") ?? ".";
    const int SampleRateHz = 44100, FrameSize = 1024, Hop = 256;

    // Build the pipeline directly (there is NO TranscriberFactory). The FFT is
    // injected per the Step 3 DECISION: Radix2Fft (Domain, Option A) shown here;
    // under Option B the root hands it a NWavesFourierTransform instead.
    IFourierTransform fft = new Radix2Fft();
    var settings = TranscriptionSettings.ForTempo(tempo) with { FrameSize = FrameSize, Hop = Hop };
    var pipeline = new TranscriptionPipeline(settings, fft);

    using var source = new PortAudioAudioSource(SampleRateHz, FrameSize, Hop, channels: 1);
    var midi = new DryWetMidiWriter();  // implements INoteEventWriter + IScoreWriter
    var session = new LiveTranscriptionSession(pipeline.StreamNotes,
                                               new SampleRate(SampleRateHz), TimeSignature.FourFour);
    // musicXmlWriter: null until Step 11 registers `new MusicXmlScoreWriter()` here (zero change to `listen`).
    var command = new ListenCommand(session, midi, midi, Console.WriteLine);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; source.Stop(); cts.Cancel(); };
    source.Start();
    command.Run(source, tempo, outDir, cts.Token);
    break;
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet build
dotnet test --filter "FullyQualifiedName~ListenCommandTests"
```

Expected PASS: three note lines printed, both MIDI files written, MusicXML only when a writer is supplied.

**Step 5 — Commit:**

```bash
but status -fv
but commit feat/step-10-live-capture \
  -m "feat(cli): listen command wired to live capture and MIDI output" \
  --changes <ids> --status-after
```

---

## Task 7: latency budget (R10.2 — the deterministic, measured part)

Use @superpowers:test-driven-development. R10.2 is a SHOULD, and the spec says the figure is *measured and documented, not promised*. The end-to-end device latency can only be measured on real hardware (Task 8), but the **algorithmic** portion — how long the pipeline must wait after a key-strike before it can emit the note — is a pure function of the frame parameters and can be asserted here.

Worst case: an onset lands just after a frame boundary, so we wait ~`frameSize` samples to fill the frame that contains the attack, plus the spectral-flux peak-picker's look-ahead of a few hops to confirm the flux peak is a local maximum. That is `frameSize + lookaheadFrames * hop` samples.

**Files:**
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Infrastructure/Capture/LatencyBudget.cs`
- Test:   `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/Infrastructure/LatencyBudgetTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Infrastructure.Capture;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class LatencyBudgetTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void DefaultParametersMeetSub150msBudget()
    {
        // Defaults: 44.1 kHz, N = 1024, H = 256, peak-picker look-ahead = 3 hops.
        double ms = LatencyBudget.WorstCaseAlgorithmicMs(
            sampleRateHz: 44100, frameSize: 1024, hop: 256, onsetLookaheadFrames: 3);

        Assert.True(ms < 150.0, $"algorithmic latency {ms:F1} ms exceeds the 150 ms budget");
        Assert.Equal(40.63, ms, 2); // 1024 + 3*256 = 1792 samples => 40.63 ms
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~LatencyBudgetTests"
```

Expected FAILURE: `LatencyBudget` does not exist.

**Step 3 — Minimal implementation:**

```csharp
namespace AudioClaudio.Infrastructure.Capture;

/// <summary>
/// The algorithmic (device-independent) portion of key-strike -> NoteEvent
/// latency (R10.2). End-to-end latency additionally includes the PortAudio input
/// buffer and OS scheduling, which are measured on hardware and documented in the
/// README, not promised here.
/// </summary>
public static class LatencyBudget
{
    /// <param name="onsetLookaheadFrames">
    /// Hops the spectral-flux peak-picker looks ahead to confirm a local maximum
    /// (from Step 5's onset detector — align this to its actual value).
    /// </param>
    /// <returns>Worst-case algorithmic latency in milliseconds.</returns>
    public static double WorstCaseAlgorithmicMs(
        int sampleRateHz, int frameSize, int hop, int onsetLookaheadFrames)
    {
        long samples = frameSize + (long)onsetLookaheadFrames * hop;
        return 1000.0 * samples / sampleRateHz;
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~LatencyBudgetTests"
```

Expected PASS: 40.63 ms, comfortably under 150 ms.

**Step 5 — Commit:**

```bash
but status -fv
but commit feat/step-10-live-capture \
  -m "feat(infra): algorithmic latency budget for live capture" \
  --changes <ids> --status-after
```

---

## Task 8: document usage, the latency figure, and manual acceptance (R10.2 doc; step Verify)

This is a documentation task (no red-green loop). The Verify section for Step 10 is a **loopback / manual acceptance** script — the automated correctness burden stays on Step 9's closed loop. Add a "Live capture" section to the README, the measured/algorithmic latency figure, and the macOS microphone-permission note (the primary dev machine is an M3 Max — first `listen` run triggers a TCC prompt to grant the terminal/app microphone access).

**Files:**
- Modify: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/README.md` (add "Live capture")
- Modify: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/DECISIONS.md` (confirm PortAudioSharp2 entry from Task 4; note the pull + bounded-channel bridge realizes the Step 2 mic decision)

**Step 1 — Add the README section (content to write):**

```markdown
### Live capture (`listen`)

    dotnet run --project src/AudioClaudio.Cli -- listen --tempo 100 [--out-dir .]

Captures from the default input device, prints each detected note as it is
played, and on Ctrl+C writes `raw.mid` (unquantized performance), `score.mid`
(quantized), and — once Step 11 lands — `score.musicxml`, into `--out-dir`.

The microphone is *just an adapter*: `listen` feeds the same transcription
pipeline the file path uses, so live and file transcription of the same audio
are identical by construction (verified device-free in
`CaptureWavEquivalenceTests`).

**Latency.** Worst-case algorithmic latency (key-strike to emitted note) with the
default parameters (44.1 kHz, frame 1024, hop 256) is **~41 ms**; end-to-end
latency adds the PortAudio input buffer and OS scheduling. Measure end-to-end on
your machine with the loopback procedure below.

**macOS microphone permission.** The first `listen` run prompts to grant your
terminal (or the built app) microphone access. If no prompt appears and capture
is silent, enable it under System Settings -> Privacy & Security -> Microphone.

**Manual acceptance (loopback).** Play a fixture WAV through the speakers, run
`listen`, then compare the emitted `score.mid` loosely against the fixture's
known notes. Exact correctness is owned by the Step 9 closed-loop suite; this
check only confirms the capture adapter is wired and audible.
```

**Step 2 — Verify the docs render and the full suite is green:**

```bash
dotnet build
dotnet test                              # full suite
dotnet test --filter Category=Fast       # fast filter still green
dotnet format --verify-no-changes
```

Expected: build clean, all tests pass, formatter reports no changes, README shows the new section.

**Step 3 — Manual acceptance (run once, document the result in the README latency line):**

```bash
# With a working microphone and speakers:
dotnet run --project src/AudioClaudio.Cli -- listen --tempo 100 --out-dir /tmp/claudio-listen
# Play a fixture WAV aloud, then Ctrl+C. Confirm notes printed and the MIDI trio written.
```

**Step 4 — Commit (docs):**

```bash
but status -fv
but commit feat/step-10-live-capture \
  -m "docs: listen usage, latency figure, and macOS mic permission note" \
  --changes <ids> --status-after
```

> The branch `feat/step-10-live-capture` now carries the whole step; its commits roll up to the spec message **`feat(infra): live capture adapter and listen command`**. Push and open the PR via the gitbutler skill (`but push feat/step-10-live-capture` then `but pr new`) when the step is accepted.

---

## Verify (step exit criteria)

- [ ] **R10.1 — same frame contract, no drops:** `CaptureWavEquivalenceTests.CaptureFramesAreByteIdenticalToWavAdapterFrames` shows capture frames are byte-identical to the WAV adapter's; `CaptureFrameStreamTests.DeliversAllFramesInOrderWithoutDropsUnderLoad` shows in-order, drop-free delivery across the thread boundary; `FrameAccumulatorTests.ChunkingIntoArbitraryBlocksYieldsIdenticalFrames` shows framing is independent of how the device chunks the stream.
- [ ] **R10.2 — latency measured and documented:** `LatencyBudgetTests.DefaultParametersMeetSub150msBudget` asserts ~41 ms algorithmic latency < 150 ms; README documents the figure plus a manual end-to-end procedure (SHOULD, not promised).
- [ ] **R10.3 — `listen` prints live and writes the trio on stop:** `ListenCommandTests.PrintsEachNoteAndWritesMidiTrio` (print + raw/quantized MIDI) and `WritesMusicXmlOnlyWhenWriterProvided` (MusicXML behind the Step 11 seam — flagged).
- [ ] **R10.4 — no transcription logic in capture; live == file:** `CaptureWavEquivalenceTests.TranscriptionOfLivePathEqualsFilePath` shows identical NoteEvents through both sources; the capture classes only downmix/reframe.
- [ ] **Loopback / manual acceptance** run once and its result recorded (Verify script; automated correctness remains on Step 9).

## Definition of Done

- [ ] `dotnet build` succeeds; warnings-as-errors clean.
- [ ] `dotnet format --verify-no-changes` reports no changes.
- [ ] All new tests green; `dotnet test --filter Category=Fast` still green (the slow CsCheck/equivalence properties carry `[Trait("Category","Slow")]`).
- [ ] Dependency rule intact: `PortAudioSharp2` is referenced **only** by `AudioClaudio.Infrastructure`; Domain and Application reference nothing new beyond the BCL; adapters are constructed only in `AudioClaudio.Cli`.
- [ ] Committed via GitButler on `feat/step-10-live-capture`; commits roll up to `feat(infra): live capture adapter and listen command`.
- [ ] Requirement-coverage table fully satisfied (R10.1–R10.4).
- [ ] `DECISIONS.md` updated: `PortAudioSharp2 <version> — MIT`, and a note that the pull + bounded-channel bridge realizes the Step 2 mic-delivery decision.
- [ ] Flagged MusicXML ordering tension recorded here; the `listen` MusicXML seam is left for Step 11 to fill with zero change to `listen`.
