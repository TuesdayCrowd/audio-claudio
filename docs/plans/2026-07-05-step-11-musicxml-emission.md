# Step 11 — MusicXML Emission — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Build-spec step:** Section 6 Step 11 (R11.1, R11.2, R11.3)
**Goal:** Hand-roll a deterministic MusicXML 4.0 writer that serializes a `Score` to single-staff notation — clef chosen by pitch range, 4/4, correct note types and rests, one `<measure>` per bar — proven by a byte-identical golden file and a bar-conservation property.
**Architecture:** This is the second Infrastructure adapter over the domain's output (Step 7 was the first). `MusicXmlScoreWriter` lives in `AudioClaudio.Infrastructure.MusicXml` and implements the `IScoreWriter` port declared in `AudioClaudio.Application.Ports` (Step 7 is its definer). It depends only inward (Application + Domain); the CLI composition root is the only place it is constructed (Section 3). The writer reads a `Score` and returns text — it performs no I/O beyond writing bytes to a caller-supplied `Stream`, and reads no clock.
**Tech Stack:** `System.Text.StringBuilder` only (hand-rolled serializer, R11.3 — no NuGet); xUnit + `System.Xml.Linq` (BCL) for assertions; CsCheck for the bar-conservation property.
**Prerequisites:** Step 0 (scaffold, dependency rule, CI), Step 6 (the `Score`/`Measure`/`ScoreElement`/`Subdivision`/`TimeSignature` domain model — canonical shapes in CONTRACTS.md §6), and Step 7 (the *definer* of the `IScoreWriter` port in `AudioClaudio.Application.Ports`) green and committed. Step 1 (`Pitch`) is transitively required. Task 7 additionally extends the `transcribe` CLI command wired in Step 9 (CONTRACTS.md §9). Step 11 *consumes* `IScoreWriter` — it does not redefine it (see Task 1). (Section 1 rule 3: one step at a time.)
**Commit (spec):** `feat(infra): MusicXML writer with bar-conservation property`

---

## Approach

MusicXML is an XML serialization of common-practice notation. A `<score-partwise>` document holds a `<part-list>` and one `<part>`; the part is a list of `<measure>` elements; each measure holds `<note>` elements. The very first measure carries an `<attributes>` block that fixes, once, the three facts the rest of the file assumes: the *divisions* (the integer time unit), the key, the time signature, and the clef. Subsequent measures inherit those and omit `<attributes>`.

The one number that makes the whole file exact is **divisions per quarter note**. MusicXML measures every duration as an integer count of divisions. We take it straight from the domain: `divisions = score.Subdivision.TicksPerQuarter()`, so a `ScoreElement`'s `LengthTicks` maps directly to the MusicXML `<duration>`. For the MVP grid (`Subdivision.Sixteenth`) that is `divisions = 4`: a quarter note is 4 divisions, a sixteenth is 1, and every supported value — including dotted eighths (3) and dotted quarters (6) — lands on an integer. No floating durations ever reach the file, which is exactly the domain's "time is integer samples" instinct carried to the notation edge.

Two mappings do the real work. **Pitch → notation:** a MIDI number `n` splits into a pitch class `n mod 12` (which we render with sharps: C, C♯, D, …, B) and an octave `n/12 − 1` (so MIDI 60 is C4, middle C). **`LengthTicks` → notation:** a `ScoreElement`'s tick length becomes a `<type>` string (`"16th"`, `"eighth"`, `"quarter"`, `"half"`, `"whole"`), an optional `<dot/>`, and a `<duration>` equal to `LengthTicks`. A `ScoreElement` with `Kind == ElementKind.Rest` is a `<note>` with `<rest/>` in place of `<pitch>` — same duration and type machinery, no pitch. A note carrying `TiedToNext` (Step 6's structural bar-split flag) emits a `<tie type="start"/>`/`<tied>` on the starting segment and a matching stop on its continuation.

The clef is chosen by range so a bass line does not float on ledger lines below a treble staff: we average the MIDI numbers of the pitched notes and pick bass (F clef, line 4) when the mean is below middle C (MIDI 60), treble (G clef, line 2) otherwise. The tie at exactly 60 goes treble, and an all-rests score defaults to treble — both fixed tie-breaks, because determinism forbids incidental ones (Section 4, non-negotiable 3).

Determinism is a first-class requirement here, not a nicety. The golden file is committed and compared **byte-for-byte**, so the writer must emit the same bytes on every machine. The single trap is line endings: `Environment.NewLine` is CRLF on Windows and LF elsewhere, which would make the golden pass on the dev Mac and fail in Linux CI. We emit an explicit `"\n"` everywhere and write UTF-8 without a BOM. The bar-conservation property is the notation analogue of the ledger's trial balance: every emitted measure's `<duration>` values must sum to exactly one bar (16 divisions in 4/4), for thousands of randomly generated valid scores.

---

## Domain contract consumed from Step 6 (canonical — CONTRACTS.md §6)

Step 6 owns these types; Step 11 only *consumes* them, using the exact names and shapes fixed in CONTRACTS.md §6. This plan's tests construct them with the shape below.

```csharp
namespace AudioClaudio.Domain;

// 4/4 for the MVP. MusicXML calls these <beats> and <beat-type>.
public readonly record struct TimeSignature(int BeatsPerMeasure, int BeatUnit)
{
    public static TimeSignature FourFour { get; }   // (4, 4)
}

// The grid subdivision; TicksPerQuarter() gives divisions per quarter note.
public enum Subdivision { /* Whole .. Sixteenth (+ dotted) */ }
public static class SubdivisionExtensions
{
    public static int TicksPerQuarter(this Subdivision subdivision);   // = 4 for Sixteenth
}

public readonly record struct Tempo
{
    public double BeatsPerMinute { get; }
    public Tempo(double beatsPerMinute);
}

public enum ElementKind { Note, Rest }

// One notated event: a note (Kind == Note, Pitch set) or a rest (Kind == Rest, Pitch null).
// Duration is an integer count of grid ticks; TiedToNext marks a structural bar-split.
public readonly record struct ScoreElement(
    ElementKind Kind, Pitch? Pitch, int Velocity, int LengthTicks, bool TiedToNext)
{
    public static ScoreElement Note(Pitch pitch, int velocity, int lengthTicks, bool tiedToNext = false);
    public static ScoreElement Rest(int lengthTicks);
}

// One bar: an ordered list of notes/rests summing to the time signature.
public sealed class Measure : IEquatable<Measure>
{
    public IReadOnlyList<ScoreElement> Elements { get; }
    public Measure(IReadOnlyList<ScoreElement> elements);
}

// A quantized score on a grid. Tempo/Subdivision are carried but NOT emitted to
// MusicXML (R11.1 lists staff, clef, 4/4, note types, rests — not tempo).
public sealed class Score : IEquatable<Score>
{
    public Tempo Tempo { get; }
    public TimeSignature TimeSignature { get; }
    public Subdivision Subdivision { get; }
    public IReadOnlyList<Measure> Measures { get; }
    public Score(Tempo tempo, TimeSignature timeSignature, Subdivision subdivision, IReadOnlyList<Measure> measures);
}
```

> **Mapping note:** `<duration>` is `ScoreElement.LengthTicks` directly, because we set `divisions = score.Subdivision.TicksPerQuarter()`. `<type>`/`<dot>` are derived from the tick length relative to divisions (a sixteenth is `divisions/4` ticks): 1→16th, 2→eighth, 3→dotted eighth, 4→quarter, 6→dotted quarter, 8→half, 12→dotted half, 16→whole. `Velocity` is not notated (MusicXML dynamics are out of R11.1's scope).

---

## Requirements coverage

| Requirement | Task(s) | Proven by (test) |
|---|---|---|
| **R11.1** — valid MusicXML 4.0 from a `Score`: single staff, clef by range, 4/4, correct note types & rests, measures barred by the time signature | Task 1 (document scaffold, part-list, one `<measure>` per bar), Task 2 (first-measure `<attributes>`: divisions, key, 4/4 time, clef-by-range), Task 3 (note types, dots, rests, pitch step/alter/octave), Task 6 (barring) | `EmitsMusicXml40PartwiseDocumentHeader`; `EmitsAttributesOnlyInTheFirstMeasure`, `ChoosesBassClefWhenMeanPitchBelowMiddleC`, `ChoosesTrebleClefWhenMeanPitchAtOrAboveMiddleC`; `MapsDurationTicksToTypeDurationAndDot`, `EmitsRestElementForRests`, `MapsMidiNumbersToStepAlterOctave`; `EveryMeasureSumsToTheTimeSignature` |
| **R11.2** — loads cleanly in MuseScore (manual, documented) and is stable: golden file checked in | Task 4 (byte-identical golden + manual MuseScore check recorded in `DECISIONS.md`), Task 5 (determinism / LF newlines) | `EmitsByteIdenticalGoldenForTwinkleFixture`; `OutputIsDeterministicAndUsesLfNewlines`; the `DECISIONS.md` manual-MuseScore-load line (Task 4) |
| **R11.3** — hand-roll the XML (serializer, not a dependency) | Tasks 1–3 (`StringBuilder`, no package reference added) | Whole suite green with zero new NuGet references; `Infrastructure.csproj` gains no `<PackageReference>` |
| **§7 `transcribe` trio** — extend Step 9's `TranscribeCommand.Run` to also emit `score.musicxml`, completing `raw.mid` / `score.mid` / `score.musicxml` (CONTRACTS.md §9) | Task 7 | `TranscribeEmitsScoreMusicXmlAlongsideMidiTrio` |
| **R10.3 completion** — flip the `listen` composition root (Step 10 left `musicXmlWriter: null`) to pass `new MusicXmlScoreWriter()`, so `listen` emits `score.musicxml` on stop | Task 7 | Step 10's `ListenCommandTests.WritesMusicXmlOnlyWhenWriterProvided` (seam) + this wiring |

*Step 11 lists no **Design decision** in Section 6 — no decision gate, no design fork, no new dependency. The only `DECISIONS.md` edit is the R11.2 manual-MuseScore-load record (Task 4), a verification-log line, not a design decision. `README.md` is not touched (Step 12 owns it, R12.1).*

---

### Task 1: `MusicXmlScoreWriter` document scaffold (implements the Step 7 `IScoreWriter` port)

**Files:**
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Infrastructure/MusicXml/MusicXmlScoreWriter.cs`
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/MusicXml/MusicXmlTestSupport.cs`
- Test: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/MusicXml/MusicXmlWriterTests.cs`

> **Consume `IScoreWriter` (do not redefine):** the port is defined by Step 7 in `AudioClaudio.Application.Ports` with the exact signature `void Write(Score score, Stream destination)` (CONTRACTS.md §7/§11). Step 11 makes `MusicXmlScoreWriter` implement that existing interface — it does not recreate the port. (If Step 7 has somehow not landed, add the port *there* first; never redefine it in Infrastructure.) A `MemoryStream` makes the byte assertions trivial.

Use @superpowers:test-driven-development for the red-green loop.

**Step 1 — Write the failing test:**

```csharp
// tests/AudioClaudio.Tests/MusicXml/MusicXmlTestSupport.cs
using System.IO;
using System.Xml;
using System.Xml.Linq;
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.MusicXml;

/// <summary>Parse MusicXML that carries a DOCTYPE without fetching the external DTD.
/// XDocument.Parse defaults to DtdProcessing.Prohibit and would THROW on our DOCTYPE.</summary>
internal static class Xml
{
    internal static XDocument Parse(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader);
    }
}

/// <summary>Deterministic Score fixtures for the MusicXML writer tests.
/// Grid is Subdivision.Sixteenth, so divisions = 4: a quarter is 4 ticks, a whole 16.</summary>
internal static class MusicXmlFixtures
{
    private const int Velocity = 64;

    // One bar, one whole note (16 ticks) of the given pitch — fills a 4/4 bar exactly.
    internal static Score OneNote(Pitch pitch) =>
        new(new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth, new[]
        {
            new Measure(new[] { ScoreElement.Note(pitch, Velocity, 16) }),
        });

    // "Twinkle, twinkle" opening: two full 4/4 bars, treble range, ending on a rest.
    // C4 C4 G4 G4 | A4 A4 G4 (rest); every element a quarter = 4 ticks.
    internal static Score Twinkle() =>
        new(new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth, new[]
        {
            new Measure(new[]
            {
                ScoreElement.Note(new Pitch(60), Velocity, 4),
                ScoreElement.Note(new Pitch(60), Velocity, 4),
                ScoreElement.Note(new Pitch(67), Velocity, 4),
                ScoreElement.Note(new Pitch(67), Velocity, 4),
            }),
            new Measure(new[]
            {
                ScoreElement.Note(new Pitch(69), Velocity, 4),
                ScoreElement.Note(new Pitch(69), Velocity, 4),
                ScoreElement.Note(new Pitch(67), Velocity, 4),
                ScoreElement.Rest(4),
            }),
        });
}
```

```csharp
// tests/AudioClaudio.Tests/MusicXml/MusicXmlWriterTests.cs
using System.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.MusicXml;
using Xunit;

namespace AudioClaudio.Tests.MusicXml;

public class MusicXmlWriterTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void EmitsMusicXml40PartwiseDocumentHeader()
    {
        var score = MusicXmlFixtures.OneNote(new Pitch(60));

        var xml = new MusicXmlScoreWriter().WriteToString(score);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", xml);
        Assert.Contains(
            "<!DOCTYPE score-partwise PUBLIC \"-//Recordare//DTD MusicXML 4.0 Partwise//EN\" " +
            "\"http://www.musicxml.org/dtds/partwise.dtd\">",
            xml);
        Assert.Contains("<score-partwise version=\"4.0\">", xml);

        var doc = Xml.Parse(xml);
        Assert.Equal("P1", (string)doc.Descendants("score-part").Single().Attribute("id")!);
        Assert.Equal("Music", (string)doc.Descendants("score-part").Single().Element("part-name")!);
        Assert.Equal("P1", (string)doc.Descendants("part").Single().Attribute("id")!);
        Assert.Single(doc.Descendants("measure")); // one <measure> per bar
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~MusicXmlWriterTests.EmitsMusicXml40PartwiseDocumentHeader"
```

Expected FAILURE: compile error — `MusicXmlScoreWriter` does not exist yet (`CS0246: The type or namespace name 'MusicXmlScoreWriter' could not be found`). Red as intended.

**Step 3 — Minimal implementation** (`IScoreWriter` already exists from Step 7 in `AudioClaudio.Application.Ports` — Step 11 only implements it):

```csharp
// src/AudioClaudio.Infrastructure/MusicXml/MusicXmlScoreWriter.cs
using System.Text;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;

namespace AudioClaudio.Infrastructure.MusicXml;

/// <summary>
/// Hand-rolled MusicXML 4.0 serializer for a monophonic <see cref="Score"/> (R11.3).
/// Single staff, clef by pitch range, 4/4, deterministic byte-for-byte output.
/// </summary>
public sealed class MusicXmlScoreWriter : IScoreWriter
{
    // Explicit LF only. Environment.NewLine is CRLF on Windows and would break the
    // bit-for-bit determinism non-negotiable (CLAUDE.md Section 4, non-negotiable 3).
    private const string Nl = "\n";

    /// <summary>Serialize a score to UTF-8 (no BOM) MusicXML on the stream.</summary>
    public void Write(Score score, Stream destination)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(WriteToString(score));
        destination.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Serialize a score to a MusicXML 4.0 string (LF newlines).</summary>
    public string WriteToString(Score score)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>").Append(Nl);
        sb.Append("<!DOCTYPE score-partwise PUBLIC \"-//Recordare//DTD MusicXML 4.0 Partwise//EN\" " +
                  "\"http://www.musicxml.org/dtds/partwise.dtd\">").Append(Nl);
        sb.Append("<score-partwise version=\"4.0\">").Append(Nl);
        sb.Append("  <part-list>").Append(Nl);
        sb.Append("    <score-part id=\"P1\">").Append(Nl);
        sb.Append("      <part-name>Music</part-name>").Append(Nl);
        sb.Append("    </score-part>").Append(Nl);
        sb.Append("  </part-list>").Append(Nl);
        sb.Append("  <part id=\"P1\">").Append(Nl);

        for (int i = 0; i < score.Measures.Count; i++)
        {
            sb.Append($"    <measure number=\"{i + 1}\">").Append(Nl);
            sb.Append("    </measure>").Append(Nl);
        }

        sb.Append("  </part>").Append(Nl);
        sb.Append("</score-partwise>").Append(Nl);
        return sb.ToString();
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~MusicXmlWriterTests.EmitsMusicXml40PartwiseDocumentHeader"
```

Expected PASS: one test passing. The document scaffold and one `<measure>` per bar are emitted; notes come in Task 3.

**Step 5 — Commit** (use the @gitbutler skill; read fresh IDs from `but status -fv`):

```bash
but branch new step-11-musicxml-emission && but mark step-11-musicxml-emission
but status -fv    # read the change <ids> for the files created above
but commit step-11-musicxml-emission \
  -m "feat(infra): scaffold MusicXmlScoreWriter" \
  --changes <ids> --status-after
```

---

### Task 2: First-measure `<attributes>` — divisions, key, 4/4 time, clef by range

**Files:**
- Modify: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Infrastructure/MusicXml/MusicXmlScoreWriter.cs`
- Test: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/MusicXml/MusicXmlWriterTests.cs`

**Step 1 — Write the failing test:**

```csharp
// Append to class MusicXmlWriterTests

[Fact]
[Trait("Category", "Fast")]
public void EmitsAttributesOnlyInTheFirstMeasure()
{
    var score = new Score(new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth, new[]
    {
        new Measure(new[] { ScoreElement.Note(new Pitch(60), 64, 16) }),
        new Measure(new[] { ScoreElement.Note(new Pitch(62), 64, 16) }),
    });

    var doc = Xml.Parse(new MusicXmlScoreWriter().WriteToString(score));
    var measures = doc.Descendants("measure").ToArray();

    Assert.Equal(2, measures.Length);
    Assert.NotNull(measures[0].Element("attributes"));
    Assert.Null(measures[1].Element("attributes"));

    var attr = measures[0].Element("attributes")!;
    Assert.Equal("4", (string)attr.Element("divisions")!);
    Assert.Equal("0", (string)attr.Element("key")!.Element("fifths")!);
    Assert.Equal("4", (string)attr.Element("time")!.Element("beats")!);
    Assert.Equal("4", (string)attr.Element("time")!.Element("beat-type")!);
}

[Fact]
[Trait("Category", "Fast")]
public void ChoosesBassClefWhenMeanPitchBelowMiddleC()
{
    // C2 (MIDI 36) and C3 (MIDI 48): mean 42 < 60 -> bass clef.
    var score = new Score(new Tempo(100), TimeSignature.FourFour, Subdivision.Sixteenth, new[]
    {
        new Measure(new[]
        {
            ScoreElement.Note(new Pitch(36), 64, 8),
            ScoreElement.Note(new Pitch(48), 64, 8),
        }),
    });

    var clef = Xml.Parse(new MusicXmlScoreWriter().WriteToString(score)).Descendants("clef").Single();

    Assert.Equal("F", (string)clef.Element("sign")!);
    Assert.Equal("4", (string)clef.Element("line")!);
}

[Fact]
[Trait("Category", "Fast")]
public void ChoosesTrebleClefWhenMeanPitchAtOrAboveMiddleC()
{
    // C5 (MIDI 72) and E5 (MIDI 76): mean 74 >= 60 -> treble clef.
    var score = new Score(new Tempo(100), TimeSignature.FourFour, Subdivision.Sixteenth, new[]
    {
        new Measure(new[]
        {
            ScoreElement.Note(new Pitch(72), 64, 8),
            ScoreElement.Note(new Pitch(76), 64, 8),
        }),
    });

    var clef = Xml.Parse(new MusicXmlScoreWriter().WriteToString(score)).Descendants("clef").Single();

    Assert.Equal("G", (string)clef.Element("sign")!);
    Assert.Equal("2", (string)clef.Element("line")!);
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~MusicXmlWriterTests.EmitsAttributesOnlyInTheFirstMeasure|FullyQualifiedName~MusicXmlWriterTests.ChoosesBassClef|FullyQualifiedName~MusicXmlWriterTests.ChoosesTrebleClef"
```

Expected FAILURE: measures are still emitted empty, so `measures[0].Element("attributes")` is `null` and there is no `<clef>` — the asserts fail (`Assert.NotNull` / `Single` throws). Red.

**Step 3 — Minimal implementation** (replace `WriteToString`'s measure loop, and add the clef helper + `Clef` type — full updated file):

```csharp
// src/AudioClaudio.Infrastructure/MusicXml/MusicXmlScoreWriter.cs
using System.Text;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;

namespace AudioClaudio.Infrastructure.MusicXml;

public sealed class MusicXmlScoreWriter : IScoreWriter
{
    private const int MiddleC = 60;            // MIDI 60; mean below this notates in bass clef
    private const string Nl = "\n";

    public void Write(Score score, Stream destination)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(WriteToString(score));
        destination.Write(bytes, 0, bytes.Length);
    }

    public string WriteToString(Score score)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>").Append(Nl);
        sb.Append("<!DOCTYPE score-partwise PUBLIC \"-//Recordare//DTD MusicXML 4.0 Partwise//EN\" " +
                  "\"http://www.musicxml.org/dtds/partwise.dtd\">").Append(Nl);
        sb.Append("<score-partwise version=\"4.0\">").Append(Nl);
        sb.Append("  <part-list>").Append(Nl);
        sb.Append("    <score-part id=\"P1\">").Append(Nl);
        sb.Append("      <part-name>Music</part-name>").Append(Nl);
        sb.Append("    </score-part>").Append(Nl);
        sb.Append("  </part-list>").Append(Nl);
        sb.Append("  <part id=\"P1\">").Append(Nl);

        var clef = ChooseClef(score);
        int divisions = score.Subdivision.TicksPerQuarter();   // MusicXML divisions per quarter
        for (int i = 0; i < score.Measures.Count; i++)
        {
            AppendMeasure(sb, score, score.Measures[i], measureNumber: i + 1, isFirst: i == 0, clef, divisions);
        }

        sb.Append("  </part>").Append(Nl);
        sb.Append("</score-partwise>").Append(Nl);
        return sb.ToString();
    }

    private static void AppendMeasure(StringBuilder sb, Score score, Measure measure,
                                      int measureNumber, bool isFirst, Clef clef, int divisions)
    {
        sb.Append($"    <measure number=\"{measureNumber}\">").Append(Nl);
        if (isFirst)
        {
            sb.Append("      <attributes>").Append(Nl);
            sb.Append($"        <divisions>{divisions}</divisions>").Append(Nl);
            sb.Append("        <key>").Append(Nl);
            sb.Append("          <fifths>0</fifths>").Append(Nl);
            sb.Append("        </key>").Append(Nl);
            sb.Append("        <time>").Append(Nl);
            sb.Append($"          <beats>{score.TimeSignature.BeatsPerMeasure}</beats>").Append(Nl);
            sb.Append($"          <beat-type>{score.TimeSignature.BeatUnit}</beat-type>").Append(Nl);
            sb.Append("        </time>").Append(Nl);
            sb.Append("        <clef>").Append(Nl);
            sb.Append($"          <sign>{clef.Sign}</sign>").Append(Nl);
            sb.Append($"          <line>{clef.Line}</line>").Append(Nl);
            sb.Append("        </clef>").Append(Nl);
            sb.Append("      </attributes>").Append(Nl);
        }
        // <note> emission arrives in Task 3.
        sb.Append("    </measure>").Append(Nl);
    }

    // Range-based clef with fixed tie-breaks: mean == MiddleC -> treble; all-rests -> treble.
    private static Clef ChooseClef(Score score)
    {
        long sum = 0;
        int count = 0;
        foreach (var m in score.Measures)
            foreach (var e in m.Elements)
                if (e.Pitch is Pitch p) { sum += p.MidiNumber; count++; }

        if (count == 0) return Clef.Treble;
        double mean = (double)sum / count;
        return mean < MiddleC ? Clef.Bass : Clef.Treble;
    }

    private readonly record struct Clef(string Sign, int Line)
    {
        public static readonly Clef Treble = new("G", 2);
        public static readonly Clef Bass = new("F", 4);
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~MusicXmlWriterTests"
```

Expected PASS: the header test plus the three new attribute/clef tests are green (notes are still absent, which none of these tests assert on).

**Step 5 — Commit** (fresh IDs from `but status -fv`; @gitbutler skill):

```bash
but commit step-11-musicxml-emission \
  -m "feat(infra): first-measure attributes and range-based clef" \
  --changes <ids> --status-after
```

---

### Task 3: Note & rest emission — pitch (step/alter/octave), duration, type, dot

**Files:**
- Modify: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Infrastructure/MusicXml/MusicXmlScoreWriter.cs`
- Test: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/MusicXml/MusicXmlWriterTests.cs`

**Step 1 — Write the failing test:**

```csharp
// Append to class MusicXmlWriterTests

[Theory]
[Trait("Category", "Fast")]
[InlineData(1,  "16th",    false)]
[InlineData(2,  "eighth",  false)]
[InlineData(3,  "eighth",  true)]
[InlineData(4,  "quarter", false)]
[InlineData(6,  "quarter", true)]
[InlineData(8,  "half",    false)]
[InlineData(12, "half",    true)]
[InlineData(16, "whole",   false)]
public void MapsDurationTicksToTypeDurationAndDot(int lengthTicks, string type, bool dotted)
{
    // A serializer, not a validator: a single element need not fill the bar.
    // Grid is Subdivision.Sixteenth -> divisions = 4, so <duration> == LengthTicks.
    var score = new Score(new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth, new[]
    {
        new Measure(new[] { ScoreElement.Note(new Pitch(60), 64, lengthTicks) }),
    });

    var note = Xml.Parse(new MusicXmlScoreWriter().WriteToString(score)).Descendants("note").Single();

    Assert.Equal(type, (string)note.Element("type")!);
    Assert.Equal(lengthTicks.ToString(), (string)note.Element("duration")!);
    Assert.Equal(dotted, note.Element("dot") is not null);
}

[Fact]
[Trait("Category", "Fast")]
public void EmitsRestElementForRests()
{
    var score = new Score(new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth, new[]
    {
        new Measure(new[] { ScoreElement.Rest(16) }),
    });

    var note = Xml.Parse(new MusicXmlScoreWriter().WriteToString(score)).Descendants("note").Single();

    Assert.NotNull(note.Element("rest"));
    Assert.Null(note.Element("pitch"));
    Assert.Equal("16", (string)note.Element("duration")!);
    Assert.Equal("whole", (string)note.Element("type")!);
}

[Theory]
[Trait("Category", "Fast")]
[InlineData(60, "C", null, 4)] // middle C
[InlineData(61, "C", 1,    4)] // C#4
[InlineData(70, "A", 1,    4)] // A#4 / Bb4
[InlineData(21, "A", null, 0)] // A0, lowest key
[InlineData(108, "C", null, 8)] // C8, highest key
public void MapsMidiNumbersToStepAlterOctave(int midi, string step, int? alter, int octave)
{
    var score = MusicXmlFixtures.OneNote(new Pitch(midi));

    var pitch = Xml.Parse(new MusicXmlScoreWriter().WriteToString(score)).Descendants("pitch").Single();

    Assert.Equal(step, (string)pitch.Element("step")!);
    Assert.Equal(octave.ToString(), (string)pitch.Element("octave")!);
    if (alter is null)
        Assert.Null(pitch.Element("alter"));
    else
        Assert.Equal(alter.Value.ToString(), (string)pitch.Element("alter")!);
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~MusicXmlWriterTests.MapsDurationTicks|FullyQualifiedName~MusicXmlWriterTests.EmitsRestElement|FullyQualifiedName~MusicXmlWriterTests.MapsMidiNumbers"
```

Expected FAILURE: measures still emit no `<note>` elements, so `Descendants("note").Single()` throws `InvalidOperationException` (sequence contains no elements). Red.

**Step 3 — Minimal implementation** (add `AppendElement`, `PitchToXml`, `TypeAndDot`, thread the running tie state, and call `AppendElement` from `AppendMeasure` — full updated file):

```csharp
// src/AudioClaudio.Infrastructure/MusicXml/MusicXmlScoreWriter.cs
using System;
using System.Text;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;

namespace AudioClaudio.Infrastructure.MusicXml;

public sealed class MusicXmlScoreWriter : IScoreWriter
{
    private const int MiddleC = 60;
    private const string Nl = "\n";

    public void Write(Score score, Stream destination)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(WriteToString(score));
        destination.Write(bytes, 0, bytes.Length);
    }

    public string WriteToString(Score score)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>").Append(Nl);
        sb.Append("<!DOCTYPE score-partwise PUBLIC \"-//Recordare//DTD MusicXML 4.0 Partwise//EN\" " +
                  "\"http://www.musicxml.org/dtds/partwise.dtd\">").Append(Nl);
        sb.Append("<score-partwise version=\"4.0\">").Append(Nl);
        sb.Append("  <part-list>").Append(Nl);
        sb.Append("    <score-part id=\"P1\">").Append(Nl);
        sb.Append("      <part-name>Music</part-name>").Append(Nl);
        sb.Append("    </score-part>").Append(Nl);
        sb.Append("  </part-list>").Append(Nl);
        sb.Append("  <part id=\"P1\">").Append(Nl);

        var clef = ChooseClef(score);
        int divisions = score.Subdivision.TicksPerQuarter();
        bool tiedFromPrevious = false;
        for (int i = 0; i < score.Measures.Count; i++)
        {
            AppendMeasure(sb, score, score.Measures[i], measureNumber: i + 1, isFirst: i == 0,
                          clef, divisions, ref tiedFromPrevious);
        }

        sb.Append("  </part>").Append(Nl);
        sb.Append("</score-partwise>").Append(Nl);
        return sb.ToString();
    }

    private static void AppendMeasure(StringBuilder sb, Score score, Measure measure,
                                      int measureNumber, bool isFirst, Clef clef, int divisions,
                                      ref bool tiedFromPrevious)
    {
        sb.Append($"    <measure number=\"{measureNumber}\">").Append(Nl);
        if (isFirst)
        {
            sb.Append("      <attributes>").Append(Nl);
            sb.Append($"        <divisions>{divisions}</divisions>").Append(Nl);
            sb.Append("        <key>").Append(Nl);
            sb.Append("          <fifths>0</fifths>").Append(Nl);
            sb.Append("        </key>").Append(Nl);
            sb.Append("        <time>").Append(Nl);
            sb.Append($"          <beats>{score.TimeSignature.BeatsPerMeasure}</beats>").Append(Nl);
            sb.Append($"          <beat-type>{score.TimeSignature.BeatUnit}</beat-type>").Append(Nl);
            sb.Append("        </time>").Append(Nl);
            sb.Append("        <clef>").Append(Nl);
            sb.Append($"          <sign>{clef.Sign}</sign>").Append(Nl);
            sb.Append($"          <line>{clef.Line}</line>").Append(Nl);
            sb.Append("        </clef>").Append(Nl);
            sb.Append("      </attributes>").Append(Nl);
        }
        foreach (var element in measure.Elements)
        {
            AppendElement(sb, element, divisions, tiedFromPrevious);
            tiedFromPrevious = element.TiedToNext;
        }
        sb.Append("    </measure>").Append(Nl);
    }

    // MusicXML note-child order: (pitch|rest), duration, tie*, type, dot*, notations.
    private static void AppendElement(StringBuilder sb, ScoreElement element, int divisions, bool tiedFromPrevious)
    {
        var (type, dotted) = TypeAndDot(element.LengthTicks, divisions);
        sb.Append("      <note>").Append(Nl);
        if (element.Kind == ElementKind.Note && element.Pitch is Pitch pitch)
        {
            var (step, alter, octave) = PitchToXml(pitch);
            sb.Append("        <pitch>").Append(Nl);
            sb.Append($"          <step>{step}</step>").Append(Nl);
            if (alter != 0)
                sb.Append($"          <alter>{alter}</alter>").Append(Nl);
            sb.Append($"          <octave>{octave}</octave>").Append(Nl);
            sb.Append("        </pitch>").Append(Nl);
        }
        else
        {
            sb.Append("        <rest/>").Append(Nl);
        }
        sb.Append($"        <duration>{element.LengthTicks}</duration>").Append(Nl);
        // Structural bar-split ties (Step 6 TiedToNext): stop the incoming tie before starting the outgoing one.
        if (tiedFromPrevious)
            sb.Append("        <tie type=\"stop\"/>").Append(Nl);
        if (element.TiedToNext)
            sb.Append("        <tie type=\"start\"/>").Append(Nl);
        sb.Append($"        <type>{type}</type>").Append(Nl);
        if (dotted)
            sb.Append("        <dot/>").Append(Nl);
        if (tiedFromPrevious || element.TiedToNext)
        {
            sb.Append("        <notations>").Append(Nl);
            if (tiedFromPrevious)
                sb.Append("          <tied type=\"stop\"/>").Append(Nl);
            if (element.TiedToNext)
                sb.Append("          <tied type=\"start\"/>").Append(Nl);
            sb.Append("        </notations>").Append(Nl);
        }
        sb.Append("      </note>").Append(Nl);
    }

    // MIDI number -> (step, chromatic alter, octave). Sharps only; octave n/12 - 1 => MIDI 60 is C4.
    private static (string Step, int Alter, int Octave) PitchToXml(Pitch pitch)
    {
        int n = pitch.MidiNumber;
        int pc = ((n % 12) + 12) % 12;
        int octave = n / 12 - 1;
        return pc switch
        {
            0  => ("C", 0, octave),
            1  => ("C", 1, octave),
            2  => ("D", 0, octave),
            3  => ("D", 1, octave),
            4  => ("E", 0, octave),
            5  => ("F", 0, octave),
            6  => ("F", 1, octave),
            7  => ("G", 0, octave),
            8  => ("G", 1, octave),
            9  => ("A", 0, octave),
            10 => ("A", 1, octave),
            _  => ("B", 0, octave),
        };
    }

    // LengthTicks -> (<type>, dotted?). <duration> is LengthTicks itself, since
    // divisions = TicksPerQuarter. Expressed in sixteenth-equivalents so the table is
    // subdivision-independent: a sixteenth is divisions/4 ticks.
    private static (string Type, bool Dotted) TypeAndDot(int lengthTicks, int divisions)
    {
        int sixteenths = lengthTicks * 4 / divisions;
        return sixteenths switch
        {
            1  => ("16th",    false),
            2  => ("eighth",  false),
            3  => ("eighth",  true),
            4  => ("quarter", false),
            6  => ("quarter", true),
            8  => ("half",    false),
            12 => ("half",    true),
            16 => ("whole",   false),
            _  => throw new ArgumentOutOfRangeException(nameof(lengthTicks), lengthTicks,
                      $"No standard note type for {lengthTicks} ticks at {divisions} divisions/quarter"),
        };
    }

    private static Clef ChooseClef(Score score)
    {
        long sum = 0;
        int count = 0;
        foreach (var m in score.Measures)
            foreach (var e in m.Elements)
                if (e.Pitch is Pitch p) { sum += p.MidiNumber; count++; }

        if (count == 0) return Clef.Treble;
        double mean = (double)sum / count;
        return mean < MiddleC ? Clef.Bass : Clef.Treble;
    }

    private readonly record struct Clef(string Sign, int Line)
    {
        public static readonly Clef Treble = new("G", 2);
        public static readonly Clef Bass = new("F", 4);
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~MusicXmlWriterTests"
```

Expected PASS: all unit tests green — note types, dots, rest, and pitch step/alter/octave mappings all serialize correctly. The writer is now feature-complete.

**Step 5 — Commit** (fresh IDs from `but status -fv`; @gitbutler skill):

```bash
but commit step-11-musicxml-emission \
  -m "feat(infra): emit notes, rests, pitches, and dotted durations" \
  --changes <ids> --status-after
```

---

### Task 4: Byte-identical golden file (+ DECISIONS.md MuseScore-load record) — R11.2

**Files:**
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/fixtures/golden/musicxml/twinkle.musicxml`
- Modify: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/.gitattributes` *(add an LF rule; create if absent)*
- Modify: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/DECISIONS.md` *(append the R11.2 manual-MuseScore-load record — exact line in Step 3)*
- Test: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/MusicXml/MusicXmlGoldenTests.cs`

> **Reuse the shared fixture locator:** the byte-comparison uses `RepoPaths` — the single repo-root/fixture locator in `AudioClaudio.Tests.TestSupport` introduced in Step 0 (CONTRACTS.md §0). Do **not** add another `TestPaths`/`Fixtures` variant. If its fixture-path helper is named differently than `RepoPaths.Fixture(...)`, adjust only this one call.

**Step 1 — Write the failing test** (reuses the shared `RepoPaths` locator from `AudioClaudio.Tests.TestSupport`; the `MusicXmlFixtures` helper is in this test's own `AudioClaudio.Tests.MusicXml` namespace):

```csharp
// tests/AudioClaudio.Tests/MusicXml/MusicXmlGoldenTests.cs
using System.IO;
using AudioClaudio.Infrastructure.MusicXml;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.MusicXml;

public class MusicXmlGoldenTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void EmitsByteIdenticalGoldenForTwinkleFixture()
    {
        var score = MusicXmlFixtures.Twinkle();

        using var ms = new MemoryStream();
        new MusicXmlScoreWriter().Write(score, ms);
        var actual = ms.ToArray();

        var expected = File.ReadAllBytes(RepoPaths.Fixture("golden", "musicxml", "twinkle.musicxml"));
        Assert.Equal(expected, actual);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~MusicXmlGoldenTests"
```

Expected FAILURE: the golden file does not exist yet, so `File.ReadAllBytes` throws `FileNotFoundException`. Red. (This proves the test actually reads the committed file.)

**Step 3 — Create the golden fixture** (hand-author it from the writer's known output, then review — golden files are reviewed, never blindly regenerated, Section 5). First pin line endings so the bytes are stable on every platform and in CI:

```bash
# .gitattributes  (append; create the file if it does not exist)
# MusicXML goldens are compared byte-for-byte: force LF so no platform normalizes to CRLF.
*.musicxml text eol=lf
```

Then write `fixtures/golden/musicxml/twinkle.musicxml` with exactly these bytes (LF newlines, UTF-8, no BOM, trailing newline). This is the reference output for `MusicXmlFixtures.Twinkle()` — inspect it against the writer's logic before committing:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE score-partwise PUBLIC "-//Recordare//DTD MusicXML 4.0 Partwise//EN" "http://www.musicxml.org/dtds/partwise.dtd">
<score-partwise version="4.0">
  <part-list>
    <score-part id="P1">
      <part-name>Music</part-name>
    </score-part>
  </part-list>
  <part id="P1">
    <measure number="1">
      <attributes>
        <divisions>4</divisions>
        <key>
          <fifths>0</fifths>
        </key>
        <time>
          <beats>4</beats>
          <beat-type>4</beat-type>
        </time>
        <clef>
          <sign>G</sign>
          <line>2</line>
        </clef>
      </attributes>
      <note>
        <pitch>
          <step>C</step>
          <octave>4</octave>
        </pitch>
        <duration>4</duration>
        <type>quarter</type>
      </note>
      <note>
        <pitch>
          <step>C</step>
          <octave>4</octave>
        </pitch>
        <duration>4</duration>
        <type>quarter</type>
      </note>
      <note>
        <pitch>
          <step>G</step>
          <octave>4</octave>
        </pitch>
        <duration>4</duration>
        <type>quarter</type>
      </note>
      <note>
        <pitch>
          <step>G</step>
          <octave>4</octave>
        </pitch>
        <duration>4</duration>
        <type>quarter</type>
      </note>
    </measure>
    <measure number="2">
      <note>
        <pitch>
          <step>A</step>
          <octave>4</octave>
        </pitch>
        <duration>4</duration>
        <type>quarter</type>
      </note>
      <note>
        <pitch>
          <step>A</step>
          <octave>4</octave>
        </pitch>
        <duration>4</duration>
        <type>quarter</type>
      </note>
      <note>
        <pitch>
          <step>G</step>
          <octave>4</octave>
        </pitch>
        <duration>4</duration>
        <type>quarter</type>
      </note>
      <note>
        <rest/>
        <duration>4</duration>
        <type>quarter</type>
      </note>
    </measure>
  </part>
</score-partwise>
```

**Record the manual MuseScore check in `DECISIONS.md` (R11.2):** open the committed `twinkle.musicxml` in MuseScore and confirm it loads without error and renders two 4/4 bars in treble clef ending on a quarter rest. Then **append one line to `DECISIONS.md`** (created in Step 0; the designated log for this kind of note — *not* the README, which Step 12 owns per R12.1 and §1 rule 3) recording the check with the exact MuseScore version and the date it was run, e.g.:

```markdown
- R11.2 manual check: `fixtures/golden/musicxml/twinkle.musicxml` loads and renders correctly in MuseScore 4.4 (checked 2026-07-06).
```

Substitute the MuseScore version you actually used and the date you ran it. This appended line is the auditable "loads cleanly in MuseScore" deliverable; the golden byte-comparison carries stability thereafter. Step 11 does **not** touch `README.md`.

> **Regeneration seam (future, deliberate — never automatic):** if the writer's format ever changes intentionally, regenerate this file by running the golden test once with a temporary `File.WriteAllBytes(RepoPaths.Fixture("golden","musicxml","twinkle.musicxml"), actual);` line, then review the diff and re-load in MuseScore before committing. Do not wire this into CI.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~MusicXmlGoldenTests"
```

Expected PASS: writer output equals the committed golden bytes exactly.

**Step 5 — Commit** (fresh IDs from `but status -fv`; @gitbutler skill):

```bash
but commit step-11-musicxml-emission \
  -m "test(infra): golden MusicXML for twinkle + DECISIONS MuseScore-load note" \
  --changes <ids> --status-after
```

---

### Task 5: Determinism & LF newlines — R11.2 stability / non-negotiable 3

**Files:**
- Test: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/MusicXml/MusicXmlWriterTests.cs`

**Step 1 — Write the failing test:**

```csharp
// Append to class MusicXmlWriterTests

[Fact]
[Trait("Category", "Fast")]
public void OutputIsDeterministicAndUsesLfNewlines()
{
    var writer = new MusicXmlScoreWriter();
    var score = MusicXmlFixtures.Twinkle();

    var first = writer.WriteToString(score);
    var second = writer.WriteToString(score);

    Assert.Equal(first, second);         // same input -> identical output
    Assert.DoesNotContain("\r\n", first); // never CRLF (would break the golden in CI)
    Assert.DoesNotContain("\r", first);   // no stray carriage returns at all
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~MusicXmlWriterTests.OutputIsDeterministicAndUsesLfNewlines"
```

Expected result: with the current implementation this test PASSES immediately, because the writer already uses the `Nl = "\n"` constant. That is the point — this is a **guard/regression test** locking in non-negotiable 3. Confirm it is genuinely enforced by temporarily changing `private const string Nl = "\n";` to `Environment.NewLine` and re-running: on a CRLF platform `Assert.DoesNotContain("\r\n", first)` fails (red), proving the test bites. Restore `"\n"` immediately.

**Step 3 — Minimal implementation:** none required — the `Nl = "\n"` constant introduced in Task 1 already satisfies this. (If the temporary `Environment.NewLine` change above was made to prove the guard, revert it now so the constant reads `private const string Nl = "\n";`.)

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~MusicXmlWriterTests"
```

Expected PASS: all `MusicXmlWriterTests` green, including the determinism guard.

**Step 5 — Commit** (fresh IDs from `but status -fv`; @gitbutler skill):

```bash
but commit step-11-musicxml-emission \
  -m "test(infra): lock LF newlines and output determinism" \
  --changes <ids> --status-after
```

---

### Task 6: Bar-conservation property (CsCheck) — the bar-level trial balance

**Files:**
- Test: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/MusicXml/MusicXmlBarConservationTests.cs`

This is the headline property for Step 11: for any valid score whose measures each fill a 4/4 bar, every emitted `<measure>` must have `<duration>` values summing to exactly one bar (16 divisions). The generator builds measures that sum to 16 sixteenths by repeatedly choosing a note value that still fits; the assertion re-parses the emitted XML and sums each measure. Use @superpowers:systematic-debugging if the property ever fails — CsCheck shrinks to a minimal failing score and prints a seed to reproduce.

**Step 1 — Write the failing test:**

```csharp
// tests/AudioClaudio.Tests/MusicXml/MusicXmlBarConservationTests.cs
using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.MusicXml;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.MusicXml;

public class MusicXmlBarConservationTests
{
    private const int Velocity = 64;
    private const int BarTicks = 16; // 4 beats * 4 sixteenth-ticks per quarter, a full 4/4 bar at Subdivision.Sixteenth

    // Standard note-value lengths in ticks (divisions = 4): 16th..whole incl. dotted.
    private static readonly int[] StandardTicks = { 1, 2, 3, 4, 6, 8, 12, 16 };

    // A pitch in the MVP detection range (MIDI 33..96), or null for a rest.
    private static readonly Gen<Pitch?> GenPitchOrRest =
        Gen.Frequency(
            (1, Gen.Const((Pitch?)null)),
            (4, Gen.Int[33, 96].Select(n => (Pitch?)new Pitch(n))));

    // Elements whose tick-durations sum to exactly `remaining`. Base list is never
    // mutated (Enumerable.Prepend + ToList allocate fresh), so sharing Gen.Const is safe.
    private static Gen<List<ScoreElement>> GenElements(int remaining)
    {
        if (remaining == 0)
            return Gen.Const(new List<ScoreElement>());

        var fitting = StandardTicks.Where(t => t <= remaining).ToArray();
        return
            from t in Gen.OneOfConst(fitting)
            from p in GenPitchOrRest
            from rest in GenElements(remaining - t)
            select rest.Prepend(
                p is Pitch pitch ? ScoreElement.Note(pitch, Velocity, t) : ScoreElement.Rest(t)).ToList();
    }

    private static readonly Gen<Measure> GenMeasure =
        GenElements(BarTicks).Select(els => new Measure(els));

    private static readonly Gen<Score> GenScore =
        from measureCount in Gen.Int[1, 4]
        from tempo in Gen.Int[60, 140]
        from measures in GenMeasure.List[measureCount]
        select new Score(new Tempo(tempo), TimeSignature.FourFour, Subdivision.Sixteenth, measures);

    [Fact]
    [Trait("Category", "Slow")]
    public void EveryMeasureSumsToTheTimeSignature()
    {
        GenScore.Sample(score =>
        {
            var xml = new MusicXmlScoreWriter().WriteToString(score);
            var doc = Xml.Parse(xml);

            foreach (var measure in doc.Descendants("measure"))
            {
                var sum = measure.Elements("note").Sum(note => (int)note.Element("duration")!);
                Assert.Equal(BarTicks, sum);
            }
        }, iter: 1000);
    }
}
```

> **Seed note (Section 5 — fix seeds for reproducibility):** this property is universally true, so any exploration seed is acceptable; CsCheck picks one per run and, on failure, prints `Set Environment variable CsCheck_Seed to "…" to reproduce.` To pin a failing case, set that env var (or pass `seed: "…"` to `Sample`) — do not fabricate a seed string, as CsCheck parses it. Cross-run byte determinism of the *writer itself* is separately pinned by the golden (Task 4) and Task 5.

**Step 2 — Run to verify it fails:**

First confirm the property is genuinely load-bearing. Temporarily break the writer — e.g. in `AppendElement` change the duration emission `<duration>{element.LengthTicks}</duration>` to emit `element.LengthTicks - 1` — then run:

```bash
dotnet test --filter "FullyQualifiedName~MusicXmlBarConservationTests"
```

Expected FAILURE: CsCheck finds a score whose measure now sums to less than 16; `Assert.Equal(16, sum)` fails and CsCheck reports a shrunk minimal score plus a reproduction seed. Revert the sabotage before Step 4. (This proves the property detects a mis-emitted duration rather than passing vacuously.)

**Step 3 — Minimal implementation:** none — the writer from Task 3 already emits correct per-value durations. The property holds against the real implementation; the deliberate sabotage above only demonstrated its teeth.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~MusicXmlBarConservationTests"
dotnet test --filter "Category=Fast"   # confirm the fast suite is still green and skips this Slow property
```

Expected PASS: the bar-conservation property holds across 1000 generated scores; the `Category=Fast` run excludes it (it is tagged `Slow`).

**Step 5 — Commit** (fresh IDs from `but status -fv`; @gitbutler skill). This commit carries the spec headline message for the writer; the CLI wiring in Task 7 then follows as a separate `feat(cli):` commit:

```bash
dotnet format
but status -fv    # read the change <ids>
but commit step-11-musicxml-emission \
  -m "feat(infra): MusicXML writer with bar-conservation property" \
  --changes <ids> --status-after
```

---

### Task 7: Complete the CLI's MusicXML wiring — `transcribe` trio + `listen` output

**Files:**
- Modify: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Cli/Commands/TranscribeCommand.cs` *(Step 9's `TranscribeCommand.Run` — add the `score.musicxml` write next to the existing `raw.mid` / `score.mid` writes)*
- Modify: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Cli/Program.cs` *(the `listen` case — pass `new MusicXmlScoreWriter()` where Step 10 wired `musicXmlWriter: null`)*
- Test: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/Cli/TranscribeMusicXmlEmissionTests.cs`

This task completes MusicXML output for **both** file-mode and live-mode, closing the §7 trio and R10.3:
- **`transcribe`** — Step 9 factored the command into `AudioClaudio.Cli.Commands.TranscribeCommand.Run(string wavPath, double tempoBpm, string outDir)` (emitting `raw.mid` + `score.mid`). Add the `score.musicxml` write there, completing `claudio transcribe <in.wav> --tempo … → raw.mid, score.mid, score.musicxml` (CONTRACTS.md §9).
- **`listen`** — Step 10 built `ListenCommand` with an **optional injected `IScoreWriter musicXmlWriter`** that the composition root left `null`, and proved the seam with a spy writer (`WritesMusicXmlOnlyWhenWriterProvided`). Now that `MusicXmlScoreWriter` exists, flip the `listen` case in `Program.cs` to construct `ListenCommand` with `musicXmlWriter: new MusicXmlScoreWriter()`, so on stop `listen` emits `score.musicxml` alongside the MIDI trio (R10.3) — **zero change to `ListenCommand`**, its behavior is already covered by Step 10's seam test.

> **Reconcile with Step 9:** the change to `TranscribeCommand.Run` is one `using` + one `File.Create`/`Write` call beside the existing MIDI writes; drive the test through `TranscribeCommand.Run` directly (as Step 9's `TranscribeCommandTests` does — the CLI uses top-level statements, so there is no callable `Program.Main`).

Use @superpowers:test-driven-development for the red-green loop.

**Step 1 — Write the failing test:**

```csharp
// tests/AudioClaudio.Tests/Cli/TranscribeMusicXmlEmissionTests.cs
using System.IO;
using System.Xml.Linq;
using AudioClaudio.Cli.Commands;     // TranscribeCommand (Step 9)
using AudioClaudio.Domain;
using AudioClaudio.Tests.MusicXml;   // Xml.Parse
using AudioClaudio.Tests.Signals;    // SignalGenerator, WavWriter
using Xunit;

namespace AudioClaudio.Tests.Cli;

public class TranscribeMusicXmlEmissionTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void TranscribeEmitsScoreMusicXmlAlongsideMidiTrio()
    {
        var rate = new SampleRate(44100);
        // One second of A4 through the signal generator, written as a mono WAV input.
        var pcm = SignalGenerator.Sine(new Pitch(69).Frequency(), rate.Hz, rate);
        var outDir = Directory.CreateTempSubdirectory().FullName;
        var wav = Path.Combine(outDir, "in.wav");
        WavWriter.WriteMonoFile(wav, pcm, rate);

        // Drive Step 9's factored command directly (top-level Program has no callable Main).
        TranscribeCommand.Run(wav, tempoBpm: 120, outDir: outDir);

        Assert.True(File.Exists(Path.Combine(outDir, "raw.mid")),   "raw.mid should still be written");
        Assert.True(File.Exists(Path.Combine(outDir, "score.mid")), "score.mid should still be written");

        var musicXmlPath = Path.Combine(outDir, "score.musicxml");
        Assert.True(File.Exists(musicXmlPath), "score.musicxml completes the §7 trio");
        var doc = Xml.Parse(File.ReadAllText(musicXmlPath));
        Assert.Equal("4.0", (string)doc.Root!.Attribute("version")!);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~TranscribeMusicXmlEmissionTests"
```

Expected FAILURE: the `transcribe` command writes only `raw.mid` and `score.mid` (Step 9), so `File.Exists(score.musicxml)` is false and the assertion fails. Red.

**Step 3 — Minimal implementation.** Two edits (`outDir` / `result` are the names Step 9 introduced in `TranscribeCommand.Run` — adjust to match):

```csharp
// src/AudioClaudio.Cli/Commands/TranscribeCommand.cs
using AudioClaudio.Infrastructure.MusicXml;   // add to the usings

// ... inside TranscribeCommand.Run, right after score.mid is written:
using (var musicXml = File.Create(Path.Combine(outDir, "score.musicxml")))
    new MusicXmlScoreWriter().Write(result.Score, musicXml);
```

```csharp
// src/AudioClaudio.Cli/Program.cs — the `listen` case (Step 10 wired musicXmlWriter: null)
using AudioClaudio.Infrastructure.MusicXml;

// was: new ListenCommand(session, midiWriter, midiWriter, Console.WriteLine, musicXmlWriter: null)
new ListenCommand(session, midiWriter, midiWriter, Console.WriteLine,
                  musicXmlWriter: new MusicXmlScoreWriter());   // R10.3: listen now emits score.musicxml
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~TranscribeMusicXmlEmissionTests"
dotnet test --filter "Category=Fast"   # full fast suite still green
```

Expected PASS: `transcribe` now emits the full trio; the `score.musicxml` parses as MusicXML 4.0.

**Step 5 — Commit** (fresh IDs from `but status -fv`; @gitbutler skill):

```bash
but commit step-11-musicxml-emission \
  -m "feat(cli): transcribe also emits score.musicxml (completes the §7 trio)" \
  --changes <ids> --status-after
```

---

## Verify (step exit criteria)

Restating Section 6 Step 11's *Verify* bullets for this step:

- [ ] **Golden:** fixture score → committed MusicXML, byte-identical (`EmitsByteIdenticalGoldenForTwinkleFixture` against `fixtures/golden/musicxml/twinkle.musicxml`, Task 4).
- [ ] **Property:** every measure's note+rest `<duration>` values sum exactly to the time signature — the bar-level trial balance (`EveryMeasureSumsToTheTimeSignature`, 1000 cases, Task 6).
- [ ] **R11.1:** valid MusicXML 4.0, single staff, clef by range, 4/4, correct note types and rests, one `<measure>` per bar (Tasks 1–3).
- [ ] **R11.2:** output loads cleanly in MuseScore (manual check recorded as a line in `DECISIONS.md`, Task 4 — not README, which is Step 12's) and is stable via the committed golden (Task 4) and the determinism guard (Task 5).
- [ ] **R11.3:** the XML is hand-rolled with `StringBuilder`; no NuGet package was added.
- [ ] **§7 `transcribe` trio:** the `transcribe` command now writes `raw.mid`, `score.mid`, and `score.musicxml` via `MusicXmlScoreWriter` (Task 7, `TranscribeEmitsScoreMusicXmlAlongsideMidiTrio`).

## Definition of Done

- [ ] `dotnet build` succeeds with warnings-as-errors (Step 0 setting) clean.
- [ ] `dotnet format` reports no changes.
- [ ] All new tests green: `dotnet test --filter "FullyQualifiedName~MusicXml"`; and the fast suite stays green: `dotnet test --filter "Category=Fast"`.
- [ ] Dependency rule intact: `MusicXmlScoreWriter` lives in `AudioClaudio.Infrastructure.MusicXml` and implements the Application port `IScoreWriter` (`AudioClaudio.Application.Ports`, defined in Step 7); Domain gained no reference; no new NuGet package anywhere (R11.3). Confirm `AudioClaudio.Infrastructure.csproj` has no added `<PackageReference>`.
- [ ] Determinism honored: output uses LF only, UTF-8 without BOM; `.gitattributes` forces `*.musicxml` to LF so CI compares identical bytes (non-negotiable 3).
- [ ] `transcribe` emits the full §7 trio (`raw.mid`, `score.mid`, `score.musicxml`) via `MusicXmlScoreWriter` in the CLI composition root (Task 7).
- [ ] Committed via GitButler on branch `step-11-musicxml-emission`; the writer's completing commit uses the spec message `feat(infra): MusicXML writer with bar-conservation property`, with the CLI wiring in a following `feat(cli):` commit.
- [ ] Requirement-coverage table fully satisfied (every R11.x and the §7 trio row mapped to a task and a passing test).
- [ ] `DECISIONS.md` carries exactly one new line: the R11.2 manual-MuseScore-load record (Task 4). Step 11 has no *Design decision* and adds no dependency, so nothing else is recorded — and `README.md` is untouched (Step 12 owns it).
```