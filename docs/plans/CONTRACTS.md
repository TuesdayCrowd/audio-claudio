# Audio Claudio — Cross-Step API Contracts (authoritative)

> **For Claude:** This file is the single source of truth for every type name,
> signature, namespace, and file path shared across the Step 0–12 plans. When a
> plan's code and this file disagree, **this file wins** — the plans were drafted
> in parallel and drifted; this reconciles them. Each type is *defined* by exactly
> one step (its **definer**) and *consumed* by others; consumers use these exact
> names. Update this file (not the plans piecemeal) if a contract genuinely must
> change, then propagate.

This appendix exists because 13 plans were drafted independently against a shared
foundation that pinned the domain *primitives* but not the *derived* types. A
consistency audit surfaced ~22 naming/signature divergences; the resolutions are
recorded here. It does not replace the plans — it constrains them.

---

## 0. Naming conventions

- **Sample-rate accessor is `Hz`** (int), never `Hertz`. `SampleRate.Hz`.
- **A `Frame`'s start is `Start`** (a `SamplePosition`); its rate is `Rate`
  (derived: `Rate => Start.Rate`); its samples are `Samples` (`float[]`). There is
  no `Position`, no `SampleRate` property, no `Length` member (use
  `Samples.Length`).
- **Application ports live in `AudioClaudio.Application.Ports`** (folder
  `src/AudioClaudio.Application/Ports/`). Application *services* (the pipeline,
  its settings/result) live in `AudioClaudio.Application`.
- **Domain** types live in `AudioClaudio.Domain` (or `AudioClaudio.Domain.Spectral`
  for the FFT front end). **Infrastructure** adapters live under
  `AudioClaudio.Infrastructure.<Area>`.
- **Test helpers**: the signal generator and WAV test-writer live in
  `AudioClaudio.Tests.Signals` (folder `Signals/`); shared fixtures/helpers
  (`RepoPaths`, `InMemoryAudioSource`, `AudioSources`, `FrameAssert`) live in
  `AudioClaudio.Tests.TestSupport` (folder `TestSupport/`). Per-area test classes
  may use `AudioClaudio.Tests.<Area>`.
- **`RepoPaths` (TestSupport) is the ONE repo-root/fixture locator** (defined in
  Step 0). It exposes `RepoRoot`, `Src(project)`, `Tests(project)`,
  `Fixture(params string[] parts)`, `SoundFontPath`, and `GoldenDirectory`. Steps
  8, 9, 11, and 12 route all fixture/root access through it — do **not** introduce a
  parallel `TestPaths` / `Fixtures` / `RepositoryRoot` walk-up.
- Tempo is the **`Tempo` value type**, never a bare `int`/`double`. Its accessor is
  `BeatsPerMinute`.
- Time-signature fields are **`BeatsPerMeasure` / `BeatUnit`**, never
  `Numerator/Denominator` or `Beats/BeatType`.

---

## 1. Domain primitives — **definer: Step 1** (`AudioClaudio.Domain`)

```csharp
public readonly record struct SampleRate
{
    public int Hz { get; }
    public SampleRate(int hz);          // throws on non-positive
}

public readonly record struct Pitch
{
    public const int MinMidi = 21;      // A0
    public const int MaxMidi = 108;     // C8
    public int MidiNumber { get; }
    public Pitch(int midiNumber);       // throws outside 21..108
    public double Frequency();          // 440 * 2^((n-69)/12)
    public static Pitch FromFrequency(double hertz);  // nearest note, MidpointRounding.AwayFromZero
}

public static class PitchMath
{
    public static double CentsBetween(double f1, double f2);   // 1200 * log2(f2/f1); throws on non-positive
}

public readonly record struct SamplePosition
{
    public long Samples { get; }
    public SampleRate Rate { get; }
    public SamplePosition(long samples, SampleRate rate);
    public double ToSeconds();          // edge/display only
    // + / - operators reject mixed rates (InvalidOperationException)
}

public readonly record struct SampleDuration
{
    public long Samples { get; }
    public SampleRate Rate { get; }
    public SampleDuration(long samples, SampleRate rate);
    public double ToSeconds();
    internal static void RequireSameRate(SampleRate x, SampleRate y);  // shared guard
}

public readonly record struct NoteEvent
{
    public const int DefaultVelocity = 64;
    public Pitch Pitch { get; }
    public SamplePosition Onset { get; }
    public SampleDuration Duration { get; }
    public int Velocity { get; }        // 0..127
    public NoteEvent(Pitch pitch, SamplePosition onset, SampleDuration duration, int velocity = DefaultVelocity);
    // enforces onset.Rate == duration.Rate
}
```

Mixed-sample-rate arithmetic throws **`InvalidOperationException`** (the
currency-mismatch rule, R1.3). Everything in this section is BCL-only (R0.2).

---

## 2. Audio source & test signals — **definer: Step 2**

```csharp
// AudioClaudio.Domain
public sealed class Frame
{
    public float[] Samples { get; }
    public SamplePosition Start { get; }
    public SampleRate Rate => Start.Rate;    // derived — NOT a ctor parameter
    public Frame(float[] samples, SamplePosition start);   // 2-arg only
}

public readonly record struct FrameParameters   // frame size N + hop H (R2.4)
{
    public int Size { get; }
    public int Hop { get; }
    public FrameParameters(int size, int hop);
}

public static class Framing
{
    public static IReadOnlyList<Frame> Split(float[] samples, SampleRate rate, FrameParameters p, long startSample = 0);
    // startSample = index of samples[0] in the wider stream, so each Frame's Start is absolute (default 0).
    // Trailing-frame convention (pinned in Step 2): one frame per hop position with start < length;
    // the final frame is ZERO-PADDED to size N (never dropped), so every input sample appears in a
    // frame. Frame count = ceil(length / hop). Step 3's Hann window tapers the padded tail to ~0.
}

// AudioClaudio.Application.Ports
public interface IAudioSource
{
    IEnumerable<Frame> Frames { get; }       // PULL model (Step 2 DECISION GATE default). A PROPERTY, not a method.
}

// AudioClaudio.Infrastructure.Audio
public sealed class WavAudioSource : IAudioSource, IDisposable
{
    public WavAudioSource(Stream wav, FrameParameters parameters);
    public static WavAudioSource FromFile(string path, FrameParameters parameters);
    public IEnumerable<Frame> Frames { get; }
    public void Dispose();                    // owns the stream when created via FromFile
}
```

Consumers open a WAV source with
`WavAudioSource.FromFile(path, new FrameParameters(frameSize, hop))` inside a
`using`. Iterate frames via the **`Frames` property** (`source.Frames`, never
`source.Frames()` or `ReadFrames()`).

### Test-only signal utilities — `AudioClaudio.Tests.Signals`

```csharp
public static class SignalGenerator
{
    public static float[] Sine(double frequencyHz, int sampleCount, SampleRate rate, double amplitude = 0.8);
    public static float[] HarmonicStack(double fundamentalHz, int sampleCount, SampleRate rate,
                                        int partials = 6, double decay = 1.0, double amplitude = 0.8);
    // decay is the partial-amplitude exponent p in 1/k^p.
}

public static class WavWriter
{
    public static byte[] Write16(float[] mono, SampleRate rate);
    public static byte[] Write24(float[] mono, SampleRate rate);            // 24-bit round-trip (Step 2 Task 7)
    public static byte[] Write16Stereo(float[] left, float[] right, SampleRate rate);
    public static void   WriteMonoFile(string path, float[] mono, SampleRate rate);   // convenience for Steps 9/10
}
```

Callers with a `Pitch`/seconds convert explicitly:
`SignalGenerator.HarmonicStack(pitch.Frequency(), (int)(seconds * rate.Hz), rate, partials, decay: p)`.
`WavWriter` is a **test** utility; the *production* WAV writer is
`WavFileWriter` (Step 8, Infrastructure) — keep the two distinct.

---

## 3. Spectral front end — **definer: Step 3** (`AudioClaudio.Domain.Spectral`)

```csharp
public static class HannWindow
{
    public static double[] Coefficients(int size);      // applied at exactly one place (R3.1)
}

public interface IFourierTransform      // in Domain — the seam the DECISION GATE hides behind
{
    System.Numerics.Complex[] Forward(double[] samples);
}

public sealed class Radix2Fft : IFourierTransform { }          // Option A — Domain, dependency-free

// Option B — AudioClaudio.Infrastructure.Spectral
public sealed class NWavesFourierTransform : IFourierTransform { }

public sealed class MagnitudeSpectrum
{
    public int BinCount { get; }                // frameSize/2 + 1
    public IReadOnlyList<double> Magnitudes { get; }
    public double FrequencyOf(int bin);         // bin * rate.Hz / frameSize
    public int PeakBin();                       // tie-break: lowest bin wins (determinism)
}

public sealed class SpectralFrontEnd
{
    public SpectralFrontEnd(int frameSize, IFourierTransform fft);   // FFT is injected
    public MagnitudeSpectrum Analyze(Frame frame);
}
```

**Dependency-rule note (load-bearing):** because the FFT is injected as the Domain
interface `IFourierTransform`, the Step 9 pipeline (in Application) never
references the concrete FFT. Under Option A it is handed `new Radix2Fft()` (Domain);
under Option B the composition root (CLI) hands it `new NWavesFourierTransform()`
(Infrastructure). Application → Infrastructure is never introduced.

---

## 4. Pitch detection — **definer: Step 4** (`AudioClaudio.Domain`)

```csharp
public readonly record struct PitchEstimate
{
    public bool IsVoiced { get; }               // NOT "Voiced"
    public double FrequencyHz { get; }
    public double Confidence { get; }           // 0..1
    public static PitchEstimate Voiced(double frequencyHz, double confidence);
    public static readonly PitchEstimate Unvoiced;
}

public sealed class YinOptions      // get-only + constructor-validated (a record `with` would bypass range checks)
{
    public double Threshold { get; }      // = 0.15 default; the named voiced/unvoiced parameter (R4.1)
    public double MinFrequencyHz { get; } // = 45.0
    public double MaxFrequencyHz { get; } // = 2500.0
    public static YinOptions Default { get; }
    public YinOptions(double threshold = 0.15, double minFrequencyHz = 45.0, double maxFrequencyHz = 2500.0);
}

public static class YinPitchDetector
{
    public static PitchEstimate Detect(Frame frame);                    // uses YinOptions.Default
    public static PitchEstimate Detect(Frame frame, YinOptions options);
}
```

Inside `Detect`, read the rate as **`frame.Rate.Hz`** (never `frame.SampleRate.Hertz`).

---

## 5. Onset detection & segmentation — **definer: Step 5** (`AudioClaudio.Domain`)

Two components, not one. Segmentation does **not** detect onsets internally.

```csharp
public sealed record OnsetDetectorOptions { /* adaptive-threshold params */ }

public sealed class OnsetDetector
{
    public OnsetDetector();
    public OnsetDetector(OnsetDetectorOptions options);
    public IReadOnlyList<int> Detect(IReadOnlyList<IReadOnlyList<double>> magnitudeSpectra);        // onset frame indices
    public IReadOnlyList<SamplePosition> DetectOnsetPositions(
        IReadOnlyList<IReadOnlyList<double>> magnitudeSpectra, IReadOnlyList<SamplePosition> frameStarts);
}

public readonly record struct FrameObservation      // one per analysis frame
{
    public SamplePosition Start { get; }
    public Pitch? Pitch { get; }            // null when unvoiced
    public double Energy { get; }
    public FrameObservation(SamplePosition start, Pitch? pitch, double energy);
}

public sealed record NoteSegmenterOptions { /* MinNoteDuration : SampleDuration (rate-carrying), etc. (R5.3) */ }

public sealed class NoteSegmenter
{
    public NoteSegmenter(NoteSegmenterOptions options);
    public IReadOnlyList<NoteEvent> Segment(
        IReadOnlyList<FrameObservation> frames, IReadOnlyList<int> onsetFrames);
}
```

The pipeline (Step 9) runs `OnsetDetector.Detect(spectra)` → onset frame indices,
builds `FrameObservation`s from the per-frame YIN estimates + energies, then calls
`NoteSegmenter(options).Segment(observations, onsetFrames)`. There is no
`SegmentationSettings` type.

---

## 6. Quantization & the Score model — **definer: Step 6** (`AudioClaudio.Domain`)

```csharp
public readonly record struct Tempo
{
    public double BeatsPerMinute { get; }
    public Tempo(double beatsPerMinute);
}

public readonly record struct TimeSignature
{
    public int BeatsPerMeasure { get; }
    public int BeatUnit { get; }
    public TimeSignature(int beatsPerMeasure, int beatUnit);
    public static TimeSignature FourFour { get; }   // (4, 4)
}

public enum Subdivision { Quarter, Eighth, Sixteenth }   // grid RESOLUTION (ticks-per-quarter = 1/2/4)
public static class SubdivisionExtensions
{
    public static int TicksPerQuarter(this Subdivision subdivision);   // grid ticks per quarter note
}
// Note VALUES (whole/half/dotted) are NOT enum members — they are handled by
// QuantizationGrid.StandardValueTicks / NearestStandardValueTicks (Step 6).

public readonly record struct QuantizationGrid
{
    public SampleRate SampleRate { get; }
    public Tempo Tempo { get; }
    public TimeSignature TimeSignature { get; }
    public Subdivision Subdivision { get; }

    public QuantizationGrid(SampleRate sampleRate, Tempo tempo, TimeSignature timeSignature, Subdivision subdivision);
    public int TicksPerBeat { get; }
    public int TicksPerMeasure { get; }
    public double SamplesPerBeat { get; }
    public double SamplesPerTick { get; }          // SamplesPerBeat / TicksPerBeat; may be fractional
    public long SamplesToTick(long samples);        // rounds half away from zero (deterministic)
    public IReadOnlyList<int> StandardValueTicks { get; }    // whole..sixteenth (+ dotted) representable on this grid, ascending
    public int NearestStandardValueTicks(double rawTicks);   // ties break toward the shorter value; never below the shortest standard value
}

public enum ElementKind { Note, Rest }

public readonly record struct ScoreElement(ElementKind Kind, Pitch? Pitch, int Velocity, int LengthTicks, bool TiedToNext)
{
    public static ScoreElement Note(Pitch pitch, int velocity, int lengthTicks, bool tiedToNext = false);
    public static ScoreElement Rest(int lengthTicks);
}

public sealed class Measure : IEquatable<Measure>
{
    public IReadOnlyList<ScoreElement> Elements { get; }
    public Measure(IReadOnlyList<ScoreElement> elements);
}

public sealed class Score : IEquatable<Score>
{
    public Tempo Tempo { get; }
    public TimeSignature TimeSignature { get; }
    public Subdivision Subdivision { get; }
    public IReadOnlyList<Measure> Measures { get; }
    public Score(Tempo tempo, TimeSignature timeSignature, Subdivision subdivision, IReadOnlyList<Measure> measures);
}

public static class Quantizer
{
    public static Score Quantize(IReadOnlyList<NoteEvent> events, QuantizationGrid grid);   // pure, static, idempotent
}
```

Consumers build a grid and quantize:
`Quantizer.Quantize(events, new QuantizationGrid(rate, new Tempo(bpm), TimeSignature.FourFour, Subdivision.Sixteenth))`.
There is no instance `new Quantizer()`, no `Score.Notes`, no `PlacedNote` type in
the domain. Step 9 may define a **local** comparison helper that *flattens*
`Score.Measures → Elements` into placed notes for its tolerance checks, but it
derives that from the canonical `Score`; it does not define an alternative `Score`.

> **⚠ Cross-cutting decision (Step 6 owns the gate):** R6.1 says "ties beyond MVP."
> The `TiedToNext` flag here is **structural bar-splitting** (a note crossing a
> barline is cut at the barline and the earlier segment carries `TiedToNext`), which
> is required for the bar-conservation invariant (R6.4) and for notating any
> barline-crossing note (Step 11). It is **not** the excluded feature (spelling a
> single note's snapped *duration* as a tie-chain of standard values). Step 6's plan
> carries a DECISION GATE stating this interpretation; Cornelius confirms it or
> chooses the stricter "constrain the corpus so no note crosses a barline" reading.
> Either way this contract's shape is unaffected.

---

## 7. MIDI I/O — **definer: Step 7** (`AudioClaudio.Infrastructure.Midi`)

Ports (in `AudioClaudio.Application.Ports`):

```csharp
public interface IScoreWriter
{
    void Write(Score score, Stream destination);
}

// The raw-performance write path. This is a 6th port beyond the constitution's
// illustrative five (IAudioSource/ITranscriber/ISynthesizer/IScoreWriter/IClock);
// it is a deliberate, documented addition justified by R7.1 (write BOTH a Score and
// a raw [NoteEvent] list). Flagged for Cornelius.
public interface INoteEventWriter
{
    void Write(IReadOnlyList<NoteEvent> events, Tempo tempo, Stream destination);
}
```

Adapters (Infrastructure):

```csharp
public sealed class DryWetMidiWriter : IScoreWriter, INoteEventWriter
{
    public void Write(Score score, Stream destination);
    public void Write(IReadOnlyList<NoteEvent> events, Tempo tempo, Stream destination);
}

public readonly record struct MidiReadResult
{
    public IReadOnlyList<NoteEvent> Events { get; }
    public Tempo Tempo { get; }              // recovered from the file's SetTempo
}

// The reader Step 7 must ship (R7.2's read-back round-trip implies it; Steps 8 & 9
// depend on it). Sample rate is NOT stored in MIDI, so it is supplied by the caller
// to denominate the recovered SamplePositions.
public static class MidiFileReader
{
    public static MidiReadResult Read(Stream source, SampleRate rate);
    public static MidiReadResult ReadFile(string path, SampleRate rate);
}
```

The single writer type is **`DryWetMidiWriter`** (never `MidiWriter` /
`DryWetMidiScoreWriter`). To write to a path, callers open a `FileStream`:
`using var fs = File.Create(path); new DryWetMidiWriter().Write(score, fs);`.

---

## 8. Synthesis & playback — **definer: Step 8**

```csharp
// AudioClaudio.Application.Ports
public interface ISynthesizer
{
    float[] Render(IReadOnlyList<NoteEvent> notes, SampleRate sampleRate);   // read rate as sampleRate.Hz
}

// AudioClaudio.Infrastructure.Synthesis
public sealed class MeltySynthSynthesizer : ISynthesizer { }   // loads the committed SoundFont once; deterministic
public sealed class WavFileWriter { }                          // PRODUCTION WAV writer (distinct from test WavWriter)
public sealed class PortAudioPlayer { }                        // first audio-device contact (output)
```

Inside the adapters, read sample rates as **`.Hz`** (`sampleRate.Hz`,
`n.Onset.Rate.Hz`). `render` and `play` CLI commands are wired in the Cli
composition root in this step; both read a `.mid` via `MidiFileReader.ReadFile(path, rate)`.

---

## 9. Transcription pipeline — **definer: Step 9** (`AudioClaudio.Application`)

```csharp
// AudioClaudio.Application.Ports
public interface ITranscriber
{
    TranscriptionResult Transcribe(IAudioSource source);
}

// AudioClaudio.Application
public sealed record TranscriptionSettings
{
    public double TempoBpm { get; init; }
    public int FrameSize { get; init; }
    public int Hop { get; init; }
    // ... YIN/onset/segmenter knobs ...
    public static TranscriptionSettings ForTempo(double bpm);
}

public sealed record TranscriptionResult(Score Score, IReadOnlyList<NoteEvent> RawEvents);

public sealed class TranscriptionPipeline : ITranscriber
{
    public TranscriptionPipeline(TranscriptionSettings settings, IFourierTransform fft);   // FFT injected (see §3)
    public TranscriptionResult Transcribe(IAudioSource source);
    public IEnumerable<NoteEvent> StreamNotes(IAudioSource source);   // incremental feed for the live `listen` view (Step 10)
}
```

The pipeline reads frames via `source.Frames`, runs `SpectralFrontEnd.Analyze`,
`YinPitchDetector.Detect`, `OnsetDetector.Detect`, `NoteSegmenter.Segment`, then
`Quantizer.Quantize`. It takes an injected `IFourierTransform`; the closed-loop
tests (which reference Domain + Infrastructure) construct it with `new Radix2Fft()`
(or the NWaves adapter, per the Step 3 decision). There is **no** `TranscriberFactory`
and **no** instance `new Quantizer()`; Step 10 constructs the pipeline directly.

### The `transcribe` CLI command (spec-gap resolution — flagged for Cornelius)

§7 requires `claudio transcribe <in.wav> --tempo …`, but no single step owned it.
Resolution: **Step 9 wires `transcribe`** to emit `raw.mid` + `score.mid` (the
pipeline and MIDI writer both exist by then) as a `feat(cli):` commit alongside the
closed-loop test; **Step 11 extends `transcribe`** to also emit `score.musicxml`
once the MusicXML writer exists.

---

## 10. Live capture — **definer: Step 10** (`AudioClaudio.Infrastructure.Audio`)

```csharp
public sealed class PortAudioAudioSource : IAudioSource, IDisposable
{
    // Same Frame contract as WavAudioSource (R10.1); bridges the device callback into
    // the pull `Frames` property via a bounded Channel<Frame> (under the Step 2 PULL default).
    public IEnumerable<Frame> Frames { get; }
    public void Dispose();
}
```

The `listen` command (Cli) constructs the pipeline directly
(`new TranscriptionPipeline(TranscriptionSettings.ForTempo(bpm) with { FrameSize=…, Hop=… }, fft)`),
prints notes from `pipeline.StreamNotes(source)`, and on stop writes the session
trio to files by opening `FileStream`s and calling `DryWetMidiWriter` (raw +
quantized MIDI) and `MusicXmlScoreWriter` (MusicXML, available from Step 11). It
compares tempo via `result.Score.Tempo.BeatsPerMinute`. Capture code contains no
transcription logic (R10.4).

---

## 11. MusicXML — **definer: Step 11** (`AudioClaudio.Infrastructure.MusicXml`)

```csharp
public sealed class MusicXmlScoreWriter : IScoreWriter
{
    public void Write(Score score, Stream destination);   // hand-rolled MusicXML 4.0; reads Score.Measures → Elements
}
```

Reads `score.TimeSignature.BeatsPerMeasure/BeatUnit` and each
`Measure.Elements` (`ScoreElement.Kind`, `Pitch`, `LengthTicks`, `TiedToNext`).
The MuseScore load check (R11.2) is recorded in **`DECISIONS.md`**, not README
(Step 12 owns the README).

---

## 12. Phase 2 — polyphony (Stages 1–5) — behind the same `ITranscriber` port

> Landed on `main` across a Phase-2 effort tracked in `IMPLEMENTATION_PLAN.md` (deleted once its
> Stage 5 — CLI + docs — ships; this section is where these signatures survive that deletion). Every
> type below is **additive**: the monophonic `Score`/`Measure`/`ScoreElement`/`Quantizer`/
> `MusicXmlScoreWriter`/`TranscriptionPipeline` contract in §§3–11 is untouched. Polyphony is reached
> through a second `ITranscriber` adapter (`BasicPitchTranscriber`) — the **default** `transcribe`
> engine as of v0.2.0, `--mono` opting back to the monophonic path — and the new `evaluate` command.

### 12.1 Evaluation — **definer: Stage 1** (harness) **+ DTW** (alignment) (`AudioClaudio.Domain.Evaluation`)

```csharp
public sealed record NoteMatchOptions(double OnsetToleranceSeconds)
{
    public static NoteMatchOptions Default { get; } = new(0.05);   // ±50 ms, exact pitch — standard MIR tolerance
}

public readonly record struct NoteSetEvaluation(
    int TruePositives, int FalsePositives, int FalseNegatives, int ReferenceCount, int CandidateCount)
{
    public double Precision { get; }   // TP / CandidateCount
    public double Recall { get; }      // TP / ReferenceCount
    public double F1 { get; }          // harmonic mean; 0 when both are 0
}

public static class TranscriptionEvaluator
{
    public static NoteSetEvaluation Evaluate(
        IReadOnlyList<NoteEvent> candidate, IReadOnlyList<NoteEvent> reference, NoteMatchOptions options);
}

public static class OnsetAlignment
{
    public static IReadOnlyList<NoteEvent> GlobalScale(IReadOnlyList<NoteEvent> candidate, IReadOnlyList<NoteEvent> reference);
    public static IReadOnlyList<NoteEvent> DtwWarp(IReadOnlyList<NoteEvent> candidate, IReadOnlyList<NoteEvent> reference);
}
```

Matching (`Evaluate`) is one-to-one and deterministic: exact MIDI pitch (an octave error is a miss) +
onset within `OnsetToleranceSeconds`; references walked in ascending `(onset-seconds, pitch, index)`
order, each greedily claiming its nearest unclaimed equal-pitch candidate (a deliberate greedy, not
optimal-bipartite, matching — see `DECISIONS.md`). `OnsetAlignment` re-times a *candidate* onto a
*reference*'s time base before scoring, using onset times only (never pitch), so F1 reflects pitch
recovery not tempo drift; both are pure, never mutate input, and return the candidate unchanged when
empty. `GlobalScale` = one linear span rescale (gross tempo only); `DtwWarp` = monotonic DP over
onset-time distance → piecewise-linear warp (cancels *local* rubato; no-ops if either side has <2
distinct onsets). Wired to `claudio evaluate <candidate.mid> <reference.mid> [--onset-tolerance-ms 50]
[--align|--warp]` (§12.9); `--warp` wins over `--align`.

### 12.2 Polyphonic note decoding — **definer: Stage 2b** (`AudioClaudio.Domain.Polyphony`)

```csharp
public sealed record BasicPitchNote(int StartFrame, int EndFrame, int MidiPitch, double Amplitude);
// frame units (caller converts to samples); MidiPitch = 21 + pitch-bin index; Amplitude ≈ mean activation 0..1

public sealed record NoteDecoderOptions(
    double OnsetThreshold = 0.5, double FrameThreshold = 0.3, int MinNoteLenFrames = 11,
    bool InferOnsets = true, bool MelodiaTrick = true, int EnergyTolerance = 11)
{
    public static NoteDecoderOptions Default { get; } = new();   // Basic Pitch's stock thresholds
}

public static class BasicPitchNoteDecoder
{
    public static IReadOnlyList<BasicPitchNote> Decode(float[,] frames, float[,] onsets, NoteDecoderOptions options);
}
```

`frames`/`onsets` are `[frame, pitchBin]` posteriorgrams (88 bins). `Decode` is a pure, deterministic
port of Basic Pitch's `output_to_notes_polyphonic` (Apache-2.0): peak-pick onsets, walk each forward to
an energy drop (consuming the pitch band + neighbours so overtones don't spawn duplicates), discard
sub-`MinNoteLenFrames` notes, then (optionally) the melodia sweep for onset-less sustained notes.

### 12.3 Resampling — **definer: Stage 2c** (`AudioClaudio.Domain`, `AudioResampler.cs`)

```csharp
public static class AudioResampler
{
    public static float[] Resample(float[] input, int inRate, int outRate, int lobes = 4);
}
```

Band-limited Lanczos (windowed-sinc); on downsampling the kernel widens to the *output* Nyquist to
prevent aliasing; weights normalised so a constant passes unchanged. Pure, buffer-level only (no
`SamplePosition`/`SampleRate` carried). Brings 44.1 kHz audio to the model's 22 050 Hz.

### 12.4 The polyphonic transcriber — **definer: Stage 2a/2d** (`AudioClaudio.Infrastructure.Transcription`)

```csharp
public sealed class BasicPitchModel : IDisposable
{
    public const int SampleRateHz = 22050;
    public const int WindowSamples = 43844;      // ~1.99 s per window
    public const int FramesPerWindow = 172;
    public const int PitchBins = 88;             // the 88 piano keys
    public const int ContourBins = 264;          // 88 * 3 bins/semitone

    public BasicPitchModel(string modelPath);
    public BasicPitchWindowOutput Run(ReadOnlySpan<float> window);   // window.Length must == WindowSamples
    public void Dispose();
}

public sealed record BasicPitchWindowOutput(float[,] NoteFrames, float[,] Onsets, float[,] Contours);

public sealed class BasicPitchTranscriber : ITranscriber, IDisposable
{
    public BasicPitchTranscriber(
        string modelPath, NoteDecoderOptions? decoderOptions = null,    // default NoteDecoderOptions.Default
        Tempo? tempo = null,                                            // default new Tempo(120)
        TimeSignature? timeSignature = null, Subdivision subdivision = Subdivision.Sixteenth);
    public TranscriptionResult Transcribe(IAudioSource source);   // ITranscriber
    public void Dispose();
}
```

`Transcribe` reconstructs the mono signal (`Framing.ReconstructMono`), resamples to 22 050 Hz (§12.3),
runs the model over overlapping front-padded windows (stitched by trimming the 30-frame overlap),
decodes (§12.2), maps frames→`SamplePosition` at 22 050 Hz, and returns events sorted by `(onset,
pitch)`. **`TranscriptionResult.RawEvents` is the honest many-note output; this class's own
`TranscriptionResult.Score` is still quantized by the monophonic `Quantizer`** — the real grand-staff
score is built by the *caller* (CLI, §12.9) via `PolyphonicQuantizer` + `GrandStaffMusicXmlWriter`/
`GrandStaffFlattener`. This is the **default** `transcribe` engine (v0.2.0; `--mono` opts out). CPU
inference is deterministic per build but not bit-identical across CPU architectures (SIMD), same as §8.

**Dependency-rule note:** `Microsoft.ML.OnnxRuntime` is referenced only by
`AudioClaudio.Infrastructure.csproj` (zero `PackageReference`s in `AudioClaudio.Domain.csproj`).
`BasicPitchNoteDecoder`/`NoteDecoderOptions`/`BasicPitchNote` (§12.2), `PitchSpeller` (§12.8), and
`AudioResampler` (§12.3) are pure Domain/BCL; only `BasicPitchModel`/`BasicPitchTranscriber` touch ONNX,
through the existing `ITranscriber` port — no new inward-pointing import.

### 12.5 Grand-staff score building — **definer: Stage 3a–3c** (`AudioClaudio.Domain.Polyphony`)

> **Parallel types, monophonic path untouched** (see `DECISIONS.md`). Model = **homophonic-per-staff**:
> a chord is a set of pitches sharing a quantized onset+duration; each staff is a sequence of chords.

```csharp
public enum Staff { Treble, Bass }

public sealed record Chord(SamplePosition Onset, IReadOnlyList<Pitch> Pitches, SampleDuration Duration, int Velocity);

public static class ChordGrouper
{
    public static IReadOnlyList<Chord> Group(IReadOnlyList<NoteEvent> events, SampleDuration window);
}

public static class StaffSplitter
{
    public const int DefaultSplitMidi = 60;   // middle C: this note and above → treble, below → bass
    public static (Chord? Treble, Chord? Bass) Split(Chord chord, int splitMidi = DefaultSplitMidi);
}

public readonly record struct ChordElement(
    ElementKind Kind, IReadOnlyList<Pitch> Pitches, int Velocity, int LengthTicks, bool TiedToNext)
{
    public static ChordElement Note(IReadOnlyList<Pitch> pitches, int velocity, int lengthTicks, bool tiedToNext = false);
    public static ChordElement Rest(int lengthTicks);
}

public sealed record GrandStaffMeasure(IReadOnlyList<ChordElement> Treble, IReadOnlyList<ChordElement> Bass);

public sealed record GrandStaffScore(
    Tempo Tempo, TimeSignature TimeSignature, Subdivision Subdivision, IReadOnlyList<GrandStaffMeasure> Measures);

public static class PolyphonicQuantizer
{
    public static GrandStaffScore Quantize(
        IReadOnlyList<NoteEvent> events, QuantizationGrid grid, SampleDuration chordWindow,
        int splitMidi = StaffSplitter.DefaultSplitMidi);
}
```

`ChordGrouper.Group` groups notes within `window` of a chord's **anchor** (its earliest note, not
chained), keeping one representative per pitch; the `Chord`'s `Duration`/`Velocity` are the max across
its pitches. `ChordElement` reuses the existing `ElementKind` enum (§6) and the same structural-tie
convention as `ScoreElement`. `GrandStaffScore` mirrors `Score`'s field order. `PolyphonicQuantizer.Quantize`:
group → split each chord → lay each staff independently (snap via `grid.SamplesToTick`/
`NearestStandardValueTicks`, clip to next onset, gap-fill rests, bar-split), both staves padded to the
same measure count. Pure/deterministic; throws `ArgumentException` on a sample-rate mismatch.

### 12.6 Grand-staff MusicXML — **definer: Stage 3d** (`AudioClaudio.Infrastructure.MusicXml`)

```csharp
public sealed class GrandStaffMusicXmlWriter
{
    public GrandStaffMusicXmlWriter(bool includeNoteNames = false, string? workTitle = null, int fifths = 0);
    public void Write(GrandStaffScore score, Stream destination);      // UTF-8, no BOM
    public string WriteToString(GrandStaffScore score);                 // MusicXML 4.0, LF newlines
}
```

One piano `<part>`, `<staves>2</staves>` (treble = voice 1/staff 1, bass = voice 2/staff 2); chord
pitches are sibling `<note>`s with `<chord/>`; a `<backup>` rewinds between staves; non-standard/tied
lengths spell as a tied run of standard values. `fifths` drives both the `<fifths>` element and every
pitch's spelling via `PitchSpeller.Spell` (§12.8). Deliberately does **not** reuse the monophonic
`MusicXmlScoreWriter` (§11), so that writer's byte-exact golden cannot be disturbed. Covered by
structural + bar-conservation + key-spelling tests (no byte-exact grand-staff golden — see `DECISIONS.md`).

### 12.7 Flattening back to `NoteEvent` — **definer: Stage 3e** (`AudioClaudio.Domain.Polyphony`)

```csharp
public static class GrandStaffFlattener
{
    public static IReadOnlyList<NoteEvent> ToNoteEvents(GrandStaffScore score, QuantizationGrid grid);
}
```

Merges tied runs into one `NoteEvent` per pitch, converts ticks→samples via `grid.SamplesPerTick`,
sorts by `(onset, pitch)`. Lets the existing §7 `INoteEventWriter` write a polyphonic `score.mid`.

### 12.8 Enharmonic spelling — **definer: Stage 4c** (`AudioClaudio.Domain`, `PitchSpeller.cs`)

```csharp
public static class PitchSpeller
{
    public static (string Step, int Alter, int Octave) Spell(int midiNumber, int fifths);
}
```

`fifths`: sharps positive, flats negative (A♭ major = −4). Line-of-fifths, nearest-to-key-centre:
diatonic natural, chromatics in the key's accidental direction, ties → fewer accidentals. Pure,
deterministic. Consumed by `GrandStaffMusicXmlWriter` (§12.6).

### 12.9 CLI wiring — **definer: Stage 1/4b/5** (`AudioClaudio.Cli`)

```csharp
public static class PolyDecoderOptions          // Cli.Commands
{
    public static NoteDecoderOptions FromArgs(string[] args);
}

public enum TranscribeMode { Monophonic, Polyphonic }   // Cli.Commands
public static class TranscribeModeResolver
{
    public static TranscribeMode Resolve(string[] args);   // --mono → Monophonic (wins); else Polyphonic (default)
}

public static class EvaluateCommand
{
    public static NoteSetEvaluation Run(
        IReadOnlyList<NoteEvent> candidate, IReadOnlyList<NoteEvent> reference,
        NoteMatchOptions options, Action<string> print);
}
```

`PolyDecoderOptions.FromArgs` reads `--onset-threshold`/`--frame-threshold`/`--min-note-len`, each
defaulting to `NoteDecoderOptions.Default` (Stage 4b's honest-default rule). `transcribe` runs the
polyphonic path by **default** (v0.2.0); `TranscribeModeResolver.Resolve` selects it unless `--mono` is
passed, in which case `Program.cs` calls the monophonic `TranscribeCommand.Run` instead. The polyphonic
branch (wired directly in `Program.cs`, not `TranscribeCommand`) resolves the model via
`ModelLocator.Resolve` (mirrors §8's `SoundFontLocator`), constructs `BasicPitchTranscriber`, writes
`raw.mid` from `RawEvents`, then quantizes `RawEvents` through `PolyphonicQuantizer` into a
`GrandStaffScore` and writes `score.musicxml` (`GrandStaffMusicXmlWriter`) + `score.mid`
(`GrandStaffFlattener` → `DryWetMidiWriter`). `--key` is a **declared** key signature (like `--tempo`,
R6.3), never estimated. `claudio evaluate <candidate.mid> <reference.mid> [--onset-tolerance-ms]
[--align|--warp]` is wired alongside `render`/`play`; both MIDIs read via `MidiFileReader` (§7).

---

## Quick divergence table (what changed vs. the first drafts)

| Symbol | Canonical (this file) | Wrong forms seen in drafts |
|---|---|---|
| Sample rate accessor | `SampleRate.Hz` | `.Hertz` (Steps 2,4,8,10) |
| Frame start / rate | `Frame.Start`, `Frame.Rate` (derived), 2-arg ctor, `sealed class` | `.Position`, `.SampleRate`, `.Length`, 3-arg ctor, `record` |
| Audio source member | `IAudioSource.Frames` (property) | `Frames()`, `ReadFrames()` |
| WAV adapter | `WavAudioSource.FromFile(path, FrameParameters)`, `IDisposable` | `WavFileAudioSource`, `(path, frameSize, hop)` ctor, non-disposable |
| Test WAV writer | `WavWriter.Write16/WriteMonoFile` | `WavFile.WriteMono/Write` |
| Detector | `static YinPitchDetector.Detect(Frame, YinOptions)`, `PitchEstimate.IsVoiced` | `new YinPitchDetector(threshold)`, `.Voiced` |
| Segmentation | `OnsetDetector` + `NoteSegmenter(NoteSegmenterOptions).Segment(obs, onsetFrames)` | single `NoteSegmenter(SegmentationSettings)` |
| Quantizer | `static Quantizer.Quantize(events, QuantizationGrid)` | `new Quantizer().Quantize(events, bpm, ts, sub)` |
| Tempo | `Tempo.BeatsPerMinute` | `.Bpm`, bare `int`/`double` |
| Time signature | `BeatsPerMeasure`/`BeatUnit` | `Numerator/Denominator`, `Beats/BeatType` |
| Score | `Score(Tempo, TimeSignature, Subdivision, Measures)`, `.Measures`/`.Elements`/`ScoreElement` | `.Notes`/`PlacedNote`, `ScoreItem/ScoreNote/ScoreRest`, `MeasureElement/NoteValue` |
| MIDI writer | `DryWetMidiWriter.Write(Score/events+Tempo, Stream)` | `MidiWriter`, `DryWetMidiScoreWriter`, path-based |
| MIDI reader | `MidiFileReader.Read(Stream, SampleRate)` / `ReadFile(path, SampleRate)` → `MidiReadResult` | unbuilt; `MidiReader.Read/ReadEvents` |
| Score writer port | `IScoreWriter.Write(Score, Stream)` | `WriteScore(Score, path)` |
| Transcriber return | `ITranscriber.Transcribe(IAudioSource) → TranscriptionResult`; live feed via `TranscriptionPipeline.StreamNotes` | `IEnumerable<NoteEvent> Transcribe(...)` |
| Pipeline ctor | `TranscriptionPipeline(TranscriptionSettings, IFourierTransform)` | `(TranscriptionSettings)` only; `TranscriberFactory.CreateDefault` |
| Ports namespace | `AudioClaudio.Application.Ports` | flat `AudioClaudio.Application` (Steps 2, 9) |
