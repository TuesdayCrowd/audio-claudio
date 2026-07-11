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

`MidiFileReader.Read` folds the **sustain pedal** into note durations via
`AudioClaudio.Domain.SustainPedal.Flatten(notes, IReadOnlyList<SustainPedal.Change>)` — a pure
Domain function that extends a note released while the pedal (CC64 ≥ 64) is down out to the pedal's
release, so a pedalled MIDI does not synthesize dry through the pedal-less note→synth path. A no-op
when there are no CC64 events (our own writer emits none).

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
R6.3) that **overrides** the Stage 3 auto-detected default (§12.10) — never estimated itself.
`claudio evaluate <candidate.mid> <reference.mid> [--onset-tolerance-ms]
[--align|--warp]` is wired alongside `render`/`play`; both MIDIs read via `MidiFileReader` (§7).

### 12.10 Key detection — **definer: v2 Stage 3b** (`AudioClaudio.Domain`, `KeyDetector.cs`)

```csharp
public static class KeyDetector
{
    public static int Detect(IReadOnlyList<Pitch> pitches);                    // occurrence-weighted
    public static int DetectFromProfile(IReadOnlyList<double> pitchClassWeights);  // 12 bins, index 0 = C
}
```

Krumhansl-Schmuckler key-finding: a 12-bin pitch-class weight profile is Pearson-correlated against the
Krumhansl-Kessler major/minor tonal-hierarchy profiles rotated to all 24 tonics; the best-correlating key's
signature (**fifths** — sharps +, flats −) wins, ties breaking toward fewest accidentals. An empty/flat
profile returns `0` (C major). Pure, deterministic, BCL-only. This is a **declared-vs-detected default**
(same shape as auto-tempo — `CLAUDE.md` §8 item 2): the CLI's `transcribe`/`notate` (§12.9) call `Detect` when
`--key` is omitted, validated/overridden via `KeyOption` (§12.13); the resulting fifths feeds `PitchSpeller`
(§12.8) and `GrandStaffMusicXmlWriter`'s `<fifths>` (§12.6). Measured 92.5% key accuracy on the notation
corpus (gate ≥ 85%, see `DECISIONS.md` "v2 Stage 3").

### 12.11 Temporal hand-split — **definer: v2 Stage 3c** (`AudioClaudio.Domain.Polyphony`, `HandSplitter.cs`)

```csharp
public sealed class HandSplitter
{
    public const double DefaultAlpha = 0.6;
    public const double DefaultLeftCentre = 52.0;   // E3
    public const double DefaultRightCentre = 67.0;  // G4

    public HandSplitter(double leftCentre = DefaultLeftCentre, double rightCentre = DefaultRightCentre,
        double alpha = DefaultAlpha);
    public (Chord? Treble, Chord? Bass) SplitNext(Chord chord);   // advances the two hand centres

    public static IReadOnlyList<(Chord? Treble, Chord? Bass)> Split(
        IReadOnlyList<Chord> chords,
        double leftCentre = DefaultLeftCentre, double rightCentre = DefaultRightCentre, double alpha = DefaultAlpha);
}
```

Replaces the fixed middle-C cut (`StaffSplitter`, §12.5 — still present but now unused by
`PolyphonicQuantizer`, kept as the documented baseline) with **temporal hand-tracking**: two running hand
centres (EMAs of each hand's recent pitches, seeded at a middle-C-straddling fifth) follow the two lines
over time, and each chord is split at the contiguous low/high boundary minimizing total distance to the two
centres, ties breaking toward the most balanced split. Because the centres move with the music, a hand's
line that crosses middle C keeps its notes — the case the fixed cut gets wrong; it cannot recover an
*isolated* leap (no continuity = no hand signal). Reproduces the fixed cut on non-crossing input, so
existing `PolyphonicQuantizer`/grand-staff goldens are unchanged. Deterministic; `SplitNext` is the
stateful per-chord API, `Split` wraps a fresh instance over a whole onset-ordered sequence.
**`PolyphonicQuantizer.Quantize` (§12.5) now calls `HandSplitter.Split(chords)` unconditionally — its
signature dropped the `splitMidi` parameter** (`Quantize(IReadOnlyList<NoteEvent> events, QuantizationGrid
grid, SampleDuration chordWindow)`, no fourth argument); §12.5's code block predates this and should be read
with that correction. Measured 89.1% (fixed cut) → 100.0% (tracker) on `HandCrossingGen` (gate ≥ 97%).

### 12.12 Triplets — **definer: v2 Stage 3d** (`AudioClaudio.Domain` + `AudioClaudio.Infrastructure.MusicXml`)

```csharp
// AudioClaudio.Domain (Subdivision.cs)
public enum Subdivision { Quarter, Eighth, Sixteenth, Twelfth }   // Twelfth added: 12 ticks/quarter
// SubdivisionExtensions.TicksPerQuarter(Subdivision.Twelfth) == 12
```

`Subdivision.Twelfth` is the LCM of the sixteenth grid (4/quarter) and the eighth-triplet grid (3/quarter),
so both land on integer ticks. `QuantizationGrid.StandardValueTicks` (§6) gained six triplet values
(half/quarter/eighth/sixteenth-note triplets, in `valuesInQuarters` as `(4,3)/(2,3)/(1,3)/(1,6)` — see the
`QuantizationGrid.cs` source) alongside the existing straight+dotted values; each is included **only** when
`ticksPerQuarter * num % den == 0`, which is true on `Twelfth` and false on `Quarter`/`Eighth`/`Sixteenth` —
so the straight-only grids (and the monophonic bit-exact closed loop) are provably untouched.
`GrandStaffMusicXmlWriter` (§12.6) engraves triplets when a chord/rest's tick length decomposes to a clean
triplet value (or, for odd lengths, a mixed straight+sixteenth-triplet fallback that never leaves an
unrepresentable remainder): each triplet note gets a `<time-modification><actual-notes>3</actual-notes>
<normal-notes>2</normal-notes></time-modification>`, and complete runs of three consecutive eighth-note
triplets get a `<tuplet type="start"/>`/`type="stop"/` bracket (a broken/partial run gets none, but still
carries the time-modification). Straight decomposition is byte-identical to before (straight values are
still whole sixteenths), so the non-triplet golden is undisturbed. Wired **opt-in** via `--triplets`
(`Program.cs` selects `Subdivision.Twelfth` over the `Subdivision.Sixteenth` default on `transcribe`/
`notate`) — off by default because auto-quantizing to triplets manufactures spurious triplets on straight
music (the same discipline as `--legato`). Note-value recovery 76.5% → 100.0% at the Twelfth grid on the
notation corpus (gate ≥ 98%).

### 12.13 `--key` validation — **definer: v2 Stage 3b review** (`AudioClaudio.Cli.Commands`, `KeyOption.cs`)

```csharp
public static class KeyOption
{
    public const int MinFifths = -7;   // C-flat major
    public const int MaxFifths = 7;    // C-sharp major

    public static bool TryParse(string? raw, out int fifths, out string? error);
}
```

Extracted from an unvalidated `int.Parse` that could crash `PitchSpeller` (`Math.Abs(int.MinValue)`) or emit
a garbage `<fifths>` on an out-of-range value. `TryParse` accepts only the twelve standard key signatures,
`[-7, +7]`; outside that (or non-integer) it returns `false` with a descriptive `error` and `fifths = 0`.
`Program.cs`'s `TryReadKeyOverride` helper wraps this for the `transcribe`/`notate` `--key` option.

---

## 13. Transkun engine — self-contained via ONNX — **definer: v2 Stage 4** — a third `ITranscriber`

> Ranked below the Basic Pitch path in guarantee tier (see `CLAUDE.md` "Where the project is right now" and
> `DECISIONS.md` "v2 Stage 4"): **statistical accuracy + a ≥ 99% PyTorch-parity gate**, not (yet) a
> closed-loop F1 gate of its own. A faithful C#/ONNX port of Yujia Yan's Transkun (Neural Semi-CRF,
> NeurIPS 2021, MIT), selectable via `transcribe --model transkun` alongside the default Basic Pitch path
> (§12.4) and the monophonic `--mono` path (§9). The committed artifact — both ONNX graphs, the frozen
> front-end buffers, license, decode spec, `MODEL_CARD.md` — lives under `fixtures/models/transkun/`
> (repo-only for now; a public HuggingFace push is deferred to Cornelius's later go-ahead, per
> `DECISIONS.md` "4f").

**Boundary split (what is ONNX vs. C#):** `torch.fft.rfft` (the mel front end) and the semi-CRF
backtracking (`viterbiBackward`) are not ONNX-exportable, so both are reimplemented in C# — the mel front
end (§13.2) and the Viterbi decode (§13.3), each validated against committed PyTorch fixtures. The
transformer + semi-CRF scorer (+ backbone `ctx` + the two velocity/sub-frame attribute heads) stay in ONNX
(§13.4), run in-process via `Microsoft.ML.OnnxRuntime` (the same runtime Basic Pitch uses) — no Python at
runtime.

### 13.1 Frozen constants — (`AudioClaudio.Infrastructure.Transcription`, `TranskunBuffers.cs`)

```csharp
public sealed class TranskunBuffers
{
    public float[] Freq2Mels { get; }     // mel filterbank, row-major [rfftBins, nMels] (index k*nMels + m)
    public float[][] Windows { get; }     // [nWindows][windowSize]; row 0 Hann, 1..5 learned Gaussian
    public int[] Symbols { get; }         // the 90 track symbols: [-64, -67, 21..108] (sustain, soft, MIDI 21-108)
    public TranskunParams Params { get; }

    public static TranskunBuffers Load(string directory);   // reads params.json + the raw f32/i32 buffer files
}

public sealed record TranskunParams
{
    public int Fs { get; init; }
    public int WindowSize { get; init; }
    public int HopSize { get; init; }
    public int NMels { get; init; }
    public int NWindows { get; init; }
    public double Eps { get; init; }
    public double FMin { get; init; }
    public double FMax { get; init; }
    public int RfftBins { get; init; }
    public double SegmentSizeSeconds { get; init; }
    public double SegmentHopSeconds { get; init; }
    public int NSymbols { get; init; }
}
```

Loads the committed `fixtures/models/transkun/` buffers — raw little-endian float32/int32 matching the
export's `.tofile`, so the load is a byte copy on the little-endian platforms this project targets.

### 13.2 Mel front end — (`AudioClaudio.Infrastructure.Transcription`, `TranskunMelFrontEnd.cs`)

```csharp
public sealed class TranskunMelFrontEnd
{
    public TranskunMelFrontEnd(TranskunBuffers buffers, IFourierTransform fft);   // FFT injected, per §3
    public float[,,] Compute(ReadOnlySpan<float> audio);   // -> featuresBatch [nFrame, nMels, nWindows]
}
```

Mono audio → `featuresBatch`, the exact input the exported ONNX (§13.4) expects: `makeFrame` (half-window
left pad, right pad to the last full frame) → per-segment gain normalization (unbiased mean/std over every
framed sample) → six analysis windows → `rfft(norm="ortho")` → power → mel filterbank (each triangular
filter's nonzero rfft-bin range precomputed so the matmul skips zeros, ~100× faster than the dense product)
→ log-normalize. Pure given its buffers and injected `IFourierTransform` (`torch.fft.rfft` is not
ONNX-exportable, which is why this lives outside the model graph). Verified against the committed `ref3b`
PyTorch fixture to ~7e-6 drift.

### 13.3 Semi-CRF Viterbi decode — **BCL-only** (`AudioClaudio.Domain`, `SemiCrfViterbi.cs`)

```csharp
public static class SemiCrfViterbi
{
    public readonly record struct Interval(int Begin, int End);   // closed frame interval [Begin, End]

    public static IReadOnlyList<IReadOnlyList<Interval>> Decode(
        float[] score, int t, int nBatch, IReadOnlyList<int>? forcedStartPos = null);
}
```

A faithful port of transkun's `viterbiBackward` (the data-dependent backtracking that is not
ONNX-exportable). `score` is the flat `[T,T,nBatch]` row-major score matrix `S` (`score[e,b,k] =
score[(e*T+b)*nBatch+k]`, `S[e,b,k]` scoring a note on track `k` spanning closed interval `[b,e]`; the skip
score is provably zero for this model and is baked in). `forcedStartPos` (per track) resumes decoding at a
frame — used by the Stage 4d segment-stitching carry; `null` starts every track at frame 0. Pure,
deterministic, BCL-only (Domain) — verified EXACT against the committed `ref3c` fixture on synthetic and
real `S`.

### 13.4 ONNX runners — (`AudioClaudio.Infrastructure.Transcription`)

```csharp
public sealed class TranskunModel : IDisposable
{
    public TranskunModel(string onnxPath);
    public float[] Run(float[,,] featuresBatch, out int t);                      // -> S flat [T*T*90]
    public (float[] S, float[] Ctx) RunWithCtx(float[,,] featuresBatch, out int t);  // + ctx flat [90*T*256]
    public void Dispose();
}

public sealed class TranskunHeads : IDisposable
{
    public const int AttrDim = 768;         // [ctx_a, ctx_b, ctx_a·ctx_b], each 256-D
    public const int VelocityClasses = 128;

    public TranskunHeads(string onnxPath);
    public (float[] VelocityLogits, float[] OfRaw) Run(float[] attr, int n);   // attr flat [n*768]
    public void Dispose();
}
```

`TranskunModel` runs the main graph (input `featuresBatch`, output `S`; `RunWithCtx` also returns the
backbone features `ctx[track,frame,d]`, needed for the attribute heads). `TranskunHeads` runs the small
velocity/sub-frame-timing MLPs (`velocityPredictor`, `refinedOFPredictor`) over gathered per-interval
features (`attr` = the endpoint `ctx` vectors and their elementwise product); `OfRaw`'s four columns per row
are two sub-frame value logits (decoded as a ContinuousBernoulli mean, recentred to `[-0.5, 0.5]`) and two
presence logits. Neither type is Domain — `Microsoft.ML.OnnxRuntime` stays out of
`AudioClaudio.Domain.csproj` (the same dependency-rule note as §12.4).

### 13.5 The transcriber — (`AudioClaudio.Infrastructure.Transcription`, `TranskunTranscriber.cs`)

```csharp
public sealed class TranskunTranscriber : ITranscriber, IDisposable
{
    public TranskunTranscriber(string modelDir, IFourierTransform fft);

    public (IReadOnlyList<NoteEvent> Notes, IReadOnlyList<SustainPedal.Change> Pedal) TranscribeDetailed(IAudioSource source);
    public TranscriptionResult Transcribe(IAudioSource source);   // ITranscriber; Score is a lossy mono quantization
    public void Dispose();
}
```

Composes §13.1–13.4 over the model's 16 s segments at 8 s hop (padded, overlapping), stitched exactly as
transkun's `transcribe`/`transcribeFrames`: the `forcedStartPos` carry, merge-across-segments
(replace/extend/append/drop by onset/overlap), EOF force-close of the last open note per track, and a
final `resolveOverlapping` truncation pass. `TranscribeDetailed` is the honest output — real per-note
**velocity** and **sub-frame** onset/offset timing (Stage 4e's two heads), plus `SustainPedal.Change`
events decoded from the dedicated CC64 track (track symbol `-64`; the soft-pedal track `-67` is decoded but
not emitted — a documented core-first limitation). `Transcribe` (the `ITranscriber` member) quantizes those
notes through the monophonic `Quantizer` at a fixed 120 BPM/sixteenth grid — same honest-raw-vs-lossy-score
split as `BasicPitchTranscriber` (§12.4); the CLI's real grand-staff output comes from feeding
`TranscribeDetailed`'s notes through `PolyphonicQuantizer` + `GrandStaffMusicXmlWriter` itself (§12.9).
Resamples input audio to the model's rate (`Fs`, from `TranskunParams`) via `AudioResampler` (§12.3) when
the source differs. CPU inference is deterministic per build, not bit-identical across CPU architectures
(SIMD) — same caveat as §8/§12.4.

### 13.6 CLI wiring — (`AudioClaudio.Cli.Composition`, `TranskunModelLocator.cs`)

```csharp
public static class TranskunModelLocator
{
    public static string Resolve(string? explicitDir = null);   // walks up from AppContext.BaseDirectory
}
```

Mirrors `ModelLocator` (§12.9): resolves the committed `fixtures/models/transkun/` directory (the one
holding `transkun.onnx`) by walking up from the executable, or returns an explicit path. `Program.cs`
selects the Transkun branch when `transcribe --model transkun` is passed AND the resolved mode (§12.9's
`TranscribeModeResolver`) is `Polyphonic` (i.e. `--mono` was not also given); it writes `raw.mid` from
`TranscribeDetailed`'s notes, quantizes them via `PolyphonicQuantizer` onto `Subdivision.Twelfth` or
`Sixteenth` per `--triplets` (§12.12), and writes `score.musicxml`/`score.mid` including the sustain-pedal
marks (`GrandStaffMusicXmlWriter`'s pedal-marks `Write` overload). `--key` behaves as in §12.9/§12.10.

### 13.7 The parity gate — `TranskunParityTests` (`tests/AudioClaudio.Tests/Transcription/`)

The engine's earned guarantee, ranked below closed-loop proof (see `DECISIONS.md` "v2 Stage 4"): the C#
`TranskunTranscriber` output is compared, per committed fixture clip, against a **native `transkun` CLI
(PyTorch) reference MIDI** committed under `fixtures/models/transkun/parity/` (so the gate runs in CI with
no Python venv). Asserts `NoteSetEvaluation.F1 >= 0.99` at ±25 ms onset tolerance (`TranscriptionEvaluator`,
§12.1) and mean absolute velocity delta ≤ 2 across matched notes. Measured **100.0% F1 with exact velocity**
on every note across both fixture clips (a 3-segment monophonic scale and a 21.8 s cross-boundary-stitching
clip) — the decisive test isolating a C#-port bug from model accuracy, per the constitution's ranked
guarantee hierarchy (mono bit-exact / poly statistical F1 / Transkun statistical + ≥99% PyTorch parity).

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
