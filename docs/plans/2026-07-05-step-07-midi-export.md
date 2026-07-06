# Step 7 — MIDI Export via DryWetMIDI — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Build-spec step:** Section 6 Step 7 (R7.1, R7.2)
**Goal:** Serialize both a quantized `Score` and a raw `[NoteEvent]` performance to standard MIDI files through a DryWetMIDI Infrastructure adapter — and read them back — losslessly with respect to each source's own precision. The read-back path (`MidiFileReader`) is what makes R7.2's round-trip concrete, and Steps 8 and 9 both depend on it.
**Architecture:** This is the first Infrastructure adapter over the domain's *output*. It lives in `AudioClaudio.Infrastructure.Midi` and implements two ports declared in `AudioClaudio.Application.Ports` (`IScoreWriter`, `INoteEventWriter`). `INoteEventWriter` is a deliberate **6th port** beyond the constitution's five illustrative ports (`IAudioSource`/`ITranscriber`/`ISynthesizer`/`IScoreWriter`/`IClock`), justified by R7.1's requirement to write **both** a `Score` and a raw `[NoteEvent]` list — flagged here for Cornelius (CONTRACTS.md §7 records it as canonical). It also ships a concrete `MidiFileReader` (no port — an Infrastructure reader Steps 8 and 9 construct directly). The Domain is untouched — it never learns MIDI exists. The CLI (composition root, Step 10+) is the only place the concrete adapter is constructed and handed to the ports; this step does not wire the CLI.
**Tech Stack:** DryWetMIDI (`Melanchall.DryWetMidi`, MIT) for standard MIDI file read/write; xUnit for example tests; CsCheck for the round-trip properties.
**Prerequisites:** Step 0 (scaffold — the four projects and references exist; `AudioClaudio.Infrastructure` already references `Application` + `Domain`, and `tests/AudioClaudio.Tests` references all four). Step 1 (`Pitch`, `SampleRate`, `SamplePosition`, `SampleDuration`, `NoteEvent`). Step 6 (`Score`, `Tempo`, `TimeSignature`, `Measure`, and the rhythmic-value model). All must be green and committed first (Section 1 rule 3).
**Commit (spec):** `feat(infra): MIDI export via DryWetMIDI`

---

## ⚠ Cross-step assumptions (now pinned by CONTRACTS.md — this is NOT a design gate)

Step 7 has **no *Design decision*** in the spec, so there is no decision gate for Cornelius. It consumes types defined by Steps 1 and 6; those names are pinned by `docs/plans/CONTRACTS.md` (the authoritative cross-step contract), so this plan codes against the canonical shapes below rather than guesses.

Canonical types this step consumes (verbatim from CONTRACTS.md §1 and §6):

```csharp
// Step 1 (Domain) — §1
public readonly record struct SampleRate { public int Hz { get; } }
public readonly record struct SamplePosition { public long Samples { get; } public SampleRate Rate { get; } }
public readonly record struct SampleDuration { public long Samples { get; } public SampleRate Rate { get; } }
public readonly record struct Pitch { public int MidiNumber { get; } /* 21..108 */ }
public readonly record struct NoteEvent(Pitch Pitch, SamplePosition Onset, SampleDuration Duration, int Velocity);

// Step 6 (Domain) — §6
public readonly record struct Tempo { public double BeatsPerMinute { get; } public Tempo(double beatsPerMinute); }
public readonly record struct TimeSignature { public int BeatsPerMeasure { get; } public int BeatUnit { get; } public static TimeSignature FourFour { get; } }
public enum Subdivision { /* Whole .. Sixteenth (+ dotted) */ }
public static class SubdivisionExtensions { public static int TicksPerQuarter(this Subdivision subdivision); } // grid ticks per quarter
public enum ElementKind { Note, Rest }
public readonly record struct ScoreElement(ElementKind Kind, Pitch? Pitch, int Velocity, int LengthTicks, bool TiedToNext)
{
    public static ScoreElement Note(Pitch pitch, int velocity, int lengthTicks, bool tiedToNext = false);
    public static ScoreElement Rest(int lengthTicks);
}
public sealed class Measure { public IReadOnlyList<ScoreElement> Elements { get; } public Measure(IReadOnlyList<ScoreElement> elements); }
public sealed class Score
{
    public Tempo Tempo { get; }
    public TimeSignature TimeSignature { get; }
    public Subdivision Subdivision { get; }
    public IReadOnlyList<Measure> Measures { get; }
    public Score(Tempo tempo, TimeSignature timeSignature, Subdivision subdivision, IReadOnlyList<Measure> measures);
}
```

**Key consequences for this adapter:**
- Sample rate is read as **`.Hz`** (never `.Hertz`); tempo as **`Tempo.BeatsPerMinute`** (never `.Bpm`); time signature as **`.BeatsPerMeasure`/`.BeatUnit`** (never `Numerator/Denominator`).
- A `ScoreElement` carries its length directly in **grid ticks** (`LengthTicks`), where one quarter note is `score.Subdivision.TicksPerQuarter()` grid ticks. The writer converts grid ticks to MIDI ticks (see Approach); it does not re-derive durations from note names. `ElementKind.Note` elements carry a non-null `Pitch`; `ElementKind.Rest` elements only advance the cursor.
- **Ports live in `AudioClaudio.Application.Ports`** (CONTRACTS.md §0/§7 — folder `src/AudioClaudio.Application/Ports/`, namespace `AudioClaudio.Application.Ports`). Every port and every `using` in this step uses that namespace.

**`INoteEventWriter` is a deliberate 6th port.** The constitution lists five illustrative Application ports (`IAudioSource`/`ITranscriber`/`ISynthesizer`/`IScoreWriter`/`IClock`); `INoteEventWriter` is a documented addition justified by R7.1 (write **both** a `Score` and a raw `[NoteEvent]` list). CONTRACTS.md §7 records it as the canonical 6th port; it is flagged here for Cornelius so parallel steps (notably Step 10's `listen`, which also emits raw MIDI) wire the same port name and signature.

*Seam for a later step:* `IScoreWriter` is deliberately format-agnostic — Step 11's `MusicXmlScoreWriter` implements the same port. Do not add MIDI specifics to the interface.

---

## Approach

Two write paths, one adapter, one tick grid.

**Why a tick resolution, and why it must be chosen (R7.2).** A standard MIDI file measures time in *ticks*, and a track's header declares how many ticks make one quarter note (PPQN — pulses per quarter note). Musical time (ticks) and wall time (seconds) are related through the tempo: at *B* BPM there are *B* quarter notes per minute, so `ticksPerSecond = PPQN · B / 60`. The requirement is that grid positions serialize **exactly**. The MVP grid runs from whole notes down to sixteenths, plus dotted values. A sixteenth is a quarter/4; a dotted sixteenth is 3/8 of a quarter. To land every one of those on an integer tick, PPQN must be divisible by 8. **We pin PPQN = 480** (the DAW-standard; `480 = 2⁵·3·5`): quarter = 480, eighth = 240, sixteenth = 120, and their dotted forms 720/360/180 — all integers. That is what makes the `Score` round-trip *exact*, not merely close.

**The `Score` path (clean/quantized).** A `Score` is already on the grid: each `ScoreElement` carries its length as an integer count of **grid ticks** (`LengthTicks`), where one quarter note is `score.Subdivision.TicksPerQuarter()` grid ticks. We convert grid ticks to MIDI ticks by `midiTicks = LengthTicks · PPQN / gridTicksPerQuarter` — exact integer arithmetic with no rounding, because PPQN (480) is a multiple of every MVP grid resolution. We walk the measures — each measure starts at `measureIndex · ticksPerMeasure` (for 4/4, `BeatsPerMeasure · 4·PPQN / BeatUnit = 1920`), a cursor advances by each element's MIDI-tick length, `ElementKind.Note` elements emit `NoteOn`/`NoteOff`, `ElementKind.Rest` elements only advance the cursor. Add a tempo event (`microsecondsPerQuarter = 60 000 000 / BeatsPerMinute`) and a time-signature event from `BeatsPerMeasure`/`BeatUnit`. Re-reading yields the identical ticks — losslessness is exact here.

**The raw `[NoteEvent]` path (the performance).** Raw events live in **integer samples**, not on any grid — that is the whole point of keeping the unquantized performance. To serialize them we map sample time to ticks through the declared tempo: `ticks = round(samples · PPQN · BPM / (60 · sampleRate))`. This rounding is the only lossy step, and it is bounded by one tick — exactly the "within MIDI tick resolution" that R7.2 permits. The round-trip property asserts every reconstructed onset/duration is within one tick's worth of samples of the original, and every pitch is bit-exact.

**The read-back path (`MidiFileReader`, R7.2).** R7.2's losslessness claim is only meaningful if the file can be read back, and Steps 8 (`render`/`play`) and 9 (closed-loop quarantine + regression corpus) both load a `.mid` into `[NoteEvent]`. This step therefore ships a concrete `MidiFileReader` in `AudioClaudio.Infrastructure.Midi`: `Read(Stream, SampleRate)` and `ReadFile(string, SampleRate)` return a `MidiReadResult { Events, Tempo }`. MIDI stores no sample rate, so the caller supplies one to denominate the recovered `SamplePosition`s; the tempo is recovered from the file's `SetTempo` event (`BeatsPerMinute = 60 000 000 / microsecondsPerQuarter`). Ticks map back to samples through the inverse relation, `samples = round(ticks · 60 · rate.Hz / (PPQN · BeatsPerMinute))`. The reader is **not** a port — it is a concrete Infrastructure type its consumers construct directly.

**Determinism (non-negotiable 3).** A MIDI file embeds no timestamps or randomness; DryWetMIDI serializes a given object graph identically every time. We convert seconds↔ticks with a single `Math.Round` per value at the serialization *edge* (non-negotiable 1 permits seconds as an edge conversion), never accumulating floating time. A dedicated test writes the same input twice and demands byte-identical output.

Time is integer samples in the domain, tempo/tick math happens only in this Infrastructure adapter, and pitch is carried as an exact MIDI number — the non-negotiables hold.

Follow @superpowers:test-driven-development (red → green → refactor) for every task; reach for @superpowers:systematic-debugging if a property shrinks to a failing case.

---

## Requirements coverage

| Requirement | Task(s) | Proven by (test) |
|---|---|---|
| **R7.1** — adapter writes both a `Score` and a raw `[NoteEvent]` list to standard MIDI files | Tasks 1, 2 (raw list); Tasks 3, 4 (score) | `WritesSingleRawEventToReadableMidi`, `RawEventRoundTripPreservesPitchAndTimingWithinOneTick`, `WritesScoreMeasureToExactTicks`, `ScoreRoundTripIsExactOnTheGrid` |
| **R7.2** — lossless round-trip w.r.t. source precision; tick resolution chosen so grid positions are exact; **re-reading the file yields the same pitches/onsets/durations** | Tasks 1, 3 (exact-tick examples); Tasks 2, 4 (write→read properties); Task 5 (determinism); **Task 6 (`MidiFileReader` read-back)** | `RawEventRoundTripPreservesPitchAndTimingWithinOneTick`, `ScoreRoundTripIsExactOnTheGrid`, `WritesScoreMeasureToExactTicks`, `WriteIsDeterministicForScoreAndEvents`, **`ReadsBackRawEventsWithinOneTick`, `ReaderRoundTripPreservesPitchAndTimingWithinOneTick`** |

---

## Task 1: `INoteEventWriter` port + DryWetMIDI raw-event writer (single-event example)

Introduces the Application port, the Infrastructure adapter, the DryWetMIDI dependency, the `DECISIONS.md` entries, the pinned tick resolution, and the samples↔ticks math — all driven by one concrete example.

**Files:**
- Create: `src/AudioClaudio.Application/Ports/INoteEventWriter.cs`
- Create: `src/AudioClaudio.Infrastructure/Midi/DryWetMidiWriter.cs`
- Modify: `src/AudioClaudio.Infrastructure/AudioClaudio.Infrastructure.csproj` (add the `Melanchall.DryWetMidi` package reference)
- Create/Modify: `DECISIONS.md` (append; create if Step 3 hasn't yet)
- Test: `tests/AudioClaudio.Tests/Infrastructure/DryWetMidiWriterTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System.IO;
using System.Linq;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class DryWetMidiWriterTests
{
    // A quarter-note-equivalent worth of samples: at 44100 Hz and 120 BPM,
    // 0.5 s = 22050 samples = exactly one beat = 480 ticks; 1.0 s = 44100 samples = 960 ticks.
    [Fact]
    [Trait("Category", "Fast")]
    public void WritesSingleRawEventToReadableMidi()
    {
        var rate = new SampleRate(44100);
        var events = new[]
        {
            new NoteEvent(
                new Pitch(60),                              // middle C
                new SamplePosition(22050, rate),            // 0.5 s in
                new SampleDuration(44100, rate),            // 1.0 s long
                velocity: 80),
        };

        INoteEventWriter writer = new DryWetMidiWriter();
        using var stream = new MemoryStream();
        writer.Write(events, new Tempo(120), stream);

        stream.Position = 0;
        var midi = MidiFile.Read(stream);

        // Tick resolution is fixed at 480 PPQN.
        var division = Assert.IsType<TicksPerQuarterNoteTimeDivision>(midi.TimeDivision);
        Assert.Equal((short)480, division.TicksPerQuarterNote);

        var notes = midi.GetNotes().OrderBy(n => n.Time).ToList();
        var note = Assert.Single(notes);
        Assert.Equal(60, (int)note.NoteNumber);   // pitch is bit-exact
        Assert.Equal(480L, note.Time);            // 0.5 s @ 120 BPM = 1 beat = 480 ticks
        Assert.Equal(960L, note.Length);          // 1.0 s = 2 beats = 960 ticks
        Assert.Equal(80, (int)note.Velocity);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~DryWetMidiWriterTests.WritesSingleRawEventToReadableMidi"
```

Expected FAILURE: compile error — `INoteEventWriter`, `DryWetMidiWriter`, and the `Melanchall.DryWetMidi.*` namespaces do not exist yet. This proves the test is exercising code that has to be written.

**Step 3 — Minimal implementation:**

Add the package to Infrastructure (verify the exact latest 7.x on nuget.org and pin it):

```xml
<ItemGroup>
  <PackageReference Include="Melanchall.DryWetMidi" Version="7.2.0" />
</ItemGroup>
```

```csharp
// src/AudioClaudio.Application/Ports/INoteEventWriter.cs
using AudioClaudio.Domain;

namespace AudioClaudio.Application.Ports;

/// <summary>
/// Writes a raw, unquantized performance (a list of <see cref="NoteEvent"/> in
/// integer sample time) to a standard MIDI file. The declared <see cref="Tempo"/>
/// supplies the tempo map that maps sample time to MIDI ticks.
///
/// Deliberate 6th Application port beyond the constitution's five illustrative
/// ports (IAudioSource/ITranscriber/ISynthesizer/IScoreWriter/IClock), justified
/// by R7.1 (write BOTH a Score and a raw NoteEvent list). Recorded in CONTRACTS.md §7.
/// </summary>
public interface INoteEventWriter
{
    void Write(IReadOnlyList<NoteEvent> events, Tempo tempo, Stream destination);
}
```

```csharp
// src/AudioClaudio.Infrastructure/Midi/DryWetMidiWriter.cs
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace AudioClaudio.Infrastructure.Midi;

/// <summary>
/// DryWetMIDI adapter that serializes both a quantized <see cref="Score"/> and a
/// raw <see cref="NoteEvent"/> performance to standard MIDI files.
/// </summary>
public sealed class DryWetMidiWriter : INoteEventWriter
{
    /// <summary>
    /// Ticks per quarter note. 480 = 2^5·3·5 makes every MVP grid value
    /// (whole..sixteenth and their dotted forms) land on an integer tick, so
    /// score grid positions serialize losslessly (R7.2). Also the DAW-standard.
    /// </summary>
    public const short TicksPerQuarterNote = 480;

    public void Write(IReadOnlyList<NoteEvent> events, Tempo tempo, Stream destination)
    {
        var extra = new List<ITimedObject> { TempoEvent(tempo) };
        var notes = new List<TimedNote>(events.Count);
        foreach (var e in events)
        {
            long onset = SamplesToTicks(e.Onset.Samples, e.Onset.Rate, tempo.BeatsPerMinute);
            long length = SamplesToTicks(e.Duration.Samples, e.Duration.Rate, tempo.BeatsPerMinute);
            // A sounding note must have positive length; a sub-tick duration
            // clamps to one tick, still within the R7.2 tick-resolution bound.
            notes.Add(new TimedNote(e.Pitch.MidiNumber, onset, Math.Max(1, length), e.Velocity));
        }

        WriteTrack(extra, notes, destination);
    }

    // ---- shared helpers -------------------------------------------------

    private static long SamplesToTicks(long samples, SampleRate rate, double bpm)
    {
        // ticksPerSecond = PPQN · BPM / 60  ⇒  ticks = samples/rate · ticksPerSecond
        double ticks = (double)samples * TicksPerQuarterNote * bpm / (60.0 * rate.Hz);
        return (long)Math.Round(ticks, MidpointRounding.ToEven);
    }

    private static TimedEvent TempoEvent(Tempo tempo)
    {
        long microsecondsPerQuarter = (long)Math.Round(60_000_000.0 / tempo.BeatsPerMinute);
        return new TimedEvent(new SetTempoEvent(microsecondsPerQuarter), 0);
    }

    private static void WriteTrack(
        IReadOnlyList<ITimedObject> headerEvents,
        IReadOnlyList<TimedNote> notes,
        Stream destination)
    {
        var objects = new List<ITimedObject>(headerEvents);
        foreach (var n in notes)
        {
            objects.Add(new Note((SevenBitNumber)n.MidiNumber, n.TickLength, n.TickOnset)
            {
                Velocity = (SevenBitNumber)n.Velocity,
            });
        }

        var midiFile = new MidiFile(objects.ToTrackChunk())
        {
            TimeDivision = new TicksPerQuarterNoteTimeDivision(TicksPerQuarterNote),
        };
        midiFile.Write(destination);
    }

    private readonly record struct TimedNote(int MidiNumber, long TickOnset, long TickLength, int Velocity);
}
```

Create/append `DECISIONS.md`:

```markdown
## Step 7 — MIDI export

### NuGet: Melanchall.DryWetMidi
- Version: 7.2.0 (pin the exact version `dotnet add` resolves).
- License: **MIT** — compatible with UNLICENSE (Section 1 rule 7); no copyleft in the graph.
- Role: standard MIDI file read/write, used only by the Infrastructure MIDI adapter.
  Never referenced from Domain or Application implementations.

### Tick resolution (PPQN) — R7.2
- Chosen: **480 ticks per quarter note.**
- Rationale: 480 = 2^5·3·5 makes every MVP grid value land on an integer tick —
  quarter 480, eighth 240, sixteenth 120, dotted 720/360/180 — so a quantized
  Score serializes to MIDI *exactly*. Raw (unquantized) events round only once,
  at the sample→tick edge, bounded by one tick. Also the DAW-standard PPQN.
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~DryWetMidiWriterTests.WritesSingleRawEventToReadableMidi"
```

Expected PASS: one note round-trips through a real MIDI file at 480 PPQN with exact pitch and exact ticks.

**Step 5 — Commit** (via the @gitbutler skill; `<ids>` come from `but status -fv` / `but diff` at execution time):

```bash
but branch new step-07-midi-export && but mark step-07-midi-export
but commit step-07-midi-export -m "feat(infra): INoteEventWriter port + DryWetMIDI raw-event writer" --changes <ids> --status-after
```

---

## Task 2: Raw-event round-trip property (R7.2 for the performance path)

Thousands of random monophonic performances must survive a write→read→compare with pitches bit-exact and timing within one tick.

**Files:**
- Test: `tests/AudioClaudio.Tests/Infrastructure/MidiRoundTripPropertyTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System.IO;
using System.Linq;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi;
using CsCheck;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class MidiRoundTripPropertyTests
{
    // A monotonic, non-overlapping monophonic performance: pitch 33..96,
    // a gap then a positive duration, all at one sample rate.
    private static readonly Gen<(int Bpm, int RateHz, NoteEvent[] Events)> GenPerformance =
        from bpm in Gen.Int[60, 140]
        from rateHz in Gen.Const(44100)
        from count in Gen.Int[1, 6]
        from pitches in Gen.Int[33, 96].Array[count]
        from gaps in Gen.Int[0, 20000].Array[count]
        from lengths in Gen.Int[2000, 40000].Array[count]
        from velocities in Gen.Int[1, 127].Array[count]
        select (bpm, rateHz, BuildEvents(rateHz, pitches, gaps, lengths, velocities));

    private static NoteEvent[] BuildEvents(int rateHz, int[] pitches, int[] gaps, int[] lengths, int[] velocities)
    {
        var rate = new SampleRate(rateHz);
        var events = new NoteEvent[pitches.Length];
        long cursor = 0;
        for (int i = 0; i < pitches.Length; i++)
        {
            cursor += gaps[i];
            events[i] = new NoteEvent(
                new Pitch(pitches[i]),
                new SamplePosition(cursor, rate),
                new SampleDuration(lengths[i], rate),
                velocities[i]);
            cursor += lengths[i];
        }

        return events;
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void RawEventRoundTripPreservesPitchAndTimingWithinOneTick()
    {
        GenPerformance.Sample(sample =>
        {
            var (bpm, rateHz, events) = sample;

            INoteEventWriter writer = new DryWetMidiWriter();
            using var stream = new MemoryStream();
            writer.Write(events, new Tempo(bpm), stream);

            stream.Position = 0;
            var notes = MidiFile.Read(stream).GetNotes().OrderBy(n => n.Time).ToList();

            Assert.Equal(events.Length, notes.Count);   // note count matches

            // One tick, expressed in samples, is the R7.2 tolerance.
            double samplesPerTick = 60.0 * rateHz / (DryWetMidiWriter.TicksPerQuarterNote * bpm);
            long tolerance = (long)Math.Ceiling(samplesPerTick);

            for (int i = 0; i < events.Length; i++)
            {
                Assert.Equal(events[i].Pitch.MidiNumber, (int)notes[i].NoteNumber);   // exact
                Assert.Equal(events[i].Velocity, (int)notes[i].Velocity);             // exact

                long onsetBack = (long)Math.Round(notes[i].Time * samplesPerTick);
                long durationBack = (long)Math.Round(notes[i].Length * samplesPerTick);
                Assert.True(Math.Abs(onsetBack - events[i].Onset.Samples) <= tolerance,
                    $"onset off by more than one tick: {onsetBack} vs {events[i].Onset.Samples}");
                Assert.True(Math.Abs(durationBack - events[i].Duration.Samples) <= tolerance,
                    $"duration off by more than one tick: {durationBack} vs {events[i].Duration.Samples}");
            }
        }, iter: 500);
        // CsCheck prints a `seed:` on any failure — paste it back into Sample(...) to replay exactly.
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~MidiRoundTripPropertyTests.RawEventRoundTripPreservesPitchAndTimingWithinOneTick"
```

Expected FAILURE: it fails to compile until this file exists (the writer from Task 1 is already present, so the *behavior* would pass — the red here is the new test file compiling in). If you want a genuine behavioral red first, temporarily change the writer's `SamplesToTicks` to return `0`, watch the property fail on timing, then restore it. Otherwise Step 4's green is the confirmation.

**Step 3 — Minimal implementation:** none — Task 1's `DryWetMidiWriter` already satisfies this property. (If Step 2's red was forced by breaking `SamplesToTicks`, restore it now.)

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~MidiRoundTripPropertyTests.RawEventRoundTripPreservesPitchAndTimingWithinOneTick"
```

Expected PASS: 500 random performances round-trip with exact pitch/velocity and onset/duration within one tick.

**Step 5 — Commit:**

```bash
but commit step-07-midi-export -m "test(infra): raw-event MIDI round-trip property" --changes <ids> --status-after
```

---

## Task 3: `IScoreWriter` port + Score→MIDI writer (exact-tick example)

The quantized path. A hand-built one-measure `Score` with mixed note values must serialize to precisely the ticks computed by hand — plus the tempo and time-signature events.

**Files:**
- Create: `src/AudioClaudio.Application/Ports/IScoreWriter.cs`
- Modify: `src/AudioClaudio.Infrastructure/Midi/DryWetMidiWriter.cs` (implement `IScoreWriter`)
- Test: `tests/AudioClaudio.Tests/Infrastructure/DryWetMidiWriterTests.cs` (add a test)

**Step 1 — Write the failing test** (add to `DryWetMidiWriterTests`):

```csharp
    // One 4/4 measure on a sixteenth grid: half note + quarter rest + quarter note.
    // Lengths are given in GRID ticks (LengthTicks); q = grid ticks per quarter.
    // half = 2q, rest = q, quarter = q -> 4q = a full 4/4 bar. In MIDI ticks
    // (LengthTicks * 480 / q, independent of q): 960, (skip) 480, 480.
    [Fact]
    [Trait("Category", "Fast")]
    public void WritesScoreMeasureToExactTicks()
    {
        int q = Subdivision.Sixteenth.TicksPerQuarter();   // grid ticks per quarter note
        var elements = new[]
        {
            ScoreElement.Note(new Pitch(60), velocity: 80, lengthTicks: 2 * q),   // half at bar start
            ScoreElement.Rest(lengthTicks: q),                                    // quarter rest
            ScoreElement.Note(new Pitch(64), velocity: 80, lengthTicks: q),       // quarter after the rest
        };
        var score = new Score(
            new Tempo(120),
            TimeSignature.FourFour,
            Subdivision.Sixteenth,
            new[] { new Measure(elements) });

        IScoreWriter writer = new DryWetMidiWriter();
        using var stream = new MemoryStream();
        writer.Write(score, stream);

        stream.Position = 0;
        var midi = MidiFile.Read(stream);

        // Tempo: 120 BPM = 500000 microseconds per quarter note.
        var setTempo = midi.GetTrackChunks()
            .SelectMany(c => c.Events)
            .OfType<SetTempoEvent>()
            .Single();
        Assert.Equal(500_000L, setTempo.MicrosecondsPerQuarterNote);

        var notes = midi.GetNotes().OrderBy(n => n.Time).ToList();
        var expected = new (int Midi, long Time, long Length)[]
        {
            (60, 0, 960),       // half at bar start
            (64, 1440, 480),    // quarter after half (960) + quarter rest (480)
        };
        Assert.Equal(expected.Length, notes.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Midi, (int)notes[i].NoteNumber);
            Assert.Equal(expected[i].Time, notes[i].Time);
            Assert.Equal(expected[i].Length, notes[i].Length);
        }
    }
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~DryWetMidiWriterTests.WritesScoreMeasureToExactTicks"
```

Expected FAILURE: compile error — `IScoreWriter` does not exist and `DryWetMidiWriter` has no `Write(Score, Stream)` overload.

**Step 3 — Minimal implementation:**

```csharp
// src/AudioClaudio.Application/Ports/IScoreWriter.cs
using AudioClaudio.Domain;

namespace AudioClaudio.Application.Ports;

/// <summary>
/// Writes a quantized <see cref="Score"/> to a destination. Format-agnostic:
/// the MIDI writer implements it here; the MusicXML writer will implement it in Step 11.
/// </summary>
public interface IScoreWriter
{
    void Write(Score score, Stream destination);
}
```

Extend `DryWetMidiWriter` — add `IScoreWriter` to the class declaration and the members below:

```csharp
public sealed class DryWetMidiWriter : INoteEventWriter, IScoreWriter
{
    // ... existing const, INoteEventWriter.Write, and helpers unchanged ...

    public void Write(Score score, Stream destination)
    {
        var header = new List<ITimedObject>
        {
            TempoEvent(score.Tempo),
            new TimedEvent(
                new TimeSignatureEvent(
                    (byte)score.TimeSignature.BeatsPerMeasure,
                    (byte)score.TimeSignature.BeatUnit),
                0),
        };

        WriteTrack(header, FlattenScore(score), destination);
    }

    // Walks measures on the grid; ElementKind.Note emits, ElementKind.Rest only
    // advances the cursor. LengthTicks is in GRID ticks (a quarter note is
    // score.Subdivision.TicksPerQuarter() of them); convert to MIDI ticks by
    // multiplying by PPQN / gridTicksPerQuarter — exact because 480 is a multiple
    // of every MVP grid resolution.
    private static IReadOnlyList<TimedNote> FlattenScore(Score score)
    {
        int gridTicksPerQuarter = score.Subdivision.TicksPerQuarter();
        long ticksPerMeasure =
            score.TimeSignature.BeatsPerMeasure * (4L * TicksPerQuarterNote) / score.TimeSignature.BeatUnit;

        var notes = new List<TimedNote>();
        for (int m = 0; m < score.Measures.Count; m++)
        {
            long cursor = m * ticksPerMeasure;
            foreach (var element in score.Measures[m].Elements)
            {
                long length = (long)element.LengthTicks * TicksPerQuarterNote / gridTicksPerQuarter;
                if (element.Kind == ElementKind.Note)
                {
                    // Tied segments (TiedToNext) are emitted as adjacent notes; MIDI
                    // tie-merging for playback is a documented later refinement.
                    notes.Add(new TimedNote(element.Pitch!.Value.MidiNumber, cursor, length, element.Velocity));
                }

                cursor += length;
            }
        }

        return notes;
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~DryWetMidiWriterTests.WritesScoreMeasureToExactTicks"
```

Expected PASS: the four notes land on ticks 0/960/1200/1440 with lengths 960/240/240/480, and the tempo event reads 500000.

**Step 5 — Commit:**

```bash
but commit step-07-midi-export -m "feat(infra): IScoreWriter + Score-to-MIDI writer" --changes <ids> --status-after
```

---

## Task 4: Score round-trip property (R7.2 for the quantized path)

Random multi-measure monophonic scores must serialize and re-read to exactly the grid ticks — the losslessness claim, generalized. To keep every generated measure a valid full 4/4 bar, each measure is four quarter-slots, each slot a note or a rest.

**Files:**
- Test: `tests/AudioClaudio.Tests/Infrastructure/MidiRoundTripPropertyTests.cs` (add a test)

**Step 1 — Write the failing test** (add to `MidiRoundTripPropertyTests`):

```csharp
    // Each measure is four quarter-slots (note or rest) → always a full 4/4 bar.
    // Lengths are one quarter of GRID ticks per slot, on a sixteenth grid.
    private static readonly int QuarterGridTicks = Subdivision.Sixteenth.TicksPerQuarter();

    private static readonly Gen<ScoreElement> GenSlot =
        from isNote in Gen.Bool
        from midi in Gen.Int[33, 96]
        from velocity in Gen.Int[1, 127]
        select isNote
            ? ScoreElement.Note(new Pitch(midi), velocity, QuarterGridTicks)
            : ScoreElement.Rest(QuarterGridTicks);

    private static readonly Gen<Measure> GenMeasure =
        GenSlot.Array[4].Select(elements => new Measure(elements));

    private static readonly Gen<Score> GenScore =
        from bpm in Gen.Int[60, 140]
        from measures in GenMeasure.Array[1, 4]
        select new Score(new Tempo(bpm), TimeSignature.FourFour, Subdivision.Sixteenth, measures);

    [Fact]
    [Trait("Category", "Slow")]
    public void ScoreRoundTripIsExactOnTheGrid()
    {
        GenScore.Sample(score =>
        {
            IScoreWriter writer = new DryWetMidiWriter();
            using var stream = new MemoryStream();
            writer.Write(score, stream);

            stream.Position = 0;
            var notes = MidiFile.Read(stream).GetNotes().OrderBy(n => n.Time).ToList();

            // Expected ticks recomputed independently of the writer's internals:
            // one quarter slot = PPQN MIDI ticks, measure = 4·PPQN, notes emit, rests skip.
            long ticksPerMeasure = 4L * DryWetMidiWriter.TicksPerQuarterNote;
            var expected = new List<(int Midi, long Time, long Length)>();
            for (int m = 0; m < score.Measures.Count; m++)
            {
                long cursor = m * ticksPerMeasure;
                foreach (var element in score.Measures[m].Elements)
                {
                    if (element.Kind == ElementKind.Note)
                    {
                        expected.Add((element.Pitch!.Value.MidiNumber, cursor, DryWetMidiWriter.TicksPerQuarterNote));
                    }

                    cursor += DryWetMidiWriter.TicksPerQuarterNote;
                }
            }

            Assert.Equal(expected.Count, notes.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Midi, (int)notes[i].NoteNumber);
                Assert.Equal(expected[i].Time, notes[i].Time);        // exact — grid positions are integer ticks
                Assert.Equal(expected[i].Length, notes[i].Length);    // exact
            }
        }, iter: 300);
    }
```

> Note: also add `using AudioClaudio.Application.Ports;` to the top of this test file if it is not already imported (needed for `IScoreWriter`).

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~MidiRoundTripPropertyTests.ScoreRoundTripIsExactOnTheGrid"
```

Expected FAILURE: compile error until the generators and test are added; behavior is already satisfied by Task 3's writer, so once it compiles it should go green (see Task 2's note on forcing a behavioral red if you want one).

**Step 3 — Minimal implementation:** none — Task 3's `Write(Score, Stream)` already satisfies this property.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~MidiRoundTripPropertyTests.ScoreRoundTripIsExactOnTheGrid"
```

Expected PASS: 300 random scores round-trip to exactly their grid ticks.

**Step 5 — Commit:**

```bash
but commit step-07-midi-export -m "test(infra): Score MIDI round-trip property" --changes <ids> --status-after
```

---

## Task 5: Determinism (non-negotiable 3)

The same input SHALL serialize to byte-identical MIDI every time, for both write paths.

**Files:**
- Test: `tests/AudioClaudio.Tests/Infrastructure/DryWetMidiWriterTests.cs` (add a test)

**Step 1 — Write the failing test** (add to `DryWetMidiWriterTests`):

```csharp
    [Fact]
    [Trait("Category", "Fast")]
    public void WriteIsDeterministicForScoreAndEvents()
    {
        var rate = new SampleRate(44100);
        var events = new[]
        {
            new NoteEvent(new Pitch(45), new SamplePosition(1000, rate), new SampleDuration(15000, rate), 90),
            new NoteEvent(new Pitch(57), new SamplePosition(30000, rate), new SampleDuration(15000, rate), 90),
        };
        int q = Subdivision.Sixteenth.TicksPerQuarter();   // grid ticks per quarter note
        var score = new Score(
            new Tempo(100),
            TimeSignature.FourFour,
            Subdivision.Sixteenth,
            new[]
            {
                new Measure(new[]
                {
                    ScoreElement.Note(new Pitch(60), velocity: 80, lengthTicks: q),        // quarter
                    ScoreElement.Rest(lengthTicks: q),                                     // quarter rest
                    ScoreElement.Note(new Pitch(62), velocity: 80, lengthTicks: 2 * q),    // half -> full 4/4 bar
                }),
            });

        var writer = new DryWetMidiWriter();

        Assert.Equal(BytesOfEvents(writer, events), BytesOfEvents(writer, events));
        Assert.Equal(BytesOfScore(writer, score), BytesOfScore(writer, score));
    }

    private static byte[] BytesOfEvents(DryWetMidiWriter writer, NoteEvent[] events)
    {
        using var stream = new MemoryStream();
        ((INoteEventWriter)writer).Write(events, new Tempo(120), stream);
        return stream.ToArray();
    }

    private static byte[] BytesOfScore(DryWetMidiWriter writer, Score score)
    {
        using var stream = new MemoryStream();
        ((IScoreWriter)writer).Write(score, stream);
        return stream.ToArray();
    }
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~DryWetMidiWriterTests.WriteIsDeterministicForScoreAndEvents"
```

Expected FAILURE: fails to compile until the test compiles in. If it compiles and immediately passes, that is expected — MIDI carries no timestamps and DryWetMIDI serialization is deterministic; the test *locks in* that guarantee against future regressions (e.g. someone introducing a `DateTime`- or hash-ordered write). To see a real red, temporarily inject `Guid.NewGuid()` bytes — then remove it.

**Step 3 — Minimal implementation:** none expected. If the test is red because output differs run-to-run, apply @superpowers:systematic-debugging: hunt the nondeterminism (unordered dictionary iteration, a wall-clock read) and remove it — the fix is in the code, never the assertion (Section 1 rule 8).

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~DryWetMidiWriterTests.WriteIsDeterministicForScoreAndEvents"
```

Expected PASS: identical bytes on repeat, for both paths.

**Step 5 — Commit, then run the full fast suite and formatter:**

```bash
dotnet format
dotnet test --filter Category=Fast
but commit step-07-midi-export -m "test(infra): MIDI writer determinism" --changes <ids> --status-after
```

---

## Task 6: `MidiFileReader` — read-back for lossless round-trips (R7.2)

R7.2 says re-reading the file yields the same pitches, onsets, and durations — that requires a *reader*, and Steps 8 (`render`/`play`) and 9 (closed-loop quarantine + regression corpus) both load a `.mid` into `[NoteEvent]`. This task ships the concrete reader (CONTRACTS.md §7) and proves the round-trip through it. It is Infrastructure, not a port — consumers construct it directly.

**Files:**
- Create: `src/AudioClaudio.Infrastructure/Midi/MidiFileReader.cs` (defines `MidiFileReader` + `MidiReadResult`)
- Test: `tests/AudioClaudio.Tests/Infrastructure/MidiReaderRoundTripTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System;
using System.IO;
using System.Linq;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

public class MidiReaderRoundTripTests
{
    // Write a raw performance, read it back through MidiFileReader, and demand
    // tempo recovered, pitch/velocity exact, onset/duration within one tick.
    [Fact]
    [Trait("Category", "Fast")]
    public void ReadsBackRawEventsWithinOneTick()
    {
        var rate = new SampleRate(44100);
        var original = new[]
        {
            new NoteEvent(new Pitch(60), new SamplePosition(22050, rate), new SampleDuration(44100, rate), 80),
            new NoteEvent(new Pitch(64), new SamplePosition(88200, rate), new SampleDuration(22050, rate), 90),
        };

        var writer = new DryWetMidiWriter();
        using var stream = new MemoryStream();
        ((INoteEventWriter)writer).Write(original, new Tempo(120), stream);

        stream.Position = 0;
        MidiReadResult result = MidiFileReader.Read(stream, rate);

        Assert.Equal(120.0, result.Tempo.BeatsPerMinute, 3);   // tempo recovered from SetTempo
        Assert.Equal(original.Length, result.Events.Count);

        double samplesPerTick = 60.0 * rate.Hz / (DryWetMidiWriter.TicksPerQuarterNote * 120.0);
        long tolerance = (long)Math.Ceiling(samplesPerTick);
        for (int i = 0; i < original.Length; i++)
        {
            Assert.Equal(original[i].Pitch.MidiNumber, result.Events[i].Pitch.MidiNumber);   // exact
            Assert.Equal(original[i].Velocity, result.Events[i].Velocity);                    // exact
            Assert.True(Math.Abs(result.Events[i].Onset.Samples - original[i].Onset.Samples) <= tolerance,
                $"onset off by more than one tick: {result.Events[i].Onset.Samples} vs {original[i].Onset.Samples}");
            Assert.True(Math.Abs(result.Events[i].Duration.Samples - original[i].Duration.Samples) <= tolerance,
                $"duration off by more than one tick: {result.Events[i].Duration.Samples} vs {original[i].Duration.Samples}");
        }
    }

    // Tempos whose 60_000_000/BPM microsecond value is an integer, so the tempo
    // round-trips exactly and the reader/writer share one BPM (keeps the timing
    // tolerance a clean one tick).
    private static readonly int[] ExactTempos = { 60, 75, 96, 100, 120, 125, 150 };

    private static readonly Gen<(int Bpm, NoteEvent[] Events)> GenPerformance =
        from t in Gen.Int[0, ExactTempos.Length - 1]
        from count in Gen.Int[1, 6]
        from pitches in Gen.Int[33, 96].Array[count]
        from gaps in Gen.Int[0, 20000].Array[count]
        from lengths in Gen.Int[2000, 40000].Array[count]
        from velocities in Gen.Int[1, 127].Array[count]
        select (ExactTempos[t], BuildEvents(pitches, gaps, lengths, velocities));

    private static NoteEvent[] BuildEvents(int[] pitches, int[] gaps, int[] lengths, int[] velocities)
    {
        var rate = new SampleRate(44100);
        var events = new NoteEvent[pitches.Length];
        long cursor = 0;
        for (int i = 0; i < pitches.Length; i++)
        {
            cursor += gaps[i];
            events[i] = new NoteEvent(
                new Pitch(pitches[i]),
                new SamplePosition(cursor, rate),
                new SampleDuration(lengths[i], rate),
                velocities[i]);
            cursor += lengths[i];
        }

        return events;
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void ReaderRoundTripPreservesPitchAndTimingWithinOneTick()
    {
        GenPerformance.Sample(sample =>
        {
            var (bpm, events) = sample;
            var rate = new SampleRate(44100);

            var writer = new DryWetMidiWriter();
            using var stream = new MemoryStream();
            ((INoteEventWriter)writer).Write(events, new Tempo(bpm), stream);

            stream.Position = 0;
            MidiReadResult result = MidiFileReader.Read(stream, rate);

            Assert.Equal((double)bpm, result.Tempo.BeatsPerMinute, 2);
            Assert.Equal(events.Length, result.Events.Count);

            double samplesPerTick = 60.0 * rate.Hz / (DryWetMidiWriter.TicksPerQuarterNote * bpm);
            long tolerance = (long)Math.Ceiling(samplesPerTick);
            for (int i = 0; i < events.Length; i++)
            {
                Assert.Equal(events[i].Pitch.MidiNumber, result.Events[i].Pitch.MidiNumber);
                Assert.Equal(events[i].Velocity, result.Events[i].Velocity);
                Assert.True(Math.Abs(result.Events[i].Onset.Samples - events[i].Onset.Samples) <= tolerance);
                Assert.True(Math.Abs(result.Events[i].Duration.Samples - events[i].Duration.Samples) <= tolerance);
            }
        }, iter: 300);
        // CsCheck prints a `seed:` on any failure — paste it back into Sample(...) to replay exactly.
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~MidiReaderRoundTripTests"
```

Expected FAILURE: compile error — `MidiFileReader` and `MidiReadResult` do not exist yet.

**Step 3 — Minimal implementation:**

```csharp
// src/AudioClaudio.Infrastructure/Midi/MidiFileReader.cs
using AudioClaudio.Domain;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace AudioClaudio.Infrastructure.Midi;

/// <summary>
/// The events recovered from a MIDI file plus the tempo read from its SetTempo event.
/// Sample rate is not stored in MIDI, so the caller supplies one to denominate the
/// recovered <see cref="SamplePosition"/>s.
/// </summary>
public readonly record struct MidiReadResult
{
    public IReadOnlyList<NoteEvent> Events { get; }
    public Tempo Tempo { get; }

    public MidiReadResult(IReadOnlyList<NoteEvent> events, Tempo tempo)
    {
        Events = events;
        Tempo = tempo;
    }
}

/// <summary>
/// Reads standard MIDI files back into the domain's <see cref="NoteEvent"/>s, the
/// read-back half of R7.2. Concrete Infrastructure type (not a port); Steps 8 and 9
/// construct it directly. Inverse of <see cref="DryWetMidiWriter"/>.
/// </summary>
public static class MidiFileReader
{
    public static MidiReadResult Read(Stream source, SampleRate rate)
    {
        var midi = MidiFile.Read(source);
        double bpm = ReadTempoBpm(midi);
        int ppqn = ((TicksPerQuarterNoteTimeDivision)midi.TimeDivision).TicksPerQuarterNote;

        var events = new List<NoteEvent>();
        foreach (var note in midi.GetNotes().OrderBy(n => n.Time))
        {
            long onsetSamples = TicksToSamples(note.Time, ppqn, bpm, rate);
            long durationSamples = TicksToSamples(note.Length, ppqn, bpm, rate);
            events.Add(new NoteEvent(
                new Pitch((int)note.NoteNumber),
                new SamplePosition(onsetSamples, rate),
                new SampleDuration(durationSamples, rate),
                (int)note.Velocity));
        }

        return new MidiReadResult(events, new Tempo(bpm));
    }

    public static MidiReadResult ReadFile(string path, SampleRate rate)
    {
        using var fs = File.OpenRead(path);
        return Read(fs, rate);
    }

    // samples = ticks · 60 · rate.Hz / (PPQN · BPM) — inverse of the writer's map.
    private static long TicksToSamples(long ticks, int ppqn, double bpm, SampleRate rate)
    {
        double samples = (double)ticks * 60.0 * rate.Hz / (ppqn * bpm);
        return (long)Math.Round(samples, MidpointRounding.ToEven);
    }

    private static double ReadTempoBpm(MidiFile midi)
    {
        var setTempo = midi.GetTrackChunks()
            .SelectMany(c => c.Events)
            .OfType<SetTempoEvent>()
            .FirstOrDefault();
        long microsecondsPerQuarter = setTempo?.MicrosecondsPerQuarterNote ?? 500_000L;   // MIDI default = 120 BPM
        return 60_000_000.0 / microsecondsPerQuarter;
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~MidiReaderRoundTripTests"
```

Expected PASS: raw events survive write→read with tempo recovered, pitch/velocity exact, and onset/duration within one tick.

**Step 5 — Commit, then run the full fast suite and formatter:**

```bash
dotnet format
dotnet test --filter Category=Fast
but commit step-07-midi-export -m "feat(infra): MIDI reader for lossless round-trip" --changes <ids> --status-after
```

> The finer commits above roll up to the spec message `feat(infra): MIDI export via DryWetMIDI` (Section 1 rule 5); the reader lands alongside as `feat(infra): MIDI reader for lossless round-trip`. Squash into the single spec message before opening the PR if you prefer one commit per step. Use the @gitbutler skill for any squash.

---

## Verify (step exit criteria)

Restating Section 6 Step 7's *Verify* for this step:

- [ ] **Property (round-trip) — event lists:** write → read → compare over generated `[NoteEvent]` performances; pitches bit-exact, onsets and durations within one MIDI tick (`RawEventRoundTripPreservesPitchAndTimingWithinOneTick`).
- [ ] **Property (round-trip) — scores:** write → read → compare over generated `Score`s; pitches bit-exact, onsets and durations land on exactly their grid ticks (`ScoreRoundTripIsExactOnTheGrid`).
- [ ] **Tick resolution makes grid positions exact (R7.2):** the 480 PPQN choice is exercised by `WritesScoreMeasureToExactTicks` (hand-computed MIDI ticks 0/1440 with lengths 960/480) and recorded in `DECISIONS.md`.
- [ ] **Read-back (R7.2):** `MidiFileReader.Read`/`ReadFile` recover events + tempo; write → read → compare survives with pitch/velocity exact and onset/duration within one tick (`ReadsBackRawEventsWithinOneTick`, `ReaderRoundTripPreservesPitchAndTimingWithinOneTick`).
- [ ] **Determinism (non-negotiable 3):** identical input → byte-identical MIDI for both paths (`WriteIsDeterministicForScoreAndEvents`).

## Definition of Done

- [ ] `dotnet build` succeeds (warnings-as-errors clean).
- [ ] `dotnet format` reports no changes.
- [ ] All new tests green; `dotnet test --filter Category=Fast` still green (the two `Slow` properties run in the full `dotnet test`).
- [ ] Dependency rule intact: `IScoreWriter`/`INoteEventWriter` live in `AudioClaudio.Application.Ports`; `DryWetMidiWriter`, `MidiFileReader`, and the only `Melanchall.DryWetMidi` reference live in `AudioClaudio.Infrastructure.Midi`; `AudioClaudio.Domain` gained no dependency and no clock read.
- [ ] `MidiFileReader`/`MidiReadResult` ship in `AudioClaudio.Infrastructure.Midi` and the reader round-trip is green (`ReadsBackRawEventsWithinOneTick`, `ReaderRoundTripPreservesPitchAndTimingWithinOneTick`) — Steps 8 and 9 can consume `MidiFileReader.Read`/`ReadFile`.
- [ ] Committed via GitButler; the finer commits roll up to `feat(infra): MIDI export via DryWetMIDI` (plus `feat(infra): MIDI reader for lossless round-trip`).
- [ ] Requirement-coverage table fully satisfied (R7.1, R7.2).
- [ ] `DECISIONS.md` updated: DryWetMIDI (MIT, pinned version) and the 480 PPQN rationale.
- [ ] Types consumed match CONTRACTS.md §1/§6 verbatim (`Tempo.BeatsPerMinute`, `TimeSignature.BeatsPerMeasure`/`BeatUnit`, `Score.Measures`→`Measure.Elements`→`ScoreElement.LengthTicks`, `Subdivision.TicksPerQuarter()`); any name adjustments confined to `DryWetMidiWriter`/`MidiFileReader` and the test fixtures.
