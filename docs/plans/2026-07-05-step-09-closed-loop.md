# Step 9 — The Closed Loop — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Build-spec step:** Section 6 Step 9 (R9.1, R9.2, R9.3, R9.4)
**Goal:** Compose Steps 2–8 into the property that sells the project — generate a random constrained monophonic score, synthesize it, transcribe the audio back through the full pipeline at the known tempo, and demand the score returns within tolerance (`transcribe ∘ synthesize ≈ id`).
**Architecture:** This step first assembles the transcription pipeline as an Application service `TranscriptionPipeline : ITranscriber` (the port `ITranscriber` lives in `AudioClaudio.Application.Ports`) that wires the pure Domain algorithms (spectral front end → YIN → two-stage onset detection + segmentation → quantization) behind the `IAudioSource` port and returns a `Score`. The FFT is injected as the Domain interface `IFourierTransform` (CONTRACTS §3), so the pipeline constructor is `TranscriptionPipeline(TranscriptionSettings, IFourierTransform)` and never references the concrete FFT or any Infrastructure type. This step also wires the `claudio transcribe <in.wav> --tempo N` CLI command (CONTRACTS §9: Step 9 owns `transcribe`, emitting `raw.mid` + `score.mid`; `score.musicxml` is added in Step 11). The closed-loop suite itself lives entirely in `tests/AudioClaudio.Tests`; being the composition root for the test, it is the only place that constructs Infrastructure adapters (`WavAudioSource`, `MeltySynthSynthesizer`, `DryWetMidiWriter`, `MidiFileReader`) and hands them (plus `new Radix2Fft()`) to the Application pipeline. The pipeline never references Infrastructure — the dependency rule stays physical.
**Tech Stack:** xUnit (Apache-2.0), CsCheck (MIT) generators + `Sample`, DryWetMIDI (Step 7 adapter, for quarantine/regression MIDI), MeltySynth (Step 8 adapter, the oracle), the Step 2 WAV adapter + WAV writer.
**Prerequisites:** Steps 0–8 green and committed (Section 1 rule 3). Specifically this step consumes: Step 1 primitives (`Pitch`, `SampleRate`, `SamplePosition`, `SampleDuration`, `NoteEvent`); Step 2 (`IAudioSource`, `WavAudioSource`, the signal generator + WAV writer test utilities); Step 3 spectral front end; Step 4 YIN detector; Step 5 onset/segmentation; Step 6 quantizer + `Score`; Step 7 MIDI writer/reader; Step 8 `ISynthesizer` + `MeltySynthSynthesizer` + the committed SoundFont under `fixtures/soundfont/`.
**Commit (spec):** `test: closed-loop synthesize→transcribe property suite`

---

## Approach

The closed loop is the project's trial balance. It works because the repository contains **both directions** of the transform: a synthesizer that turns notes into audio (`synthesize`), and a transcriber that turns audio into notes (`transcribe`). Compose them and you get an endomorphism on scores; the property asserts it is approximately the identity on a constrained corpus. No hand-labelled ground truth is ever needed — MeltySynth *is* the oracle (Section 5).

The corpus is deliberately narrow so that a failure means a real DSP bug, not an unfair test. A generated case is a **grid-exact monophonic performance**: a short sequence of notes whose onsets and durations fall exactly on a sixteenth-note grid derived from a chosen tempo. Concretely, at tempo `bpm` and sample rate `r`, one sixteenth spans `sps = 60/bpm · r / 4` samples (a *fractional* count). Note *i* starts at grid index `onsetSubᵢ` and its onset in samples is `round(onsetSubᵢ · sps)`; its duration in samples is `round(endSubᵢ · sps) − round(onsetSubᵢ · sps)`. Because the generator places every event at exactly the sample position the quantizer will invert (`gridIndex = round(samples / sps)`), quantizing the generated events reproduces the same grid indices (Step 6's "events generated exactly on a grid reproduce them exactly" property). That quantized score is the **reference** we compare against.

Time stays integer samples throughout (non-negotiable 1): `sps` is fractional but every stored onset/duration is a rounded `long`, and the seconds→samples conversions happen only at the edges (generator and the ms→samples min-duration knob), never accumulated. Comparison happens in **grid-subdivision space** (non-negotiable 4's cousin: decisions in musically meaningful units, not raw samples), so the `±1 subdivision` tolerance of R9.2 means the same thing at every tempo.

One case therefore runs: `events → synthesize → PCM → write WAV → WavAudioSource → TranscriptionPipeline → Score → flatten to grid → compare to reference grid`. That WAV path is not incidental — it is literally what `claudio transcribe <in.wav> --tempo` does (Section 7), minus the CLI. On mismatch we persist the generated MIDI and the rendered WAV to a quarantine directory (R9.3) so the case can be reproduced, fixed, and then promoted into `fixtures/regressions/` where a dedicated harness re-runs it forever — the suite only ever gets harder.

Determinism (non-negotiable 3) is proven separately and cheaply by a fixed, RNG-free case rendered and transcribed twice; the random property uses CsCheck with a pinnable seed so CI is reproducible.

Use @superpowers:test-driven-development for every red→green loop below. When the closed-loop property (Task 5) fails on green, that is a *discovered bug in an earlier step*, not a flaky test — reach for @superpowers:systematic-debugging and treat the quarantined case as the reproduction (Section 1 rule 8: the test is presumed right).

---

## Requirements coverage

| Requirement | Task(s) | Proven by (test) |
|---|---|---|
| **R9.1** generate constrained score, render, transcribe at known tempo, compare | 1 (pipeline), 2 (generator), 5 (loop) | `Generated_cases_satisfy_the_R9_1_constraints` (Task 2); `Transcribe_of_Synthesize_recovers_the_score_within_tolerance` (Task 5); pipeline proven by `Pipeline_transcribes_a_single_sustained_note_to_one_event` (Task 1) |
| **R9.2** count exact, pitch exact, onset ±1 sub, duration ±1 sub | 3 (comparer), 5 (loop) | `ClosedLoopComparer` unit tests (Task 3); enforced every case in Task 5 |
| **R9.3** persist MIDI + WAV to quarantine; promote to `fixtures/regressions/` | 4 (quarantine), 7 (regression harness), 5 (loop calls quarantine on failure) | `Quarantine_persists_midi_and_wav_for_a_failing_case` (Task 4); `All_regression_fixtures_transcribe_within_tolerance` (Task 7) |
| **R9.4** runs in CI (modest push count; optional nightly) | 8 (CI), 5 (env-driven case count) | `.github/workflows/ci.yml` runs `dotnet test`; case count from `CLOSED_LOOP_CASES`; optional `nightly-closed-loop.yml` |
| Determinism (non-negotiable 3) reinforced | 6 | `Domain_output_is_deterministic_for_a_fixed_case` (Task 6) |
| §7 `transcribe` CLI (CONTRACTS §9 assigns it to Step 9): emit `raw.mid` + `score.mid` (`score.musicxml` deferred to Step 11) | 9 | `Transcribe_emits_raw_and_score_midi_for_a_sustained_note` (Task 9) |

---

## Consumed prior-step surface (per CONTRACTS.md — authoritative)

Step 9 is a *composition* step, so its code necessarily calls into types built in Steps 1–8. The names below are taken **verbatim from `docs/plans/CONTRACTS.md`** (the single source of truth for cross-step type names and signatures); use them exactly. Everything else in the task code is new and complete.

- **Step 1 (`AudioClaudio.Domain`):** `Pitch(int midiNumber)` with `.MidiNumber` and `.Frequency()`; `SampleRate(int hz)` with `.Hz`; `SamplePosition(long samples, SampleRate rate)` with `.Samples`; `SampleDuration(long samples, SampleRate rate)` with `.Samples`; `NoteEvent(Pitch, SamplePosition onset, SampleDuration duration, int velocity = 64)` with `.Pitch`, `.Onset`, `.Duration`, `.Velocity`.
- **Step 2 (`AudioClaudio.Domain` / `AudioClaudio.Application.Ports` / `AudioClaudio.Infrastructure.Audio`):** `IAudioSource` (in `AudioClaudio.Application.Ports`) with the **property** `IEnumerable<Frame> Frames { get; }` (pull model — Step 2 DECISION GATE default); `Frame` (`sealed class`, 2-arg ctor `Frame(float[] samples, SamplePosition start)`) with `.Samples` (`float[]`), `.Start` (SamplePosition), `.Rate` (derived `=> Start.Rate`) — there is **no** `.Position`, no `.Length`; `FrameParameters(int size, int hop)`; `WavAudioSource : IAudioSource, IDisposable` with `WavAudioSource.FromFile(string path, FrameParameters parameters)`. Test utilities in `AudioClaudio.Tests.Signals`: `SignalGenerator.Sine(double frequencyHz, int sampleCount, SampleRate rate, double amplitude = 0.8)`, `SignalGenerator.HarmonicStack(double fundamentalHz, int sampleCount, SampleRate rate, int partials = 6, double decay = 1.0, double amplitude = 0.8)`, and `WavWriter.WriteMonoFile(string path, float[] mono, SampleRate rate)`.
- **Step 3 (`AudioClaudio.Domain.Spectral`):** `IFourierTransform` with `Complex[] Forward(double[] samples)`; `Radix2Fft : IFourierTransform` (Option A, Domain, dependency-free — the closed-loop tests construct `new Radix2Fft()`); `SpectralFrontEnd(int frameSize, IFourierTransform fft)` with `MagnitudeSpectrum Analyze(Frame frame)`; `MagnitudeSpectrum` with `.Magnitudes` (`IReadOnlyList<double>`).
- **Step 4 (`AudioClaudio.Domain`):** `static YinPitchDetector.Detect(Frame frame, YinOptions options)` returning `PitchEstimate`; `YinOptions(double threshold = 0.15, ...)`; `PitchEstimate` with `bool IsVoiced`, `double FrequencyHz`, `double Confidence`.
- **Step 5 (`AudioClaudio.Domain`):** `OnsetDetector()` with `IReadOnlyList<int> Detect(IReadOnlyList<IReadOnlyList<double>> magnitudeSpectra)` (returns onset frame indices); `FrameObservation(SamplePosition start, Pitch? pitch, double energy)`; `NoteSegmenterOptions` (record with `required SampleDuration MinNoteDuration` (R5.3) and `double DecayFloorRatio = 0.0` (R5.2) — note CONTRACTS §5's illustrative comment says "MinDurationSamples", but Step 5, the definer, ships `MinNoteDuration`); `NoteSegmenter(NoteSegmenterOptions options)` with `IReadOnlyList<NoteEvent> Segment(IReadOnlyList<FrameObservation> frames, IReadOnlyList<int> onsetFrames)`; `OnsetDetectorOptions` with `ThresholdMultiplier`. There is **no** `SegmentationSettings` type; segmentation does **not** detect onsets internally.
- **Step 6 (`AudioClaudio.Domain`):** `Tempo(double beatsPerMinute)` with `.BeatsPerMinute`; `TimeSignature.FourFour`; `Subdivision.Sixteenth`; `QuantizationGrid(SampleRate sampleRate, Tempo tempo, TimeSignature timeSignature, Subdivision subdivision)` with `.TicksPerBeat`; `static Quantizer.Quantize(IReadOnlyList<NoteEvent> events, QuantizationGrid grid) -> Score`; `Score` with `.Tempo`, `.TimeSignature`, `.Subdivision`, `.Measures`; `Measure.Elements` of `ScoreElement(ElementKind Kind, Pitch? Pitch, int Velocity, int LengthTicks, bool TiedToNext)`; `enum ElementKind { Note, Rest }`. There is **no** `Score.Notes` and **no** `PlacedNote` type — Step 9 flattens `Score.Measures → Elements` into a local grid sequence (Task 3).
- **Step 7 (`AudioClaudio.Infrastructure.Midi`):** `DryWetMidiWriter : IScoreWriter, INoteEventWriter` with `Write(IReadOnlyList<NoteEvent> events, Tempo tempo, Stream destination)` and `Write(Score score, Stream destination)` (Stream-based — callers open a `FileStream`); `MidiFileReader.ReadFile(string path, SampleRate rate) -> MidiReadResult` where `MidiReadResult` has `.Events` (`IReadOnlyList<NoteEvent>`) and `.Tempo` (`Tempo`). Sample rate is not stored in MIDI, so the caller supplies it.
- **Step 8 (`AudioClaudio.Application.Ports` / `AudioClaudio.Infrastructure.Synthesis`):** `ISynthesizer` (in `AudioClaudio.Application.Ports`) with `float[] Render(IReadOnlyList<NoteEvent> notes, SampleRate sampleRate)`; `MeltySynthSynthesizer(string soundFontPath) : ISynthesizer`; the committed SoundFont at `fixtures/soundfont/*.sf2`.
- **Step 0 (`AudioClaudio.Tests.TestSupport`):** `RepoPaths.RepoRoot` locates the repo root by walking up to `AudioClaudio.sln`; Step 9's `Fixtures` helper reuses it rather than re-deriving the root.

If any consumed member is absent when you reach its call site, that is a seam to reconcile against CONTRACTS.md now (a 10–20 line adapter or a rename), not a reason to break the dependency rule (Section 1 rule 6).

---

## Task 1: `TranscriptionPipeline` — compose the Domain behind `ITranscriber`

**Files:**
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Application/Ports/ITranscriber.cs`
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Application/TranscriptionSettings.cs`
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Application/TranscriptionResult.cs`
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Application/TranscriptionPipeline.cs`
- Test: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/ClosedLoop/PipelineIntegrationTests.cs`

**Step 1 — Write the failing test:** feed a signal-generator note through the whole pipeline (independent of the synth) and demand exactly one event of the right pitch.

```csharp
using System;
using System.IO;
using System.Linq;
using AudioClaudio.Application;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;       // Radix2Fft
using AudioClaudio.Infrastructure.Audio;  // WavAudioSource (composition root role — test only)
using AudioClaudio.Tests.Signals;         // SignalGenerator, WavWriter (Step 2 test utilities)
using Xunit;

namespace AudioClaudio.Tests.ClosedLoop;

public sealed class PipelineIntegrationTests
{
    [Trait("Category", "Fast")]
    [Fact]
    public void Pipeline_transcribes_a_single_sustained_note_to_one_event()
    {
        var rate = new SampleRate(44100);
        float[] pcm = SignalGenerator.HarmonicStack(
            new Pitch(69).Frequency(), (int)(1.0 * rate.Hz), rate, partials: 6, decay: 1.0);

        var wav = Path.Combine(Path.GetTempPath(), $"acl_pipeline_{Guid.NewGuid():N}.wav");
        try
        {
            WavWriter.WriteMonoFile(wav, pcm, rate);

            var pipeline = new TranscriptionPipeline(TranscriptionSettings.ForTempo(120), new Radix2Fft());
            using var source = WavAudioSource.FromFile(wav, new FrameParameters(2048, 512));

            TranscriptionResult result = pipeline.Transcribe(source);

            Assert.Single(result.RawEvents);
            Assert.Equal(69, result.RawEvents[0].Pitch.MidiNumber);

            // The quantized score carries exactly one note element (flattened inline here;
            // the reusable ScoreGrid.From helper arrives in Task 3).
            var notes = result.Score.Measures
                .SelectMany(m => m.Elements)
                .Where(e => e.Kind == ElementKind.Note)
                .ToList();
            Assert.Single(notes);
            Assert.Equal(69, notes[0].Pitch!.Value.MidiNumber);
        }
        finally
        {
            if (File.Exists(wav)) File.Delete(wav);
        }
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~PipelineIntegrationTests"
```

Expected FAILURE: compile error — `ITranscriber` (in `AudioClaudio.Application.Ports`) and `TranscriptionSettings` / `TranscriptionResult` / `TranscriptionPipeline` (in `AudioClaudio.Application`) do not exist yet.

**Step 3 — Minimal implementation:** the port, the settings record, the result record, and the pipeline that composes Steps 3–6. Application depends only on Domain + its own ports — no Infrastructure reference.

```csharp
// Ports/ITranscriber.cs
using AudioClaudio.Application;   // TranscriptionResult

namespace AudioClaudio.Application.Ports;   // ports live here (CONTRACTS §0)

/// <summary>Full monophonic transcription: audio frames in, a quantized score out.</summary>
public interface ITranscriber
{
    /// <summary>Runs the pipeline over <paramref name="source"/> at the settings' known tempo.</summary>
    TranscriptionResult Transcribe(IAudioSource source);   // IAudioSource is also in .Ports
}
```

```csharp
// TranscriptionSettings.cs
using AudioClaudio.Domain;

namespace AudioClaudio.Application;

/// <summary>Every pipeline knob in one place (R2.4: frame/hop are parameters, not scattered constants).</summary>
public sealed record TranscriptionSettings
{
    /// <summary>Analysis window length N in samples.</summary>
    public int FrameSize { get; init; } = 2048;

    /// <summary>Hop H in samples between successive frames.</summary>
    public int Hop { get; init; } = 512;

    /// <summary>YIN CMND threshold separating voiced from unvoiced (Step 4).</summary>
    public double YinThreshold { get; init; } = 0.15;

    /// <summary>Adaptive spectral-flux peak threshold multiplier → `OnsetDetectorOptions.ThresholdMultiplier` (Step 5).</summary>
    public double OnsetThreshold { get; init; } = 1.5;

    /// <summary>Flicker floor as milliseconds; converted to integer samples at the edge → `NoteSegmenterOptions.MinNoteDuration` (R5.3).</summary>
    public double MinNoteMilliseconds { get; init; } = 50.0;

    /// <summary>
    /// Decay-below-floor termination ratio → `NoteSegmenterOptions.DecayFloorRatio` (Step 5's R5.2). The
    /// composition root owns this choice; the closed loop keeps it 0 (disabled) because synthesized piano
    /// notes decay naturally and a floor would truncate sustained durations.
    /// </summary>
    public double DecayFloorRatio { get; init; } = 0.0;

    /// <summary>User-declared tempo (R6.3): MVP never estimates it.</summary>
    public required double TempoBpm { get; init; }

    public TimeSignature TimeSignature { get; init; } = TimeSignature.FourFour;

    public Subdivision Subdivision { get; init; } = Subdivision.Sixteenth;

    public static TranscriptionSettings ForTempo(double bpm) => new() { TempoBpm = bpm };
}
```

```csharp
// TranscriptionResult.cs
using System.Collections.Generic;
using AudioClaudio.Domain;

namespace AudioClaudio.Application;

/// <summary>The quantized <see cref="Score"/> plus the raw (unquantized) events that produced it.</summary>
public sealed record TranscriptionResult(Score Score, IReadOnlyList<NoteEvent> RawEvents);
```

```csharp
// TranscriptionPipeline.cs
using System;
using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Application.Ports;   // ITranscriber, IAudioSource, ISynthesizer live here
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;     // IFourierTransform, SpectralFrontEnd, MagnitudeSpectrum

namespace AudioClaudio.Application;

/// <summary>
/// Composes Steps 3–6 into a single audio→score transform. Pure with respect to the domain:
/// it constructs only Domain algorithm objects (the FFT is injected as the Domain interface
/// <see cref="IFourierTransform"/>) and never touches Infrastructure or the wall clock.
/// </summary>
public sealed class TranscriptionPipeline : ITranscriber
{
    private readonly TranscriptionSettings _settings;
    private readonly IFourierTransform _fft;

    // FFT injected (CONTRACTS §3): under Option A the test/composition root hands us a Domain
    // `new Radix2Fft()`; under Option B it hands us the Infrastructure NWaves adapter. Either way
    // Application never references the concrete FFT — the dependency rule stays physical.
    public TranscriptionPipeline(TranscriptionSettings settings, IFourierTransform fft)
    {
        _settings = settings;
        _fft = fft;
    }

    public TranscriptionResult Transcribe(IAudioSource source)
    {
        // Pull all frames once (Step 2 pull model, `Frames` is a property). Deterministic order → deterministic output.
        var frames = source.Frames.ToList();
        if (frames.Count == 0)
        {
            // No audio: a real WAV always yields ≥1 frame; guard so the grid below has a rate.
            var emptyGrid = new QuantizationGrid(
                new SampleRate(FallbackSampleRateHz), new Tempo(_settings.TempoBpm),
                _settings.TimeSignature, _settings.Subdivision);
            return new TranscriptionResult(
                Quantizer.Quantize(Array.Empty<NoteEvent>(), emptyGrid), Array.Empty<NoteEvent>());
        }

        var rate = frames[0].Rate;                                           // Step 2: Frame.Rate => Start.Rate
        var frontEnd = new SpectralFrontEnd(_settings.FrameSize, _fft);       // Step 3 (FFT injected)
        var yinOptions = new YinOptions(threshold: _settings.YinThreshold);   // Step 4

        // Per-frame: YIN estimate, magnitude spectrum, and a FrameObservation for the segmenter.
        var magnitudeSpectra = new List<IReadOnlyList<double>>(frames.Count);
        var observations = new List<FrameObservation>(frames.Count);
        foreach (var frame in frames)
        {
            PitchEstimate estimate = YinPitchDetector.Detect(frame, yinOptions);   // Step 4 (static)
            MagnitudeSpectrum spectrum = frontEnd.Analyze(frame);

            magnitudeSpectra.Add(spectrum.Magnitudes);

            double energy = 0.0;
            float[] s = frame.Samples;
            for (int j = 0; j < s.Length; j++) energy += s[j] * (double)s[j];

            Pitch? pitch = estimate.IsVoiced ? Pitch.FromFrequency(estimate.FrequencyHz) : null;
            observations.Add(new FrameObservation(frame.Start, pitch, energy));
        }

        // Step 5 is two stages: OnsetDetector finds attack frames, NoteSegmenter bounds notes.
        IReadOnlyList<int> onsetFrames = new OnsetDetector(
            new OnsetDetectorOptions { ThresholdMultiplier = _settings.OnsetThreshold })
            .Detect(magnitudeSpectra);

        long minSamples = (long)Math.Round(_settings.MinNoteMilliseconds / 1000.0 * rate.Hz);
        var segmenter = new NoteSegmenter(new NoteSegmenterOptions
        {
            MinNoteDuration = new SampleDuration(minSamples, rate),   // R5.3 flicker floor
            // R5.2's decay-below-floor is the composition root's call (Step 5's R5.2 coverage row):
            // deliberately 0 here — synthesized piano notes decay, so a floor would truncate sustained
            // durations and break the ±1-subdivision duration tolerance of the closed loop.
            DecayFloorRatio = _settings.DecayFloorRatio,
        });

        IReadOnlyList<NoteEvent> events = segmenter.Segment(observations, onsetFrames);

        // Step 6: build the grid and quantize (static, no `new Quantizer()`).
        var grid = new QuantizationGrid(
            rate, new Tempo(_settings.TempoBpm), _settings.TimeSignature, _settings.Subdivision);
        Score score = Quantizer.Quantize(events, grid);

        return new TranscriptionResult(score, events);
    }

    /// <summary>
    /// Incremental note feed for the live `listen` view (Step 10). For the MVP this simply
    /// surfaces the raw events of a full pass; Step 10 iterates it as notes are produced.
    /// </summary>
    public IEnumerable<NoteEvent> StreamNotes(IAudioSource source) => Transcribe(source).RawEvents;

    private const int FallbackSampleRateHz = 44100;
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~PipelineIntegrationTests"
```

Expected PASS: one event, MIDI 69, one note in the score. If pitch is off by an octave or the count is wrong, that is a real Step 3/4/5 bug — debug with @superpowers:systematic-debugging before proceeding.

**Step 5 — Commit:** create the step branch, mark it, then commit (see @gitbutler). Run `but status -fv` and read the file/hunk IDs for these four Application files + the test.

```bash
but branch new step-09-closed-loop && but mark step-09-closed-loop
but status -fv                       # copy the IDs for the changes below
but commit step-09-closed-loop -m "feat(app): compose ITranscriber pipeline (frames→Score)" --changes <ids> --status-after
```

---

## Task 2: The constrained-score generator

**Files:**
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/ClosedLoop/ClosedLoopCase.cs`
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/ClosedLoop/ClosedLoopGen.cs`
- Test: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/ClosedLoop/GeneratorConstraintTests.cs`

**Step 1 — Write the failing test:** a CsCheck property asserting *every* generated case obeys R9.1's constraints.

```csharp
using System;
using System.Linq;
using AudioClaudio.Domain;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.ClosedLoop;

public sealed class GeneratorConstraintTests
{
    [Trait("Category", "Fast")]
    [Fact]
    public void Generated_cases_satisfy_the_R9_1_constraints()
    {
        ClosedLoopGen.Cases.Sample(c =>
        {
            Assert.InRange(c.TempoBpm, 60, 140);
            Assert.True(c.Events.Count >= 3, "at least three notes");

            double sps = 60.0 / c.TempoBpm * c.Rate.Hz / ClosedLoopGen.SubdivisionsPerBeat;
            long prevEnd = -1;
            int prevEndSub = -1;
            foreach (var e in c.Events)
            {
                Assert.InRange(e.Pitch.MidiNumber, 33, 96);              // MIDI 33..96
                Assert.Equal(ClosedLoopGen.Velocity, e.Velocity);

                int durSub = (int)Math.Round(e.Duration.Samples / sps);
                Assert.True(durSub >= 2, $"duration {durSub} sub < eighth");   // notes >= eighth

                Assert.True(e.Onset.Samples >= prevEnd, "notes overlap (not monophonic)");

                int onsetSub = (int)Math.Round(e.Onset.Samples / sps);
                if (prevEndSub >= 0)
                    Assert.True(onsetSub - prevEndSub >= 1, "no grid rest between notes");

                // grid-exact: onset is exactly the rounded grid position
                Assert.Equal((long)Math.Round(onsetSub * sps), e.Onset.Samples);

                prevEnd = e.Onset.Samples + e.Duration.Samples;
                prevEndSub = onsetSub + durSub;
            }
        }, iter: 500);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~GeneratorConstraintTests"
```

Expected FAILURE: compile error — `ClosedLoopCase` and `ClosedLoopGen` do not exist.

**Step 3 — Minimal implementation:** the case record and the CsCheck generator.

```csharp
// ClosedLoopCase.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>A grid-exact monophonic performance: the input to one closed-loop trial.</summary>
public sealed record ClosedLoopCase(
    SampleRate Rate,
    int TempoBpm,
    TimeSignature TimeSignature,
    Subdivision Subdivision,
    IReadOnlyList<NoteEvent> Events)
{
    /// <summary>Stable short id derived from the case content; names quarantine artifacts.</summary>
    public string Id()
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Describe()));
        return Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant(); // 12 hex chars
    }

    public string Describe() =>
        $"bpm={TempoBpm};rate={Rate.Hz};notes=" +
        string.Join(",", Events.Select(e => $"{e.Pitch.MidiNumber}@{e.Onset.Samples}+{e.Duration.Samples}"));

    public override string ToString() => Describe();
}
```

```csharp
// ClosedLoopGen.cs
using System;
using System.Collections.Generic;
using AudioClaudio.Domain;
using CsCheck;

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>Generates the constrained corpus of R9.1 and one fixed case for the determinism guard.</summary>
public static class ClosedLoopGen
{
    public const int SubdivisionsPerBeat = 4;   // sixteenth-note grid
    public const int MidiLow = 33;              // A1
    public const int MidiHigh = 96;             // C7
    public const int Velocity = 100;            // MVP constant (R1.4)
    public const int SampleRateHz = 44100;      // fixed so MeltySynth renders are deterministic

    // Durations in sixteenths, all >= eighth: eighth, dotted-eighth, quarter, dotted-quarter, half.
    private static readonly Gen<int> DurationSub = Gen.OneOfConst(2, 3, 4, 6, 8);

    public static readonly Gen<ClosedLoopCase> Cases =
        from bpm in Gen.Int[60, 140]
        from count in Gen.Int[3, 8]
        from pitches in Gen.Int[MidiLow, MidiHigh].Array[count]
        from durs in DurationSub.Array[count]
        from rests in Gen.Int[1, 4].Array[count]   // >= 1 grid subdivision of rest between notes
        select Build(bpm, pitches, durs, rests);

    /// <summary>Four quarter notes at 120 BPM — RNG-free, for the determinism guard (Task 6).</summary>
    public static ClosedLoopCase Fixed() =>
        Build(120, new[] { 60, 64, 67, 72 }, new[] { 4, 4, 4, 4 }, new[] { 1, 1, 1, 1 });

    internal static ClosedLoopCase Build(int bpm, int[] pitches, int[] durs, int[] rests)
    {
        var rate = new SampleRate(SampleRateHz);
        double sps = 60.0 / bpm * rate.Hz / SubdivisionsPerBeat;  // samples per sixteenth (fractional)
        long OnsetSamples(int sub) => (long)Math.Round(sub * sps, MidpointRounding.AwayFromZero);

        var events = new List<NoteEvent>(pitches.Length);
        int cursorSub = 0;
        for (int i = 0; i < pitches.Length; i++)
        {
            int onsetSub = cursorSub;
            int endSub = onsetSub + durs[i];
            long onset = OnsetSamples(onsetSub);
            long duration = OnsetSamples(endSub) - onset;

            events.Add(new NoteEvent(
                new Pitch(pitches[i]),
                new SamplePosition(onset, rate),
                new SampleDuration(duration, rate),
                Velocity));

            cursorSub = endSub + rests[i];   // rest >= 1 subdivision keeps it monophonic + separated
        }

        return new ClosedLoopCase(rate, bpm, TimeSignature.FourFour, Subdivision.Sixteenth, events);
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~GeneratorConstraintTests"
```

Expected PASS: 500 generated cases all satisfy pitch range, ≥ eighth duration, tempo range, ≥ 1 grid rest, monophonic non-overlap, and grid-exact onsets.

**Step 5 — Commit:**

```bash
but status -fv
but commit step-09-closed-loop -m "test(closedloop): constrained-score generator" --changes <ids> --status-after
```

---

## Task 3: Grid-space score comparison

**Files:**
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/ClosedLoop/NoteGridPosition.cs`
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/ClosedLoop/ScoreGrid.cs`
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/ClosedLoop/ClosedLoopComparer.cs`
- Test: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/ClosedLoop/ClosedLoopComparerTests.cs`

**Step 1 — Write the failing test:** encode R9.2 exactly — count, pitch, onset±1, duration±1 — over a Step-9-owned `NoteGridPosition` so the comparison is testable without constructing a full `Score`.

```csharp
using System.Collections.Generic;
using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.ClosedLoop;

public sealed class ClosedLoopComparerTests
{
    private static NoteGridPosition N(int midi, int onset, int dur) =>
        new(new Pitch(midi), onset, dur);

    [Trait("Category", "Fast")]
    [Fact]
    public void Identical_grids_match()
    {
        var g = new List<NoteGridPosition> { N(60, 0, 4), N(62, 5, 2) };
        Assert.True(ClosedLoopComparer.Compare(g, g).IsMatch);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void Onset_off_by_one_subdivision_is_within_tolerance()
    {
        var exp = new List<NoteGridPosition> { N(60, 0, 4), N(62, 8, 4) };
        var act = new List<NoteGridPosition> { N(60, 0, 4), N(62, 9, 4) };
        Assert.True(ClosedLoopComparer.Compare(exp, act).IsMatch);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void Onset_off_by_two_subdivisions_fails()
    {
        var exp = new List<NoteGridPosition> { N(60, 0, 4) };
        var act = new List<NoteGridPosition> { N(60, 2, 4) };
        var r = ClosedLoopComparer.Compare(exp, act);
        Assert.False(r.IsMatch);
        Assert.Contains("onset", r.Detail);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void Duration_off_by_two_subdivisions_fails()
    {
        var exp = new List<NoteGridPosition> { N(60, 0, 4) };
        var act = new List<NoteGridPosition> { N(60, 0, 6) };
        Assert.False(ClosedLoopComparer.Compare(exp, act).IsMatch);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void Any_pitch_mismatch_fails_exactly()
    {
        var exp = new List<NoteGridPosition> { N(60, 0, 4) };
        var act = new List<NoteGridPosition> { N(61, 0, 4) };
        var r = ClosedLoopComparer.Compare(exp, act);
        Assert.False(r.IsMatch);
        Assert.Contains("pitch", r.Detail);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void Note_count_mismatch_fails()
    {
        var exp = new List<NoteGridPosition> { N(60, 0, 4), N(62, 5, 4) };
        var act = new List<NoteGridPosition> { N(60, 0, 4) };
        var r = ClosedLoopComparer.Compare(exp, act);
        Assert.False(r.IsMatch);
        Assert.Contains("count", r.Detail);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~ClosedLoopComparerTests"
```

Expected FAILURE: compile error — `NoteGridPosition`, `ScoreGrid`, `ClosedLoopComparer` do not exist.

**Step 3 — Minimal implementation:**

```csharp
// NoteGridPosition.cs
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>A note reduced to grid space: pitch plus absolute onset/duration in subdivisions.</summary>
public readonly record struct NoteGridPosition(Pitch Pitch, int OnsetSubdivisions, int DurationSubdivisions);
```

```csharp
// ScoreGrid.cs
using System.Collections.Generic;
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>
/// Flattens the canonical Step 6 <see cref="Score"/> (Measures → Elements) into the linear grid
/// sequence the comparer needs. There is no <c>Score.Notes</c>/<c>PlacedNote</c> in the domain, so
/// this LOCAL helper derives placed notes from <see cref="Score.Measures"/>: it walks measures in
/// order, accumulates a subdivision cursor, and merges any barline-tied segments (<c>TiedToNext</c>)
/// back into a single note. Step 6's grid uses one tick per subdivision
/// (<c>TicksPerBeat</c> == subdivisions-per-beat for the chosen <c>Subdivision</c>), so each
/// element's <c>LengthTicks</c> is already its length in subdivisions.
/// </summary>
public static class ScoreGrid
{
    public static IReadOnlyList<NoteGridPosition> From(Score score)
    {
        var notes = new List<NoteGridPosition>();

        int cursor = 0;             // cumulative subdivision index across all measures
        bool havePending = false;   // a tied note being accumulated across a barline
        Pitch pendingPitch = default;
        int pendingOnset = 0;
        int pendingDuration = 0;

        foreach (var measure in score.Measures)
        {
            foreach (var element in measure.Elements)
            {
                if (element.Kind == ElementKind.Note)
                {
                    if (!havePending)
                    {
                        havePending = true;
                        pendingPitch = element.Pitch!.Value;
                        pendingOnset = cursor;
                        pendingDuration = element.LengthTicks;
                    }
                    else
                    {
                        pendingDuration += element.LengthTicks;   // continuation of a barline-split note
                    }

                    if (!element.TiedToNext)
                    {
                        notes.Add(new NoteGridPosition(pendingPitch, pendingOnset, pendingDuration));
                        havePending = false;
                    }
                }

                cursor += element.LengthTicks;   // notes and rests both advance the grid cursor
            }
        }

        if (havePending)   // dangling tie (malformed score) — emit what we have
            notes.Add(new NoteGridPosition(pendingPitch, pendingOnset, pendingDuration));

        return notes;
    }
}
```

```csharp
// ClosedLoopComparer.cs
using System;
using System.Collections.Generic;

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>Outcome of one score comparison — match, or the first divergence found.</summary>
public readonly record struct ClosedLoopComparison(bool IsMatch, string? Detail)
{
    public static ClosedLoopComparison Match => new(true, null);
    public static ClosedLoopComparison Fail(string detail) => new(false, detail);
}

/// <summary>Encodes R9.2: count exact, pitch exact, onset/duration within ±1 subdivision.</summary>
public static class ClosedLoopComparer
{
    public static ClosedLoopComparison Compare(
        IReadOnlyList<NoteGridPosition> expected,
        IReadOnlyList<NoteGridPosition> actual,
        int subdivisionTolerance = 1)
    {
        if (expected.Count != actual.Count)
            return ClosedLoopComparison.Fail($"note count {actual.Count} != expected {expected.Count}");

        for (int i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var a = actual[i];

            if (e.Pitch.MidiNumber != a.Pitch.MidiNumber)
                return ClosedLoopComparison.Fail(
                    $"note {i}: pitch {a.Pitch.MidiNumber} != expected {e.Pitch.MidiNumber}");

            if (Math.Abs(e.OnsetSubdivisions - a.OnsetSubdivisions) > subdivisionTolerance)
                return ClosedLoopComparison.Fail(
                    $"note {i}: onset {a.OnsetSubdivisions} not within {subdivisionTolerance} of {e.OnsetSubdivisions}");

            if (Math.Abs(e.DurationSubdivisions - a.DurationSubdivisions) > subdivisionTolerance)
                return ClosedLoopComparison.Fail(
                    $"note {i}: duration {a.DurationSubdivisions} not within {subdivisionTolerance} of {e.DurationSubdivisions}");
        }

        return ClosedLoopComparison.Match;
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~ClosedLoopComparerTests"
```

Expected PASS: all six comparison cases behave per R9.2.

**Step 5 — Commit:**

```bash
but status -fv
but commit step-09-closed-loop -m "test(closedloop): grid-space score comparison" --changes <ids> --status-after
```

---

## Task 4: Failure quarantine writer

**Files:**
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/ClosedLoop/Quarantine.cs`
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/ClosedLoop/Fixtures.cs`
- Test: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/ClosedLoop/QuarantineTests.cs`

**Step 1 — Write the failing test:** a fabricated failing case must leave a re-readable MIDI and a WAV on disk (R9.3).

```csharp
using System.IO;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi;   // MidiFileReader (test = composition root)
using Xunit;

namespace AudioClaudio.Tests.ClosedLoop;

public sealed class QuarantineTests
{
    [Trait("Category", "Fast")]
    [Fact]
    public void Quarantine_persists_midi_and_wav_for_a_failing_case()
    {
        var c = ClosedLoopGen.Fixed();
        float[] pcm = new float[c.Rate.Hz / 2];   // half a second of silence stands in for a render
        var dir = Path.Combine(Path.GetTempPath(), $"acl_quarantine_test_{c.Id()}");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

        try
        {
            string written = Quarantine.Persist(c, pcm, dir);

            var mid = Path.Combine(written, $"{c.Id()}.mid");
            var wav = Path.Combine(written, $"{c.Id()}.wav");
            Assert.True(File.Exists(mid), "quarantined MIDI missing");
            Assert.True(File.Exists(wav), "quarantined WAV missing");

            // MIDI carries no sample rate, so the reader is told which rate to denominate positions in.
            var read = MidiFileReader.ReadFile(mid, c.Rate);
            Assert.Equal(c.Events.Count, read.Events.Count);
            Assert.Equal(c.Events[0].Pitch.MidiNumber, read.Events[0].Pitch.MidiNumber);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~QuarantineTests"
```

Expected FAILURE: compile error — `Quarantine` (and the `Fixtures` helper it will use for the default directory) do not exist.

**Step 3 — Minimal implementation:** the quarantine writer plus a small fixtures-locator (also used by later tasks to find the SoundFont and the regressions folder).

```csharp
// Fixtures.cs
using System;
using System.IO;
using System.Linq;
using AudioClaudio.Tests.TestSupport;   // shared RepoPaths locator (Step 0) — do not re-derive the repo root

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>
/// Locates committed fixtures and the default quarantine directory for the closed-loop suite. It
/// reuses the shared <see cref="RepoPaths"/> locator from Step 0 rather than re-implementing the
/// walk up to <c>AudioClaudio.sln</c> (one locator, not five).
/// </summary>
public static class Fixtures
{
    public static string RepoRoot { get; } = RepoPaths.RepoRoot;

    public static string SoundFontPath { get; } = Directory
        .EnumerateFiles(Path.Combine(RepoRoot, "fixtures", "soundfont"), "*.sf2", SearchOption.AllDirectories)
        .OrderBy(p => p, StringComparer.Ordinal)
        .First();

    public static string RegressionsDir { get; } = Path.Combine(RepoRoot, "fixtures", "regressions");

    public static string QuarantineDir { get; } =
        Environment.GetEnvironmentVariable("AUDIO_CLAUDIO_QUARANTINE")
        ?? Path.Combine(RepoRoot, "artifacts", "closed-loop-quarantine");
}
```

```csharp
// Quarantine.cs
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi;   // DryWetMidiWriter
using AudioClaudio.Tests.Signals;          // WavWriter (Step 2 test utility)

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>Persists a failing closed-loop case so it can be reproduced and promoted to fixtures/regressions/.</summary>
public static class Quarantine
{
    /// <summary>Writes &lt;id&gt;.mid (the generated performance) and &lt;id&gt;.wav (the rendered audio); returns the directory.</summary>
    public static string Persist(ClosedLoopCase c, IReadOnlyList<float> pcm, string? directory = null)
    {
        string dir = directory ?? Fixtures.QuarantineDir;
        Directory.CreateDirectory(dir);

        string id = c.Id();

        // Step 7's writer is Stream-based and needs the tempo for the sample→tick map; open a FileStream.
        using (var midi = File.Create(Path.Combine(dir, $"{id}.mid")))
            new DryWetMidiWriter().Write(c.Events, new Tempo(c.TempoBpm), midi);

        float[] samples = pcm as float[] ?? pcm.ToArray();
        WavWriter.WriteMonoFile(Path.Combine(dir, $"{id}.wav"), samples, c.Rate);
        return dir;
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~QuarantineTests"
```

Expected PASS: both artifacts exist and the MIDI re-reads to the same note count and first pitch.

**Step 5 — Commit:**

```bash
but status -fv
but commit step-09-closed-loop -m "test(closedloop): failure quarantine writer" --changes <ids> --status-after
```

---

## Task 5: The closed-loop property suite (the headline)

**Files:**
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/ClosedLoop/ClosedLoop.cs`
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/ClosedLoop/ClosedLoopMismatchException.cs`
- Test: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/ClosedLoop/ClosedLoopPropertyTests.cs`

**Step 1 — Write the failing test:** the property itself. It drives the whole loop via `ClosedLoop.RunCase`, with the push case count read from `CLOSED_LOOP_CASES` (R9.4). Mark it `Slow` so `--filter Category=Fast` skips it.

```csharp
using System;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.ClosedLoop;

public sealed class ClosedLoopPropertyTests
{
    [Trait("Category", "Slow")]
    [Fact]
    public void Transcribe_of_Synthesize_recovers_the_score_within_tolerance()
    {
        int cases = int.TryParse(Environment.GetEnvironmentVariable("CLOSED_LOOP_CASES"), out var n) && n > 0
            ? n
            : 32;   // modest push default (R9.4); CI/nightly override via env

        // Reproducibility: CsCheck pins the seed via the CsCheck_Seed env var and prints it on failure.
        ClosedLoopGen.Cases.Sample(ClosedLoop.RunCase, iter: cases);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
CLOSED_LOOP_CASES=4 dotnet test --filter "FullyQualifiedName~ClosedLoopPropertyTests"
```

Expected FAILURE: compile error — `ClosedLoop.RunCase` and `ClosedLoopMismatchException` do not exist.

**Step 3 — Minimal implementation:** one trial end-to-end; on mismatch, quarantine and throw a shrinking-friendly exception. This is the composition root for the test — the only place `MeltySynthSynthesizer` and `WavAudioSource` are constructed.

```csharp
// ClosedLoopMismatchException.cs
using System;

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>Thrown when a synthesized case does not transcribe back within R9.2 tolerance.</summary>
public sealed class ClosedLoopMismatchException : Exception
{
    public ClosedLoopMismatchException(string message) : base(message) { }
}
```

```csharp
// ClosedLoop.cs
using System.IO;
using AudioClaudio.Application;
using AudioClaudio.Application.Ports;         // ISynthesizer
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;           // Radix2Fft
using AudioClaudio.Infrastructure.Audio;      // WavAudioSource
using AudioClaudio.Infrastructure.Synthesis;  // MeltySynthSynthesizer
using AudioClaudio.Tests.Signals;             // WavWriter

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>Runs one trial of transcribe ∘ synthesize ≈ id and reports mismatches.</summary>
public static class ClosedLoop
{
    public static ISynthesizer CreateSynthesizer() => new MeltySynthSynthesizer(Fixtures.SoundFontPath);

    /// <summary>Synthesize → WAV → transcribe → compare. Quarantines and throws on any divergence.</summary>
    public static void RunCase(ClosedLoopCase c)
    {
        float[] pcm = CreateSynthesizer().Render(c.Events, c.Rate);

        var settings = TranscriptionSettings.ForTempo(c.TempoBpm) with
        {
            TimeSignature = c.TimeSignature,
            Subdivision = c.Subdivision,
        };

        // Reference: quantizing the grid-exact performance reproduces it exactly (Step 6 property).
        var referenceGrid = new QuantizationGrid(
            c.Rate, new Tempo(c.TempoBpm), c.TimeSignature, c.Subdivision);
        var expected = ScoreGrid.From(Quantizer.Quantize(c.Events, referenceGrid));

        string wav = Path.Combine(Path.GetTempPath(), $"acl_cl_{c.Id()}.wav");
        WavWriter.WriteMonoFile(wav, pcm, c.Rate);
        try
        {
            using var source = WavAudioSource.FromFile(wav, new FrameParameters(settings.FrameSize, settings.Hop));
            var pipeline = new TranscriptionPipeline(settings, new Radix2Fft());
            var actual = ScoreGrid.From(pipeline.Transcribe(source).Score);

            var cmp = ClosedLoopComparer.Compare(expected, actual, subdivisionTolerance: 1);
            if (!cmp.IsMatch)
            {
                string dir = Quarantine.Persist(c, pcm);
                throw new ClosedLoopMismatchException($"case {c.Id()} quarantined in {dir}: {cmp.Detail}\n{c.Describe()}");
            }
        }
        finally
        {
            if (File.Exists(wav)) File.Delete(wav);   // temp copy only; quarantine wrote its own
        }
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
CLOSED_LOOP_CASES=16 dotnet test --filter "FullyQualifiedName~ClosedLoopPropertyTests"
```

Expected PASS: every synthesized case transcribes back with matching count, exact pitches, and onset/duration within ±1 subdivision. If a case fails, this is the suite doing its job — a real bug in Steps 3–8. The failing MIDI + WAV are in the quarantine directory (`artifacts/closed-loop-quarantine/` by default); reproduce from them, fix the underlying step with @superpowers:systematic-debugging, and only then continue. Do not weaken the tolerance (Section 1 rule 8).

**Step 5 — Commit (the spec message lands here — this task is the step's headline):**

```bash
but status -fv
but commit step-09-closed-loop -m "test: closed-loop synthesize→transcribe property suite" --changes <ids> --status-after
```

---

## Task 6: Determinism guard

**Files:**
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/ClosedLoop/ClosedLoopDeterminismTests.cs`

**Step 1 — Write the failing test:** the same fixed case, synthesized and transcribed twice, must produce byte-identical audio (R8.2) and an identical score (non-negotiable 3). A `record struct` sequence compares by value, so `Assert.Equal` on the two grids is exact.

```csharp
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AudioClaudio.Application;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;           // Radix2Fft
using AudioClaudio.Infrastructure.Audio;      // WavAudioSource
using AudioClaudio.Tests.Signals;             // WavWriter
using Xunit;

namespace AudioClaudio.Tests.ClosedLoop;

public sealed class ClosedLoopDeterminismTests
{
    [Trait("Category", "Fast")]
    [Fact]
    public void Domain_output_is_deterministic_for_a_fixed_case()
    {
        var c = ClosedLoopGen.Fixed();

        float[] pcm1 = ClosedLoop.CreateSynthesizer().Render(c.Events, c.Rate);
        float[] pcm2 = ClosedLoop.CreateSynthesizer().Render(c.Events, c.Rate);
        Assert.Equal(Sha(pcm1), Sha(pcm2));                       // synthesis determinism (R8.2)

        Assert.Equal(TranscribeToGrid(c, pcm1), TranscribeToGrid(c, pcm2));   // transcription determinism
    }

    private static System.Collections.Generic.IReadOnlyList<NoteGridPosition> TranscribeToGrid(
        ClosedLoopCase c, float[] pcm)
    {
        var settings = TranscriptionSettings.ForTempo(c.TempoBpm) with
        {
            TimeSignature = c.TimeSignature,
            Subdivision = c.Subdivision,
        };
        string wav = Path.Combine(Path.GetTempPath(), $"acl_det_{Guid.NewGuid():N}.wav");
        WavWriter.WriteMonoFile(wav, pcm, c.Rate);
        try
        {
            using var source = WavAudioSource.FromFile(wav, new FrameParameters(settings.FrameSize, settings.Hop));
            return ScoreGrid.From(new TranscriptionPipeline(settings, new Radix2Fft()).Transcribe(source).Score);
        }
        finally
        {
            if (File.Exists(wav)) File.Delete(wav);
        }
    }

    private static string Sha(float[] pcm) =>
        Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(pcm.AsSpan())));
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~ClosedLoopDeterminismTests"
```

Expected FAILURE (before this file compiles): none of the pipeline types resolve if Tasks 1–5 were skipped; with them present this test simply exercises them. If it *fails on green* (grids differ), a domain algorithm has hidden nondeterminism (unordered iteration, an undefined tie-break) — that is a real defect to fix, not a test to relax.

**Step 3 — Minimal implementation:** none beyond Tasks 1–5 — this task is a guard over existing code. If the test fails on green, fix the offending Domain component so its output is bit-stable.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~ClosedLoopDeterminismTests"
```

Expected PASS: identical PCM hash and identical grid across both runs.

**Step 5 — Commit:**

```bash
but status -fv
but commit step-09-closed-loop -m "test(closedloop): determinism guard" --changes <ids> --status-after
```

---

## Task 7: Regression corpus harness

**Files:**
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/fixtures/regressions/.gitkeep`
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/fixtures/regressions/README.md`
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/ClosedLoop/RegressionCorpusTests.cs`

**Step 1 — Write the failing test:** every promoted `.mid` in `fixtures/regressions/` must transcribe back within tolerance forever (R9.3 "only ever gets harder"). An empty corpus passes vacuously.

```csharp
using System;
using System.IO;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi;   // MidiFileReader, MidiReadResult
using Xunit;

namespace AudioClaudio.Tests.ClosedLoop;

public sealed class RegressionCorpusTests
{
    [Trait("Category", "Fast")]
    [Fact]
    public void All_regression_fixtures_transcribe_within_tolerance()
    {
        string dir = Fixtures.RegressionsDir;
        if (!Directory.Exists(dir)) return;

        // MIDI stores no sample rate; the corpus was rendered at the generator's fixed rate (denominate here).
        var rate = new SampleRate(ClosedLoopGen.SampleRateHz);

        foreach (string mid in Directory.GetFiles(dir, "*.mid"))
        {
            MidiReadResult read = MidiFileReader.ReadFile(mid, rate);   // { Events, Tempo }
            var c = new ClosedLoopCase(
                rate,
                (int)Math.Round(read.Tempo.BeatsPerMinute),
                TimeSignature.FourFour,
                Subdivision.Sixteenth,
                read.Events);

            ClosedLoop.RunCase(c);   // throws (and re-quarantines) on any regression
        }
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~RegressionCorpusTests"
```

Expected FAILURE: compile error until the `fixtures/regressions/` directory exists and the test file is added. (With an empty corpus, once it compiles it passes — that is the intended baseline.)

**Step 3 — Minimal implementation:** create the corpus directory (kept in git even while empty) and document the promotion procedure.

`fixtures/regressions/.gitkeep` — empty file.

```markdown
<!-- fixtures/regressions/README.md -->
# Closed-loop regression corpus

Each `*.mid` here is a previously-failing closed-loop case (the *generated performance*,
written by `Quarantine.Persist`). `RegressionCorpusTests` re-runs every one through
`transcribe ∘ synthesize` and asserts it still returns within tolerance.

## Promotion procedure (R9.3)
1. The closed-loop suite fails and quarantines `<id>.mid` + `<id>.wav` under
   `artifacts/closed-loop-quarantine/`.
2. Reproduce, then fix the underlying Step 3–8 bug.
3. Copy the quarantined `<id>.mid` into this directory (the WAV is regenerated
   deterministically by the synth, so only the MIDI is committed).
4. Commit. The corpus only ever grows — the suite only ever gets harder.
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~RegressionCorpusTests"
```

Expected PASS: empty corpus → the loop body never runs → green baseline established.

**Step 5 — Commit:**

```bash
but status -fv
but commit step-09-closed-loop -m "test(closedloop): regression corpus harness" --changes <ids> --status-after
```

---

## Task 8: Run the closed loop in CI (R9.4)

**Files:**
- Modify: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/.github/workflows/ci.yml` — the **`- name: Test`** step created in Step 0 (whose body is `run: dotnet test --no-build --configuration Release --verbosity normal`). Add an `env:` block to that one step only; change no other job or step.
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/.github/workflows/nightly-closed-loop.yml` (optional larger run)

> This task configures CI rather than adding a unit test, so it deviates from the strict red→green shape; the "verify" is a local reproduction of the CI invocation. The suite already runs under the Step 0 `dotnet test` step — here we pin a modest push count and add the optional nightly (R9.4).

**Step 1 — Add a modest, reproducible closed-loop run to the push workflow.** Apply this exact edit to the Step 0 `- name: Test` step (the `run:` line is unchanged; only the `env:` block is added directly beneath it — the anchor is the step name and its `dotnet test` run command):

```diff
       - name: Test
         run: dotnet test --no-build --configuration Release --verbosity normal
+        env:
+          CLOSED_LOOP_CASES: "32"     # modest push count (R9.4)
+          CsCheck_Seed: "0"           # pin CsCheck for reproducible push runs; replace with a seed CsCheck prints on failure
```

**Step 2 — Add the optional nightly workflow** at `.github/workflows/nightly-closed-loop.yml`:

```yaml
name: nightly-closed-loop
on:
  schedule:
    - cron: "0 5 * * *"   # 05:00 UTC daily
  workflow_dispatch: {}

jobs:
  closed-loop:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"
      - name: Nightly closed loop (large corpus)
        run: dotnet test --filter "Category=Slow"
        env:
          CLOSED_LOOP_CASES: "1000"
      - name: Upload quarantine artifacts
        if: failure()
        uses: actions/upload-artifact@v4
        with:
          name: closed-loop-quarantine
          path: artifacts/closed-loop-quarantine/
```

**Step 3 — Verify locally** that the push-equivalent invocation is green and the Fast filter still excludes the slow suite:

```bash
CLOSED_LOOP_CASES=32 dotnet test                       # full suite incl. the Slow closed loop
dotnet test --filter "Category=Fast"                   # must skip the Slow property, stay green
```

Expected: the full run exercises the closed loop at 32 cases; the Fast filter runs everything except `ClosedLoopPropertyTests` and passes.

**Step 4 — (No code) confirm the workflow YAML is well-formed** by pushing the branch and watching the run, or with a local linter if available. The push workflow must show the closed-loop suite executing.

**Step 5 — Commit:**

```bash
but status -fv
but commit step-09-closed-loop -m "ci: run closed-loop suite (modest push, optional nightly)" --changes <ids> --status-after
```

---

## Task 9: Wire the `transcribe` CLI command (`feat(cli): transcribe`)

§7 requires `claudio transcribe <in.wav> --tempo N [--out-dir .]`, but no single build-spec step owned it; CONTRACTS §9 resolves the gap by assigning it to **Step 9** (the pipeline and MIDI writer both exist by now). This task adds the command to the CLI composition root, emitting `raw.mid` (the unquantized performance) and `score.mid` (the quantized score). `score.musicxml` is **not** produced here — Step 11 extends `transcribe` to also emit it once `MusicXmlScoreWriter` exists.

**Files:**
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Cli/Commands/TranscribeCommand.cs`
- Modify: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Cli/Program.cs` (add the `transcribe` case to the Step 8 `switch (args[0])` dispatch and extend `Usage()`)
- Test: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/Cli/TranscribeCommandTests.cs`

(No csproj change: Step 0's `AudioClaudio.Tests` already references `AudioClaudio.Cli` — the composition root is testable directly.)

**Step 1 — Write the failing test:** synthesize a sustained-note WAV, run the command handler, and demand both MIDI files land and re-read to the right note.

```csharp
using System;
using System.IO;
using AudioClaudio.Cli.Commands;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi;   // MidiFileReader (Step 7 reader)
using AudioClaudio.Tests.Signals;         // SignalGenerator, WavWriter
using Xunit;

namespace AudioClaudio.Tests.Cli;

public sealed class TranscribeCommandTests
{
    [Trait("Category", "Fast")]
    [Fact]
    public void Transcribe_emits_raw_and_score_midi_for_a_sustained_note()
    {
        var rate = new SampleRate(44100);
        float[] pcm = SignalGenerator.HarmonicStack(
            new Pitch(69).Frequency(), (int)(1.0 * rate.Hz), rate, partials: 6, decay: 1.0);

        string dir = Path.Combine(Path.GetTempPath(), $"acl_transcribe_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string wav = Path.Combine(dir, "in.wav");
        try
        {
            WavWriter.WriteMonoFile(wav, pcm, rate);

            TranscribeCommand.Run(wav, tempoBpm: 120, outDir: dir);

            string raw = Path.Combine(dir, "raw.mid");
            string score = Path.Combine(dir, "score.mid");
            Assert.True(File.Exists(raw), "raw.mid missing");
            Assert.True(File.Exists(score), "score.mid missing");

            var rawRead = MidiFileReader.ReadFile(raw, rate);
            Assert.Single(rawRead.Events);
            Assert.Equal(69, rawRead.Events[0].Pitch.MidiNumber);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~TranscribeCommandTests"
```

Expected FAILURE: compile error — `AudioClaudio.Cli.Commands.TranscribeCommand` does not exist.

**Step 3 — Minimal implementation:** the command handler (composition root; the only place these adapters are wired for `transcribe`). It builds a `WavAudioSource`, runs the `TranscriptionPipeline` with a Domain `Radix2Fft`, and writes both MIDI files via the Stream-based `DryWetMidiWriter`.

```csharp
// src/AudioClaudio.Cli/Commands/TranscribeCommand.cs
using System.IO;
using AudioClaudio.Application;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;        // Radix2Fft
using AudioClaudio.Infrastructure.Audio;   // WavAudioSource
using AudioClaudio.Infrastructure.Midi;    // DryWetMidiWriter

namespace AudioClaudio.Cli.Commands;

/// <summary>
/// Wires the file-based transcription pipeline for
/// <c>claudio transcribe &lt;in.wav&gt; --tempo N [--out-dir .]</c>. Emits <c>raw.mid</c> (the
/// unquantized performance) and <c>score.mid</c> (the quantized score). <c>score.musicxml</c> is
/// added by Step 11 once <c>MusicXmlScoreWriter</c> exists (CONTRACTS §9).
/// </summary>
public static class TranscribeCommand
{
    public static void Run(string inputWav, double tempoBpm, string outDir)
    {
        Directory.CreateDirectory(outDir);

        var settings = TranscriptionSettings.ForTempo(tempoBpm);
        var pipeline = new TranscriptionPipeline(settings, new Radix2Fft());   // Domain FFT (Option A)

        TranscriptionResult result;
        using (var source = WavAudioSource.FromFile(inputWav, new FrameParameters(settings.FrameSize, settings.Hop)))
            result = pipeline.Transcribe(source);

        var tempo = new Tempo(tempoBpm);
        var writer = new DryWetMidiWriter();

        using (var raw = File.Create(Path.Combine(outDir, "raw.mid")))
            writer.Write(result.RawEvents, tempo, raw);   // INoteEventWriter: raw performance

        using (var score = File.Create(Path.Combine(outDir, "score.mid")))
            writer.Write(result.Score, score);            // IScoreWriter: quantized score
    }
}
```

Then wire it into the Step 8 `Program.cs` dispatch. Add a `transcribe` case to the existing `switch (args[0])` (it does not use the synthesizer/soundfont that `render`/`play` construct) and extend `Usage()`. **Make SoundFont resolution lazy first:** Step 8 constructs `SoundFontLocator.Resolve(...)` + `MeltySynthSynthesizer` *unconditionally before the switch*, which would force `transcribe` to require a `.sf2` it never uses. Move that construction *into* the `render`/`play` cases (or a lazy `Lazy<ISynthesizer>` resolved only there) so `claudio transcribe x.wav --tempo 120` runs with no SoundFont present. The `synthesizer` referenced by the `render`/`play` cases below becomes that lazily-resolved value.

```diff
 switch (args[0])
 {
+    case "transcribe" when args.Length >= 2:
+    {
+        // claudio transcribe <in.wav> --tempo N [--out-dir .]
+        double tempo = double.Parse(
+            TryReadOption(args, "--tempo")
+                ?? throw new ArgumentException("transcribe requires --tempo <bpm>"),
+            System.Globalization.CultureInfo.InvariantCulture);
+        string outDir = TryReadOption(args, "--out-dir") ?? ".";
+        AudioClaudio.Cli.Commands.TranscribeCommand.Run(args[1], tempo, outDir);
+        return 0;
+    }
     case "render" when args.Length >= 3:
     {
         // Step 7 reader: load the committed/source MIDI into domain NoteEvents.
         IReadOnlyList<NoteEvent> notes = MidiFileReader.ReadFile(args[1], rate).Events;
         RenderCommand.RenderToWav(notes, synthesizer, rate, args[2]);
         return 0;
     }
     default:
         return Usage();
 }
```

```diff
 static int Usage()
 {
-    Console.Error.WriteLine("usage: claudio <render|play> <in.mid> [<out.wav>] [--soundfont <path>]");
+    Console.Error.WriteLine("usage: claudio <transcribe|render|play> ...");
+    Console.Error.WriteLine("  transcribe <in.wav> --tempo <bpm> [--out-dir <dir>]   → raw.mid, score.mid");
+    Console.Error.WriteLine("  render|play <in.mid> [<out.wav>] [--soundfont <path>]");
     return 1;
 }
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~TranscribeCommandTests"
```

Expected PASS: `raw.mid` and `score.mid` are written to the out-dir, and `raw.mid` re-reads to a single MIDI-69 event. This is exactly what `claudio transcribe <in.wav> --tempo 120` does, minus the arg parsing.

**Step 5 — Commit:**

```bash
but status -fv
but commit step-09-closed-loop -m "feat(cli): transcribe command (raw.mid + score.mid)" --changes <ids> --status-after
```

---

## Verify (step exit criteria)

Section 6 Step 9 states no separate *Verify* block; its exit criteria are the requirements themselves plus the R9.2 pass criteria. All must hold:

- [ ] **R9.1** — The property suite generates constrained monophonic scores (MIDI 33–96, notes ≥ eighth, 60–140 BPM, ≥ 1 grid rest), synthesizes them, transcribes at the known tempo, and compares (`GeneratorConstraintTests`, `ClosedLoopPropertyTests`, and the pipeline proven by `PipelineIntegrationTests`).
- [ ] **R9.2** — For every case: note count matches; every pitch matches exactly; every onset within ±1 subdivision; every duration within ±1 subdivision (`ClosedLoopComparerTests` unit-prove the rule; `ClosedLoopPropertyTests` enforce it across the corpus).
- [ ] **R9.3** — Failing cases persist their generated MIDI + rendered WAV to the quarantine directory (`QuarantineTests`), and `fixtures/regressions/` re-runs promoted cases forever (`RegressionCorpusTests`).
- [ ] **R9.4** — The suite runs in CI at a modest push count (`CLOSED_LOOP_CASES`), with an optional larger nightly workflow.
- [ ] Determinism (non-negotiable 3) — identical audio and identical score across two runs of a fixed case (`ClosedLoopDeterminismTests`).
- [ ] §7 `transcribe` CLI (CONTRACTS §9 assigns it to Step 9) — `claudio transcribe <in.wav> --tempo N [--out-dir .]` emits `raw.mid` + `score.mid` (`TranscribeCommandTests`); `score.musicxml` is added by Step 11.

## Definition of Done

- [ ] `dotnet build` succeeds with warnings-as-errors clean.
- [ ] `dotnet format` reports no changes.
- [ ] All new tests green; `dotnet test --filter Category=Fast` is green and skips the Slow closed-loop property.
- [ ] The full suite (`CLOSED_LOOP_CASES=32 dotnet test`) passes, including `Transcribe_of_Synthesize_recovers_the_score_within_tolerance`.
- [ ] Dependency rule intact: `AudioClaudio.Application` (the `TranscriptionPipeline`) references only Domain + its own ports, and takes the FFT as the injected Domain interface `IFourierTransform` (never the concrete `Radix2Fft`/Infrastructure). Infrastructure adapters (`WavAudioSource`, `MeltySynthSynthesizer`, `DryWetMidiWriter`, `MidiFileReader`) are constructed only in composition roots — the test project (closed loop) and the CLI (`TranscribeCommand`). `AudioClaudio.Domain` still references nothing beyond the BCL.
- [ ] `claudio transcribe` is wired in the CLI (Task 9): `raw.mid` + `score.mid` emitted; `TranscribeCommandTests` green.
- [ ] Committed via GitButler on branch `step-09-closed-loop`; the headline commit uses the spec message `test: closed-loop synthesize→transcribe property suite` (finer per-task commits roll up to it, including `feat(cli): transcribe`).
- [ ] Requirement-coverage table fully satisfied (every R9.x maps to a task and a green test).
- [ ] `DECISIONS.md` — no update required: Step 9 introduces **no new NuGet package** (CsCheck, xUnit, DryWetMIDI, MeltySynth all logged in earlier steps) and has **no *Design decision* gate**. If you record the closed-loop WAV-round-trip implementation choice, do so as an implementation note only.
- [ ] Ran @superpowers:verification-before-completion: every "green" claim above backed by an actual command run, not assumed.
