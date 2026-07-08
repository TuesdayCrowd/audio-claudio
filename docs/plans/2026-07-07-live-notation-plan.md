# Live Incremental Notation (`claudio listen --view`) — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Design doc:** [`2026-07-07-live-notation-design.md`](2026-07-07-live-notation-design.md) —
read it first. This plan implements the decisions recorded there without
re-litigating them; where code here needs the *why*, it points back rather
than repeating it.

**Build-spec reference:** CLAUDE.md §8 Phase 2, item 3 ("Live incremental
notation"). This is Phase-2 work, not part of the numbered Step 0–12 build
spec, so the requirement IDs below (`LV1`–`LV8`) are local to this plan, not
CLAUDE.md `R<step>.<n>` numbers — same spirit, different numbering space.

**Goal:** `claudio listen --view` opens the browser to a localhost page that
renders the growing score with OpenSheetMusicDisplay (OSMD) as the
microphone is played, note by note, then snaps to the accurate final score
on Ctrl+C — with **zero change** to `listen`'s behavior when `--view` is not
passed.

**Architecture:** A pure Application-layer accumulator
(`LiveScoreProjector`) turns the existing incremental note callback into a
growing `Score`. A new Infrastructure adapter
(`LiveNotationServer`, `AudioClaudio.Infrastructure.LiveView`) serves the
page and pushes each `Score` over Server-Sent Events as base64 MusicXML. The
Cli composition root wires them together behind two new **optional** hooks
on the already-shipped `ListenCommand`. Domain is untouched. No new NuGet
package: `System.Net.HttpListener`/SSE are BCL; OSMD is a vendored static JS
asset, exactly like the SoundFont is a vendored static binary asset.

**Tech stack:** `System.Net.HttpListener`, `System.Net.Sockets.TcpListener`
(free-port probe), `System.Threading.Channels` (per-connection outbox,
reusing the Step 10 bounded-channel idiom) — all BCL, all already implicitly
available to `AudioClaudio.Infrastructure` (no new `<PackageReference>`).
xUnit + an in-process `System.Net.Http.HttpClient` for the server's tests —
no real browser, no real audio device. CsCheck (already referenced) for one
property test. OSMD (`opensheetmusicdisplay.min.js`, BSD-3-Clause) vendored
under `src/AudioClaudio.Cli/wwwroot/`.

**Prerequisites:** v0.1.0 (Steps 0–12) green and tagged on `main` —
specifically Step 6 (`Quantizer`/`QuantizationGrid`/`Score`), Step 9
(`TranscriptionPipeline`, `TranscriptionSettings`, `TranscriptionResult`),
Step 10 (`PortAudioAudioSource`, `LiveTranscriptionSession`,
`ListenCommand`, the genuinely-incremental `StreamNotes`), and Step 11
(`MusicXmlScoreWriter`) all shipped and behaviorally unchanged.
`docs/plans/CONTRACTS.md` §6/§9/§10/§11 are the authoritative shapes this
plan builds on; where this plan introduces new types, it follows
CONTRACTS' naming conventions (§0) rather than inventing new ones.

**Commit (spec):** landed as a small stack of commits (§1 rule 5 — "one step
per commit minimum; finer-grained is fine"), one per task/part below, all on
branch `live-notation-view`. Task 3's completing commit carries the closest
thing to a headline message, `feat(infra): LiveNotationServer (HttpListener
+ SSE)`, with the CLI wiring following in Task 5's `feat(cli):` commits —
mirroring how Step 11 split its writer commit from its CLI-wiring commit.

---

## Approach

Three new pieces compose around code that already works and does not
change. `LiveScoreProjector` (Application) is the simplest of the three: it
holds a growing `List<NoteEvent>` and, on each `Add`, re-runs the same
`Quantizer.Quantize` call `TranscriptionPipeline.Transcribe` already makes —
no new algorithm, just a different place to call an existing pure function
repeatedly. It is deliberately *push*-shaped (`Add(NoteEvent) : Score`)
rather than pulling from an `IAudioSource` directly, because the live
device's frame channel can only be drained once, and `LiveTranscriptionSession.Run`
already owns that single drain (see the design doc's "Note on the
projector's shape").

`LiveNotationServer` (Infrastructure) is the substantial new piece: an
`HttpListener` that serves a handful of static files and one SSE endpoint.
It is built in two passes — static file serving (Part A of Task 3) proves
the listener, routing, and free-port machinery work at all; SSE broadcast,
late-join, and multi-client delivery (Part B) then layer the actual feature
on top. Each open `/events` connection owns a capacity-1, drop-oldest
`Channel<string>` as its outbox — the same bounded/non-blocking idiom
`CaptureFrameStream` already uses for live audio frames (Step 10), applied
here to a different kind of backpressure (a slow browser instead of a slow
disk), so `PublishScore` can never be blocked by a client that isn't
keeping up.

The CLI wiring (Task 5) is the smallest diff by line count but the one most
worth being careful with, because it touches an already-shipped, already-tested
file (`ListenCommand.cs`). The two new constructor parameters are optional
and trail the existing ones, so every existing call site — both in
`Program.cs` and in `ListenCommandTests.cs` — keeps compiling and behaving
identically without modification; a new test proves that explicitly.
`Program.cs`'s own `listen` case (top-level statements, no callable `Main`,
per Step 11's own note on this file) is composition-root wiring with no
direct unit test today and none added here — it is verified by `dotnet
build` plus Task 6's manual acceptance script, the same posture the rest of
`Program.cs` already has.

---

## Contracts

### Already shipped — consumed as-is (cite exact names; do not redeclare)

```csharp
// AudioClaudio.Application (Step 9/10) — UNCHANGED by this plan
namespace AudioClaudio.Application;

public sealed class TranscriptionPipeline : ITranscriber
{
    public TranscriptionPipeline(TranscriptionSettings settings, IFourierTransform fft);
    public TranscriptionResult Transcribe(IAudioSource source);
    public IEnumerable<NoteEvent> StreamNotes(IAudioSource source);   // incremental live feed
}

public sealed record TranscriptionSettings
{
    public static TranscriptionSettings ForTempo(double bpm);
    // FrameSize = 2048, Hop = 512 defaults; TempoBpm required; TimeSignature = FourFour;
    // Subdivision = Sixteenth; see TranscriptionSettings.cs for the full knob list.
}

public sealed record TranscriptionResult(Score Score, IReadOnlyList<NoteEvent> RawEvents);

// AudioClaudio.Application.UseCases (Step 10) — UNCHANGED by this plan
public sealed record LiveSessionResult(IReadOnlyList<NoteEvent> Events, Score Score);

public sealed class LiveTranscriptionSession
{
    public LiveTranscriptionSession(
        Func<IAudioSource, IEnumerable<NoteEvent>> streamNotes,
        Func<IAudioSource, TranscriptionResult> transcribe);
    public LiveSessionResult Run(IAudioSource source, Action<NoteEvent> onNote, CancellationToken ct = default);
}

// AudioClaudio.Domain (Step 6) — UNCHANGED by this plan
namespace AudioClaudio.Domain;

public readonly record struct QuantizationGrid
{
    public QuantizationGrid(SampleRate sampleRate, Tempo tempo, TimeSignature timeSignature, Subdivision subdivision);
    // TicksPerBeat, TicksPerMeasure, SamplesPerBeat, SamplesPerTick, SamplesToTick(long),
    // StandardValueTicks, NearestStandardValueTicks(double) — see QuantizationGrid.cs
}

public static class Quantizer
{
    public static Score Quantize(IReadOnlyList<NoteEvent> events, QuantizationGrid grid);   // pure, static
}

public sealed class Score : IEquatable<Score>
{
    public Tempo Tempo { get; }
    public TimeSignature TimeSignature { get; }
    public Subdivision Subdivision { get; }
    public IReadOnlyList<Measure> Measures { get; }
    public Score(Tempo tempo, TimeSignature timeSignature, Subdivision subdivision, IReadOnlyList<Measure> measures);
}

// AudioClaudio.Application.Ports (Step 2/7) — UNCHANGED
public interface IAudioSource { IEnumerable<Frame> Frames { get; } }
public interface IScoreWriter { void Write(Score score, Stream destination); }
public interface INoteEventWriter { void Write(IReadOnlyList<NoteEvent> events, Tempo tempo, Stream destination); }

// AudioClaudio.Infrastructure.MusicXml (Step 11) — UNCHANGED
namespace AudioClaudio.Infrastructure.MusicXml;
public sealed class MusicXmlScoreWriter : IScoreWriter
{
    public void Write(Score score, Stream destination);
    public string WriteToString(Score score);       // used directly by LiveNotationServer
}

// AudioClaudio.Infrastructure.Audio (Step 10) — UNCHANGED
namespace AudioClaudio.Infrastructure.Audio;
public sealed class PortAudioAudioSource : IAudioSource, IDisposable
{
    public PortAudioAudioSource(int sampleRateHz, int frameSize, int hop, int channels = 1, int channelCapacity = 256);
    public void Start(); public void Stop();
}

// AudioClaudio.Tests.TestSupport (Step 0) — UNCHANGED; the ONE repo-root/fixture locator
namespace AudioClaudio.Tests.TestSupport;
public static class RepoPaths
{
    public static string RepoRoot { get; }
    public static string Fixture(params string[] parts);
    // Src(project), Tests(project), SoundFontPath, GoldenDirectory also exist — see RepoPaths.cs
}

// AudioClaudio.Cli.Commands (Step 10/11) — the SHAPE before this plan's Task 5 changes it
namespace AudioClaudio.Cli.Commands;
public sealed class ListenCommand
{
    public ListenCommand(LiveTranscriptionSession session, INoteEventWriter rawWriter, IScoreWriter scoreWriter,
                         Action<string> print, IScoreWriter? musicXmlWriter = null);
    public LiveSessionResult Run(IAudioSource source, int tempoBpm, string outDir, CancellationToken ct = default);
}
```

### New types this plan defines

```csharp
// AudioClaudio.Application.UseCases — Task 2
public sealed class LiveScoreProjector
{
    public LiveScoreProjector(QuantizationGrid grid);
    public IReadOnlyList<NoteEvent> Events { get; }
    public Score Add(NoteEvent note);                            // push: accumulate + re-quantize
    public IEnumerable<Score> LiveScores(IEnumerable<NoteEvent> notes);   // pull convenience over Add
}

// AudioClaudio.Infrastructure.LiveView — Task 3
public sealed class LiveNotationServer : IDisposable
{
    public LiveNotationServer(string webRootPath, int port = 0, Func<Score, string>? scoreToMusicXml = null);
    public int Port { get; }
    public string BaseUrl { get; }                 // "http://localhost:{Port}/"
    public void Start();
    public void PublishScore(Score score);         // serialize, base64, broadcast to all /events clients
}

internal static class FreeTcpPort
{
    internal static int Find();                    // probe-then-release a free loopback TCP port
}

// AudioClaudio.Cli.Commands — Task 5, ADDITIVE to the existing ListenCommand
public sealed class ListenCommand
{
    public ListenCommand(LiveTranscriptionSession session, INoteEventWriter rawWriter, IScoreWriter scoreWriter,
                         Action<string> print, IScoreWriter? musicXmlWriter = null,
                         Action<NoteEvent>? onLiveNote = null,      // NEW, optional
                         Action<Score>? onFinalScore = null);       // NEW, optional
    // Run(...) signature is UNCHANGED.
}
```

---

## Requirements coverage

| Requirement | Task(s) | Proven by |
|---|---|---|
| **LV1** — `--view` starts a server + opens the browser; `listen` unchanged without it | T5 (Parts A+B) | `OnLiveNoteAndOnFinalScoreDefaultToNullWithoutError` (existing behavior preserved); Task 6 step 8 (manual regression check on plain `listen`) |
| **LV2** — pure, I/O-free Application live-score projection | T2 | `LiveScoreProjectorTests` (accumulation, non-mutation, prefix property) |
| **LV3** — server serves the page/bundle and streams SSE base64-MusicXML | T3 Part A + Part B | `LiveNotationServerTests` (static file tests; `PublishScoreDeliversBase64MusicXmlToAConnectedClient`) |
| **LV4** — late-join + multi-client broadcast | T3 Part B | `LateJoiningClientImmediatelyReceivesTheCurrentScore`, `BroadcastsToAllConnectedClients` |
| **LV5** — final batch `Score` published once on stop; MIDI/MusicXML trio unchanged | T5 Part A | `InvokesOnLiveNoteForEveryLiveNoteAndOnFinalScoreOnceWithTheBatchScore`; the two pre-existing `ListenCommandTests` stay green untouched |
| **LV6** — no new NuGet; OSMD vendored with BSD-3-Clause recorded | T1 | `OsmdAssetTests`; no `<PackageReference>` added to any `.csproj`; `DECISIONS.md` entry |
| **LV7** — dependency rule holds (Domain untouched; projection in Application; server in Infrastructure; Cli is the sole composition root) | T2, T3, T5 | code shape itself (`LiveScoreProjector` imports only `AudioClaudio.Domain`; `LiveNotationServer` imports only `AudioClaudio.Domain` + sibling `AudioClaudio.Infrastructure.MusicXml`); Definition of Done checklist |
| **LV8** — automated coverage for the projector + server; manual for real rendering/mic | T2, T3, T4, T6 | the respective test classes; Task 6's script |

---

### Task 1: Vendor the OSMD bundle + BSD-3-Clause license record

**Files:**
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Cli/wwwroot/osmd/opensheetmusicdisplay.min.js`
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Cli/wwwroot/osmd/LICENSE-OSMD.txt`
- Modify: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Cli/AudioClaudio.Cli.csproj`
- Modify: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/DECISIONS.md`
- Test: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/LiveView/OsmdAssetTests.cs`

This is a vendoring task, not an algorithm — it mirrors Step 8's SoundFont
task exactly: download once, verify, commit, record in `DECISIONS.md`.

Use @superpowers:test-driven-development for the red-green loop; the
"implementation" step is a download-and-verify procedure, not code.

**Step 1 — Write the failing test:**

```csharp
// tests/AudioClaudio.Tests/LiveView/OsmdAssetTests.cs
using System;
using System.IO;
using System.Text;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.LiveView;

/// <summary>
/// Sanity-checks the vendored OSMD bundle the same way Step 8's SoundFontFixtureTests
/// sanity-checked the committed .sf2: not empty, not accidentally a fetch-failure HTML
/// page, and its license file is actually present and says BSD.
/// </summary>
public class OsmdAssetTests
{
    private static string WwwRoot => Path.Combine(RepoPaths.RepoRoot, "src", "AudioClaudio.Cli", "wwwroot");

    [Fact]
    [Trait("Category", "Fast")]
    public void VendoredOsmdBundleExistsAndLooksLikeRealJavaScript()
    {
        string path = Path.Combine(WwwRoot, "osmd", "opensheetmusicdisplay.min.js");
        Assert.True(File.Exists(path), $"expected the vendored OSMD bundle at {path}");

        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 100_000,
            "the OSMD UMD bundle is legitimately large (hundreds of KB); a tiny file means a bad download");

        string head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 200)).TrimStart();
        Assert.False(head.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase),
            "the file looks like an HTML error page, not JavaScript -- check the download URL");
        Assert.False(head.StartsWith("<html", StringComparison.OrdinalIgnoreCase),
            "the file looks like an HTML error page, not JavaScript -- check the download URL");
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void OsmdLicenseFileIsCommittedAndIsBsd()
    {
        string path = Path.Combine(WwwRoot, "osmd", "LICENSE-OSMD.txt");
        Assert.True(File.Exists(path), $"expected the OSMD license text at {path}");

        string text = File.ReadAllText(path);
        Assert.Contains("BSD", text, StringComparison.OrdinalIgnoreCase);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~OsmdAssetTests"
```

Expected FAILURE: neither file exists yet; `File.Exists` assertions fail
(or, if you reorder, `File.ReadAllBytes`/`ReadAllText` throw
`FileNotFoundException` first). Red either way.

**Step 3 — Vendor the files** (download-and-verify, not code):

1. Fetch `opensheetmusicdisplay.min.js` from OSMD's published release (its
   GitHub Releases page, or the npm-published `build/opensheetmusicdisplay.min.js`
   via a CDN mirror such as `https://cdn.jsdelivr.net/npm/opensheetmusicdisplay@<version>/build/opensheetmusicdisplay.min.js`)
   fetched **once**, at execution time — never referenced live from the
   served page. Pin the exact version fetched.
2. Save it at `src/AudioClaudio.Cli/wwwroot/osmd/opensheetmusicdisplay.min.js`.
3. Fetch the OSMD project's `LICENSE` file (BSD-3-Clause) from the same
   release/repo and save it as `src/AudioClaudio.Cli/wwwroot/osmd/LICENSE-OSMD.txt`.
4. Compute the SHA-256 of the committed bundle.
5. Add the copy-to-output wiring to the csproj:

```xml
<!-- src/AudioClaudio.Cli/AudioClaudio.Cli.csproj -- add this ItemGroup -->
<ItemGroup>
  <!-- Static web assets for `listen --view` (live notation). Copied verbatim; no build
       step, no bundler -- the OSMD bundle is vendored, not built (see DECISIONS.md).
       The glob is evaluated at build time, so Task 4's index.html/app.js are picked up
       automatically once they exist -- no second csproj edit needed later. -->
  <Content Include="wwwroot\**\*" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

6. Record in `DECISIONS.md`, in the same shape as the Step 8 SoundFont entry:

```markdown
## Live notation view (`listen --view`, Phase-2 §8 item 3)

### Vendored asset: OpenSheetMusicDisplay (OSMD)
- Version: **<exact version fetched>**
- Source URL: **<exact URL fetched from>**
- File: `src/AudioClaudio.Cli/wwwroot/osmd/opensheetmusicdisplay.min.js`
  Size: **<n> bytes** SHA-256: **<hash>**
- License: committed at `src/AudioClaudio.Cli/wwwroot/osmd/LICENSE-OSMD.txt` --
  **BSD-3-Clause**, on the §1 rule 7 allow-list (MIT/Apache-2.0/BSD/MPL-2.0);
  no copyleft.
- No new NuGet package; no npm/Node toolchain introduced anywhere in this
  repo. Served as a static asset by `LiveNotationServer` (Infrastructure),
  exactly like the GeneralUser GS SoundFont is a committed static asset
  served by MeltySynth (Step 8).
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~OsmdAssetTests"
```

Expected PASS: both files are present, well past the size floor, and don't
look like an error page; the license text mentions BSD.

**Step 5 — Commit** (use the @gitbutler skill; read fresh IDs from `but status -fv`):

```bash
but branch new live-notation-view && but mark live-notation-view
but status -fv    # read the change <ids> for the files created/modified above
but commit live-notation-view \
  -m "chore(cli): vendor OpenSheetMusicDisplay bundle (BSD-3-Clause)" \
  --changes <ids> --status-after
```

---

### Task 2: Application — `LiveScoreProjector` (TDD)

**Files:**
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Application/UseCases/LiveScoreProjector.cs`
- Test: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/Application/LiveScoreProjectorTests.cs`

> **Why push-shaped, not `IEnumerable<Score> LiveScores(IAudioSource source)`:**
> see the design doc's "Note on the projector's shape." A live capture
> channel drains exactly once, and `LiveTranscriptionSession.Run` already
> owns that drain — a second independent enumeration would race it.

Use @superpowers:test-driven-development for the red-green loop.

**Step 1 — Write the failing test:**

```csharp
// tests/AudioClaudio.Tests/Application/LiveScoreProjectorTests.cs
using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Application.UseCases;
using AudioClaudio.Domain;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.Application;

public class LiveScoreProjectorTests
{
    private static readonly SampleRate Rate = new SampleRate(44100);
    private static readonly QuantizationGrid Grid =
        new QuantizationGrid(Rate, new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth);

    private static NoteEvent Note(int midi, long onsetSamples) =>
        new NoteEvent(new Pitch(midi), new SamplePosition(onsetSamples, Rate),
                     new SampleDuration(5512, Rate), velocity: 100); // ~1/8 note at 120 BPM

    private static List<ScoreElement> Flatten(Score score) =>
        score.Measures.SelectMany(m => m.Elements).ToList();

    [Fact]
    [Trait("Category", "Fast")]
    public void AddAccumulatesAndReturnsAScoreOverAllNotesSoFar()
    {
        var projector = new LiveScoreProjector(Grid);

        Score afterFirst = projector.Add(Note(60, 0));
        Score afterSecond = projector.Add(Note(62, 5512));

        Assert.Single(Flatten(afterFirst).Where(e => e.Kind == ElementKind.Note));
        var secondNotes = Flatten(afterSecond).Where(e => e.Kind == ElementKind.Note).ToList();
        Assert.Equal(2, secondNotes.Count);
        Assert.Equal(60, secondNotes[0].Pitch!.Value.MidiNumber);
        Assert.Equal(62, secondNotes[1].Pitch!.Value.MidiNumber);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void AddNeverMutatesAPreviouslyReturnedScore()
    {
        var projector = new LiveScoreProjector(Grid);

        Score afterFirst = projector.Add(Note(60, 0));
        int notesInFirstSnapshot = Flatten(afterFirst).Count(e => e.Kind == ElementKind.Note);

        projector.Add(Note(62, 5512));

        // The Score returned for note 1 is a snapshot -- R6.2's append-only instinct, carried
        // into the live view: the performance grows, past Scores never change under you.
        Assert.Equal(notesInFirstSnapshot, Flatten(afterFirst).Count(e => e.Kind == ElementKind.Note));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void LiveScoresYieldsOneGrowingScorePerNoteMatchingDirectQuantize()
    {
        var notes = new[] { Note(60, 0), Note(62, 5512), Note(64, 11025) };
        var projector = new LiveScoreProjector(Grid);

        List<Score> scores = projector.LiveScores(notes).ToList();

        Assert.Equal(notes.Length, scores.Count);
        for (int i = 0; i < notes.Length; i++)
        {
            Score expected = Quantizer.Quantize(notes.Take(i + 1).ToList(), Grid);
            Assert.Equal(expected, scores[i]); // Score implements IEquatable<Score> (Step 6)
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void LiveScoresPrefixPropertyMatchesDirectQuantizeForRandomNoteSequences()
    {
        var genNote =
            from midi in Gen.Int[Pitch.MinMidi, Pitch.MaxMidi]
            from gapSamples in Gen.Int[1, 20]
            select (midi, gapSamples);

        var genSequence =
            from count in Gen.Int[1, 12]
            from notes in genNote.List[count]
            select notes;

        genSequence.Sample(sequence =>
        {
            long onset = 0;
            var notes = new List<NoteEvent>();
            foreach (var (midi, gapSamples) in sequence)
            {
                notes.Add(new NoteEvent(new Pitch(midi), new SamplePosition(onset, Rate),
                                        new SampleDuration(2000, Rate), velocity: 100));
                onset += gapSamples * 2000L;
            }

            var projector = new LiveScoreProjector(Grid);
            List<Score> produced = projector.LiveScores(notes).ToList();

            for (int i = 0; i < notes.Count; i++)
            {
                Score expected = Quantizer.Quantize(notes.Take(i + 1).ToList(), Grid);
                Assert.Equal(expected, produced[i]);
            }
        }, iter: 200);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~LiveScoreProjectorTests"
```

Expected FAILURE: compile error — `LiveScoreProjector` does not exist yet
(`CS0246`). Red as intended.

**Step 3 — Minimal implementation:**

```csharp
// src/AudioClaudio.Application/UseCases/LiveScoreProjector.cs
using System.Collections.Generic;
using AudioClaudio.Domain;

namespace AudioClaudio.Application.UseCases;

/// <summary>
/// Projects an incremental note stream onto a growing, approximate live <see cref="Score"/>:
/// each new note is appended to the performance so far and the WHOLE performance is
/// re-quantized (<see cref="Quantizer.Quantize"/> is pure and cheap enough at session-length
/// note counts -- see the live-notation design doc's "Full re-render per update" decision).
/// Pure and I/O-free: Domain types only, no clock, no device, no HTTP -- the same discipline
/// as <see cref="TranscriptionPipeline"/> and <see cref="LiveTranscriptionSession"/>.
///
/// Deliberately push-shaped (<see cref="Add"/>), not a pull over <c>IAudioSource</c>: a live
/// capture channel drains exactly once (see <see cref="LiveTranscriptionSession"/>'s remarks),
/// and that one drain is already owned by <see cref="LiveTranscriptionSession.Run"/>'s single
/// <c>onNote</c> callback. The Cli composition root feeds this projector from THAT callback
/// (see <c>ListenCommand</c>'s <c>onLiveNote</c> hook, Task 5) rather than re-enumerating the
/// source.
/// </summary>
public sealed class LiveScoreProjector
{
    private readonly QuantizationGrid _grid;
    private readonly List<NoteEvent> _events = new();

    public LiveScoreProjector(QuantizationGrid grid) => _grid = grid;

    /// <summary>The accumulated performance so far, in the order notes were added.</summary>
    public IReadOnlyList<NoteEvent> Events => _events;

    /// <summary>Append one note and return the Score quantized over every note added so far.</summary>
    public Score Add(NoteEvent note)
    {
        _events.Add(note);
        return Quantizer.Quantize(_events, _grid);
    }

    /// <summary>
    /// Pull-shaped convenience over an already-materialized (or fake, for tests) note sequence:
    /// one growing Score per note, via repeated <see cref="Add"/>. NOT used directly against a
    /// live <c>IAudioSource</c> -- see the remarks above.
    /// </summary>
    public IEnumerable<Score> LiveScores(IEnumerable<NoteEvent> notes)
    {
        foreach (NoteEvent note in notes)
        {
            yield return Add(note);
        }
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~LiveScoreProjectorTests"
dotnet test --filter "Category=Fast"   # confirm the fast suite is still green
```

Expected PASS: all four tests green, including the CsCheck property (200
generated sequences).

**Step 5 — Commit** (fresh IDs from `but status -fv`; @gitbutler skill):

```bash
dotnet format
but status -fv
but commit live-notation-view \
  -m "feat(app): live Score projection over the incremental note stream" \
  --changes <ids> --status-after
```

---

### Task 3: Infrastructure — `LiveNotationServer` (HttpListener + SSE + free port)

**Files:**
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Infrastructure/LiveView/FreeTcpPort.cs`
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Infrastructure/LiveView/LiveNotationServer.cs`
- Test: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/LiveView/LiveNotationServerTests.cs`

This task lands in two parts — static serving first (proves the listener,
routing, and free-port machinery), then SSE (the actual feature) on top of
it. Both parts extend the same test class and the same two source files;
each part's Step 3 pastes the *complete* file, matching how Step 11's plan
handles multi-part single files (never a partial diff description).

Use @superpowers:test-driven-development for each part's red-green loop.

#### Part A — free port + static file serving

**Step 1 — Write the failing test:**

```csharp
// tests/AudioClaudio.Tests/LiveView/LiveNotationServerTests.cs
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AudioClaudio.Infrastructure.LiveView;
using Xunit;

namespace AudioClaudio.Tests.LiveView;

public class LiveNotationServerTests
{
    // Read-only fixture content shared across tests (no test mutates it) -- a small stand-in
    // for the real (large, vendored) OSMD bundle, so these tests stay fast and independent of
    // it. LiveNotationServer's constructor takes an explicit webRootPath for exactly this
    // reason -- production points it at the real wwwroot; tests point it here.
    private static readonly string WebRoot = CreateFixtureWebRoot();

    private static string CreateFixtureWebRoot()
    {
        string dir = Directory.CreateTempSubdirectory("claudio_liveview_").FullName;
        File.WriteAllText(Path.Combine(dir, "index.html"), "<html><body>osmd host</body></html>");
        Directory.CreateDirectory(Path.Combine(dir, "osmd"));
        File.WriteAllText(Path.Combine(dir, "osmd", "opensheetmusicdisplay.min.js"), "/* fake bundle */");
        return dir;
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void TwoServersAutoAssignDifferentFreePorts()
    {
        using var a = new LiveNotationServer(WebRoot);
        using var b = new LiveNotationServer(WebRoot);

        Assert.NotEqual(a.Port, b.Port);
        Assert.True(a.Port > 0);
        Assert.Equal($"http://localhost:{a.Port}/", a.BaseUrl);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task ServesIndexHtmlAtRoot()
    {
        using var server = new LiveNotationServer(WebRoot);
        server.Start();
        using var http = new HttpClient();

        var response = await http.GetAsync(server.BaseUrl);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("osmd host", body);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task ServesTheVendoredOsmdBundlePath()
    {
        using var server = new LiveNotationServer(WebRoot);
        server.Start();
        using var http = new HttpClient();

        var body = await http.GetStringAsync(server.BaseUrl + "osmd/opensheetmusicdisplay.min.js");

        Assert.Equal("/* fake bundle */", body);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public async Task UnknownPathReturns404()
    {
        using var server = new LiveNotationServer(WebRoot);
        server.Start();
        using var http = new HttpClient();

        var response = await http.GetAsync(server.BaseUrl + "no-such-file.txt");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~LiveNotationServerTests"
```

Expected FAILURE: compile error — `LiveNotationServer` does not exist yet
(`CS0246`). Red.

**Step 3 — Minimal implementation:**

```csharp
// src/AudioClaudio.Infrastructure/LiveView/FreeTcpPort.cs
using System.Net;
using System.Net.Sockets;

namespace AudioClaudio.Infrastructure.LiveView;

/// <summary>
/// Picks a free TCP port by binding an ephemeral probe listener to loopback:0, reading back
/// whatever port the OS assigned, then releasing it immediately so <see cref="LiveNotationServer"/>
/// can bind an <c>HttpListener</c> to the same number. A microscopic release-then-rebind race is
/// possible in principle (see the live-notation design doc's "Port selection" decision);
/// accepted as a documented risk for this single-user, localhost-only, once-per-session feature.
/// </summary>
internal static class FreeTcpPort
{
    internal static int Find()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
        probe.Stop();
        return port;
    }
}
```

```csharp
// src/AudioClaudio.Infrastructure/LiveView/LiveNotationServer.cs
using System.Net;

namespace AudioClaudio.Infrastructure.LiveView;

/// <summary>
/// Serves the live-notation web page (static files under <c>webRootPath</c>). SSE (`/events`)
/// arrives in Part B. Localhost-only -- bound to <c>http://localhost:&lt;port&gt;/</c>, never
/// <c>+</c>/<c>*</c>, so it needs no URL-ACL reservation or admin rights on Windows. BCL only:
/// <see cref="System.Net.HttpListener"/>, no NuGet package (the live-notation design doc's
/// "Web server" decision).
/// </summary>
public sealed class LiveNotationServer : IDisposable
{
    private readonly string _webRootPath;
    private readonly HttpListener _listener = new();
    private Task? _acceptLoop;

    public int Port { get; }
    public string BaseUrl { get; }

    public LiveNotationServer(string webRootPath, int port = 0)
    {
        _webRootPath = Path.GetFullPath(webRootPath);
        Port = port == 0 ? FreeTcpPort.Find() : port;
        BaseUrl = $"http://localhost:{Port}/";
        _listener.Prefixes.Add(BaseUrl);
    }

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (!_listener.IsListening)
            {
                return; // Dispose()/Stop() closed the listener while a GetContextAsync was pending
            }

            _ = Task.Run(() => HandleRequestAsync(ctx));
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url?.AbsolutePath ?? "/";
        await ServeStaticFileAsync(ctx, path).ConfigureAwait(false);
    }

    private async Task ServeStaticFileAsync(HttpListenerContext ctx, string path)
    {
        string relative = path == "/" ? "index.html" : path.TrimStart('/');
        string filePath = Path.GetFullPath(Path.Combine(_webRootPath, relative));

        if (!filePath.StartsWith(_webRootPath, StringComparison.Ordinal) || !File.Exists(filePath))
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            ctx.Response.Close();
            return;
        }

        ctx.Response.ContentType = ContentTypeFor(filePath);
        byte[] bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        try { _listener.Stop(); } catch { /* already stopped */ }
        try { _listener.Close(); } catch { /* already closed */ }
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~LiveNotationServerTests"
```

Expected PASS: all four Part-A tests green — free-port uniqueness, index
serving, bundle-path serving, 404 for unknown paths.

**Step 5 — Commit** (fresh IDs from `but status -fv`; @gitbutler skill):

```bash
but status -fv
but commit live-notation-view \
  -m "feat(infra): LiveNotationServer static file serving + free-port helper" \
  --changes <ids> --status-after
```

#### Part B — SSE broadcast, late-join, multiple clients

**Step 1 — Write the failing test** (append to `LiveNotationServerTests`):

```csharp
// Append to class LiveNotationServerTests -- and add these usings at the top of the file:
// using System.Linq;
// using AudioClaudio.Domain;
// using AudioClaudio.Infrastructure.MusicXml;

private static Score Fixture(params int[] midiNotes)
{
    var rate = new SampleRate(44100);
    var grid = new QuantizationGrid(rate, new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth);
    var events = midiNotes
        .Select((midi, i) => new NoteEvent(new Pitch(midi), new SamplePosition(i * 5512L, rate),
                                           new SampleDuration(5512, rate), 100))
        .ToList();
    return Quantizer.Quantize(events, grid);
}

private static async Task<string> ReadSseDataLineAsync(StreamReader reader, TimeSpan timeout)
{
    // SSE frames are "event: score\ndata: <payload>\n\n" -- skip the event: line, return the
    // decoded data: payload.
    string? line;
    do
    {
        var readTask = reader.ReadLineAsync();
        if (await Task.WhenAny(readTask, Task.Delay(timeout)) != readTask)
        {
            throw new TimeoutException("No SSE line received in time.");
        }

        line = await readTask;
    } while (line is not null && !line.StartsWith("data: ", StringComparison.Ordinal));

    Assert.NotNull(line);
    return Encoding.UTF8.GetString(Convert.FromBase64String(line!["data: ".Length..]));
}

[Fact]
[Trait("Category", "Fast")]
public async Task PublishScoreDeliversBase64MusicXmlToAConnectedClient()
{
    using var server = new LiveNotationServer(WebRoot);
    server.Start();
    using var http = new HttpClient();
    using var response = await http.GetAsync(server.BaseUrl + "events", HttpCompletionOption.ResponseHeadersRead);
    Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

    Score score = Fixture(60, 62);
    server.PublishScore(score);

    string xml = await ReadSseDataLineAsync(reader, TimeSpan.FromSeconds(5));
    Assert.Equal(new MusicXmlScoreWriter().WriteToString(score), xml);
}

[Fact]
[Trait("Category", "Fast")]
public async Task LateJoiningClientImmediatelyReceivesTheCurrentScore()
{
    using var server = new LiveNotationServer(WebRoot);
    server.Start();
    Score score = Fixture(64);
    server.PublishScore(score);          // published BEFORE any client connects

    using var http = new HttpClient();
    using var response = await http.GetAsync(server.BaseUrl + "events", HttpCompletionOption.ResponseHeadersRead);
    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

    string xml = await ReadSseDataLineAsync(reader, TimeSpan.FromSeconds(5));
    Assert.Equal(new MusicXmlScoreWriter().WriteToString(score), xml);   // no fresh publish needed
}

[Fact]
[Trait("Category", "Fast")]
public async Task BroadcastsToAllConnectedClients()
{
    using var server = new LiveNotationServer(WebRoot);
    server.Start();
    using var httpA = new HttpClient();
    using var httpB = new HttpClient();
    using var responseA = await httpA.GetAsync(server.BaseUrl + "events", HttpCompletionOption.ResponseHeadersRead);
    using var responseB = await httpB.GetAsync(server.BaseUrl + "events", HttpCompletionOption.ResponseHeadersRead);
    using var readerA = new StreamReader(await responseA.Content.ReadAsStreamAsync());
    using var readerB = new StreamReader(await responseB.Content.ReadAsStreamAsync());

    Score score = Fixture(67);
    server.PublishScore(score);

    string expected = new MusicXmlScoreWriter().WriteToString(score);
    Assert.Equal(expected, await ReadSseDataLineAsync(readerA, TimeSpan.FromSeconds(5)));
    Assert.Equal(expected, await ReadSseDataLineAsync(readerB, TimeSpan.FromSeconds(5)));
}
```

Also add `using System;`, `using System.Linq;`, `using System.Text;`,
`using AudioClaudio.Domain;`, `using AudioClaudio.Infrastructure.MusicXml;`
to the top of the test file alongside Part A's usings.

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~LiveNotationServerTests"
```

Expected FAILURE: compile error — `LiveNotationServer.PublishScore` does not
exist yet. Red.

**Step 3 — Minimal implementation** (full updated `LiveNotationServer.cs`;
`FreeTcpPort.cs` is unchanged from Part A):

```csharp
// src/AudioClaudio.Infrastructure/LiveView/LiveNotationServer.cs
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.MusicXml;

namespace AudioClaudio.Infrastructure.LiveView;

/// <summary>
/// Serves the live-notation web page and pushes each published <see cref="Score"/> to every
/// connected browser over Server-Sent Events, as base64-encoded MusicXML (the live-notation
/// design doc's SSE protocol). Localhost-only (<c>http://localhost:&lt;port&gt;/</c>), BCL-only
/// (<see cref="System.Net.HttpListener"/>, no NuGet package). A late-joining client receives the
/// most recently published score immediately, before any new publish.
/// </summary>
public sealed class LiveNotationServer : IDisposable
{
    private readonly string _webRootPath;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private readonly List<SseConnection> _connections = new();
    private readonly Func<Score, string> _toMusicXml;
    private string? _latestBase64;
    private Task? _acceptLoop;

    public int Port { get; }
    public string BaseUrl { get; }

    public LiveNotationServer(string webRootPath, int port = 0, Func<Score, string>? scoreToMusicXml = null)
    {
        _webRootPath = Path.GetFullPath(webRootPath);
        Port = port == 0 ? FreeTcpPort.Find() : port;
        BaseUrl = $"http://localhost:{Port}/";
        _listener.Prefixes.Add(BaseUrl);
        _toMusicXml = scoreToMusicXml ?? new MusicXmlScoreWriter().WriteToString;
    }

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>
    /// Serialize, base64-encode, remember as "latest" (for late joiners), and broadcast to every
    /// open /events connection. Never blocks on a slow client -- each connection has its own
    /// bounded, coalescing outbox (see <see cref="SseConnection"/>).
    /// </summary>
    public void PublishScore(Score score)
    {
        string xml = _toMusicXml(score);
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));

        SseConnection[] snapshot;
        lock (_gate)
        {
            _latestBase64 = base64;
            snapshot = _connections.ToArray();
        }

        foreach (SseConnection connection in snapshot)
        {
            connection.Enqueue(base64);
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (!_listener.IsListening)
            {
                return; // Dispose()/Stop() closed the listener while a GetContextAsync was pending
            }

            _ = Task.Run(() => HandleRequestAsync(ctx));
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url?.AbsolutePath ?? "/";

        if (path == "/events")
        {
            await HandleSseAsync(ctx).ConfigureAwait(false);
            return;
        }

        await ServeStaticFileAsync(ctx, path).ConfigureAwait(false);
    }

    private async Task HandleSseAsync(HttpListenerContext ctx)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.SendChunked = true;

        var connection = new SseConnection(ctx.Response);
        lock (_gate)
        {
            _connections.Add(connection);
            if (_latestBase64 is not null)
            {
                connection.Enqueue(_latestBase64); // late-joiner sync: current state, no fresh publish needed
            }
        }

        try
        {
            await connection.PumpAsync(_stopping.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _connections.Remove(connection);
        }
    }

    private async Task ServeStaticFileAsync(HttpListenerContext ctx, string path)
    {
        string relative = path == "/" ? "index.html" : path.TrimStart('/');
        string filePath = Path.GetFullPath(Path.Combine(_webRootPath, relative));

        if (!filePath.StartsWith(_webRootPath, StringComparison.Ordinal) || !File.Exists(filePath))
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            ctx.Response.Close();
            return;
        }

        ctx.Response.ContentType = ContentTypeFor(filePath);
        byte[] bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        _stopping.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        try { _listener.Close(); } catch { /* already closed */ }

        lock (_gate)
        {
            foreach (SseConnection connection in _connections) connection.Close();
            _connections.Clear();
        }

        _stopping.Dispose();
    }

    /// <summary>
    /// One open browser's /events connection: a capacity-1, drop-oldest outbox (the same
    /// non-blocking, drop-not-swallow idiom <c>CaptureFrameStream</c> uses for live audio
    /// frames, Step 10) so a burst of rapid notes coalesces to the freshest score instead of
    /// queuing an ever-growing backlog, and <see cref="LiveNotationServer.PublishScore"/> never
    /// blocks on a slow client.
    /// </summary>
    private sealed class SseConnection
    {
        private readonly HttpListenerResponse _response;
        private readonly Channel<string> _outbox = Channel.CreateBounded<string>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

        public SseConnection(HttpListenerResponse response) => _response = response;

        public void Enqueue(string base64) => _outbox.Writer.TryWrite(base64);

        public async Task PumpAsync(CancellationToken ct)
        {
            try
            {
                await foreach (string base64 in _outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes($"event: score\ndata: {base64}\n\n");
                    await _response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                    await _response.OutputStream.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // client disconnected, or the server is stopping (ct cancelled) -- either way,
                // stop pumping.
            }
            finally
            {
                Close();
            }
        }

        public void Close()
        {
            _outbox.Writer.TryComplete();
            try { _response.OutputStream.Close(); } catch { /* already gone */ }
        }
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~LiveNotationServerTests"
dotnet test --filter "Category=Fast"   # confirm the fast suite is still green
```

Expected PASS: all seven `LiveNotationServerTests` green — Part A's four
plus Part B's three (single delivery, late-join, broadcast).

**Step 5 — Commit** (this is the task's headline commit; fresh IDs from
`but status -fv`; @gitbutler skill):

```bash
dotnet format
but status -fv
but commit live-notation-view \
  -m "feat(infra): LiveNotationServer (HttpListener + SSE)" \
  --changes <ids> --status-after
```

---

### Task 4: Web assets — `index.html` + `app.js`

**Files:**
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Cli/wwwroot/index.html`
- Create: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Cli/wwwroot/app.js`
- Test: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/LiveView/WebAssetContentTests.cs`

The real OSMD rendering these files trigger can only be proven in a real
browser (Task 6). What *is* practical and worth automating here is a
content-substring check — not a DOM/browser test, so no headless-browser
dependency is introduced — confirming the committed page actually wires up
the pieces Tasks 1 and 3 built: the OSMD bundle path, and the `/events`
endpoint.

Use @superpowers:test-driven-development for the red-green loop.

**Step 1 — Write the failing test:**

```csharp
// tests/AudioClaudio.Tests/LiveView/WebAssetContentTests.cs
using System.IO;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.LiveView;

public class WebAssetContentTests
{
    private static string WwwRoot => Path.Combine(RepoPaths.RepoRoot, "src", "AudioClaudio.Cli", "wwwroot");

    [Fact]
    [Trait("Category", "Fast")]
    public void IndexHtmlReferencesTheOsmdBundleAndAppScript()
    {
        string html = File.ReadAllText(Path.Combine(WwwRoot, "index.html"));

        Assert.Contains("osmd/opensheetmusicdisplay.min.js", html);
        Assert.Contains("app.js", html);
        Assert.Contains("osmd-container", html); // the element OSMD renders into
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void AppJsConnectsToEventsAndCallsOsmdLoadAndRender()
    {
        string js = File.ReadAllText(Path.Combine(WwwRoot, "app.js"));

        Assert.Contains("EventSource(\"/events\")", js);
        Assert.Contains("osmd.load(", js);
        Assert.Contains("osmd.render()", js);
        Assert.Contains("atob(", js); // base64 decode of the SSE payload (the SSE protocol decision)
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~WebAssetContentTests"
```

Expected FAILURE: neither file exists yet; `File.ReadAllText` throws
`FileNotFoundException`. Red.

**Step 3 — Write the two files:**

```html
<!-- src/AudioClaudio.Cli/wwwroot/index.html -->
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <title>Audio Claudio — Live</title>
</head>
<body>
  <div id="osmd-container"></div>
  <script src="osmd/opensheetmusicdisplay.min.js"></script>
  <script src="app.js"></script>
</body>
</html>
```

```javascript
// src/AudioClaudio.Cli/wwwroot/app.js
// Live notation view: renders whatever score LiveNotationServer last published, and
// re-renders on every SSE update. OSMD is a whole-document renderer (no incremental API in
// the vendored build), so each event is a complete MusicXML document, base64-encoded to
// survive SSE's line-oriented framing -- see the live-notation design doc's SSE protocol.
const osmd = new opensheetmusicdisplay.OpenSheetMusicDisplay("osmd-container");
const source = new EventSource("/events");

source.addEventListener("score", (event) => {
  const xml = atob(event.data);
  osmd.load(xml).then(() => osmd.render());
});
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~WebAssetContentTests"
```

Expected PASS: both content checks green.

**Step 5 — Commit** (fresh IDs from `but status -fv`; @gitbutler skill):

```bash
but status -fv
but commit live-notation-view \
  -m "feat(cli): live notation web assets (index.html + app.js)" \
  --changes <ids> --status-after
```

---

### Task 5: `listen --view` CLI wiring

**Files:**
- Modify: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Cli/Commands/ListenCommand.cs`
- Modify: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/src/AudioClaudio.Cli/Program.cs`
- Test: `/Users/lawls/Development/TuesdayCrowd/Projects/audio-claudio/tests/AudioClaudio.Tests/Cli/ListenCommandTests.cs`

#### Part A — `ListenCommand`'s two new optional hooks (TDD)

Use @superpowers:test-driven-development for the red-green loop.

**Step 1 — Write the failing test** (append to the existing
`ListenCommandTests` class — its `Session`, `Note`, `FakeSource`,
`SpyMidiWriter` helpers are reused unchanged):

```csharp
// Append to class ListenCommandTests

[Fact]
[Trait("Category", "Fast")]
public void InvokesOnLiveNoteForEveryLiveNoteAndOnFinalScoreOnceWithTheBatchScore()
{
    var notes = new[] { Note(60, 0), Note(62, 22050) };
    var midi = new SpyMidiWriter();
    var liveNotes = new List<int>();
    Score? finalScore = null;
    string dir = Path.Combine(Path.GetTempPath(), $"claudio_listen_{Guid.NewGuid():N}");
    try
    {
        var cmd = new ListenCommand(Session(notes), midi, midi, _ => { }, musicXmlWriter: null,
                                    onLiveNote: n => liveNotes.Add(n.Pitch.MidiNumber),
                                    onFinalScore: s => finalScore = s);
        var result = cmd.Run(new FakeSource(), tempoBpm: 120, outDir: dir);

        Assert.Equal(new[] { 60, 62 }, liveNotes);      // fired once per live note, in order
        Assert.NotNull(finalScore);
        Assert.Equal(result.Score, finalScore);          // the BATCH score, not a live approximation
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
}

// The two ORIGINAL tests above construct ListenCommand without onLiveNote/onFinalScore --
// this confirms both new hooks are genuinely optional and behavior is unaffected when absent.
[Fact]
[Trait("Category", "Fast")]
public void OnLiveNoteAndOnFinalScoreDefaultToNullWithoutError()
{
    var notes = new[] { Note(60, 0) };
    var midi = new SpyMidiWriter();
    string dir = Path.Combine(Path.GetTempPath(), $"claudio_listen_{Guid.NewGuid():N}");
    try
    {
        var cmd = new ListenCommand(Session(notes), midi, midi, _ => { }); // no new params at all
        var result = cmd.Run(new FakeSource(), tempoBpm: 120, outDir: dir);
        Assert.Single(result.Events);
    }
    finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~ListenCommandTests"
```

Expected FAILURE: compile error — `ListenCommand`'s constructor has no
`onLiveNote`/`onFinalScore` parameters yet. Red.

**Step 3 — Minimal implementation** (full updated `ListenCommand.cs`):

```csharp
// src/AudioClaudio.Cli/Commands/ListenCommand.cs
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
///
/// <paramref name="onLiveNote"/>/<paramref name="onFinalScore"/> (live-notation view, Phase-2
/// §8 item 3) are OPTIONAL hooks, both null by default, so every existing caller/test is
/// unaffected. They exist because the live device's note stream is enumerated EXACTLY ONCE, by
/// this class's own call to <see cref="LiveTranscriptionSession.Run"/> -- a second, independent
/// consumer of the same live source is not possible (see the live-notation design doc).
/// <c>onLiveNote</c> fires once per note, at the same moment as the console print;
/// <c>onFinalScore</c> fires once, after the accurate batch <see cref="Score"/> is computed,
/// with THAT (not a live approximation) score.
/// </summary>
public sealed class ListenCommand
{
    private readonly LiveTranscriptionSession _session;
    private readonly INoteEventWriter _rawWriter;   // DryWetMidiWriter (raw performance)
    private readonly IScoreWriter _scoreWriter;     // DryWetMidiWriter (quantized MIDI)
    private readonly IScoreWriter? _musicXmlWriter; // MusicXmlScoreWriter; null until Step 11 registers it
    private readonly Action<string> _print;
    private readonly Action<NoteEvent>? _onLiveNote;
    private readonly Action<Score>? _onFinalScore;

    public ListenCommand(LiveTranscriptionSession session,
                         INoteEventWriter rawWriter, IScoreWriter scoreWriter,
                         Action<string> print, IScoreWriter? musicXmlWriter = null,
                         Action<NoteEvent>? onLiveNote = null, Action<Score>? onFinalScore = null)
    {
        _session = session;
        _rawWriter = rawWriter;
        _scoreWriter = scoreWriter;
        _print = print;
        _musicXmlWriter = musicXmlWriter;
        _onLiveNote = onLiveNote;
        _onFinalScore = onFinalScore;
    }

    public LiveSessionResult Run(IAudioSource source, int tempoBpm, string outDir,
                                 CancellationToken ct = default)
    {
        Directory.CreateDirectory(outDir);
        _print($"Listening at {tempoBpm} BPM. Press Ctrl+C to stop.");

        // The live print streams notes incrementally; the returned result is the ACCURATE
        // batch transcription of the session's audio (R10.3) — that is what the files below use.
        // tempoBpm still denominates the raw-performance MIDI's tempo map (INoteEventWriter).
        var result = _session.Run(source, n =>
        {
            _print(FormatNote(n));
            _onLiveNote?.Invoke(n);
        }, ct);
        _onFinalScore?.Invoke(result.Score);

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

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~ListenCommandTests"
dotnet test --filter "Category=Fast"   # confirms the two PRE-EXISTING tests are still green, unchanged
```

Expected PASS: all four `ListenCommandTests` green — the two original ones
(`PrintsEachNoteAndWritesMidiTrio`, `WritesMusicXmlOnlyWhenWriterProvided`)
plus the two new ones.

**Step 5 — Commit** (fresh IDs from `but status -fv`; @gitbutler skill):

```bash
dotnet format
but status -fv
but commit live-notation-view \
  -m "feat(cli): ListenCommand onLiveNote/onFinalScore hooks (optional, additive)" \
  --changes <ids> --status-after
```

#### Part B — `Program.cs`: `--view` flag, server/projector wiring, browser-open

`Program.cs` uses top-level statements with no callable `Main` (as Step
11's plan itself notes for this same file), so this part has no
independent unit test — exactly the posture the rest of `Program.cs`'s
composition-root code already has. It is verified by `dotnet build` and by
Task 6's manual acceptance script, not by a red/green cycle.

**Implementation** (full updated `listen` case; the `transcribe`/`render`/`play`
cases and the two local functions `Usage`/`TryReadOption` are unchanged
from before this plan — only the `listen` case, the using list, and one new
local function `TryOpenBrowser` change):

```csharp
// src/AudioClaudio.Cli/Program.cs
using System.Diagnostics;
using AudioClaudio.Application;
using AudioClaudio.Application.UseCases;
using AudioClaudio.Cli.Commands;
using AudioClaudio.Cli.Composition;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Infrastructure.LiveView;
using AudioClaudio.Infrastructure.Midi;
using AudioClaudio.Infrastructure.MusicXml;
using AudioClaudio.Infrastructure.Synthesis;

var rate = new SampleRate(44100);

if (args.Length == 0)
    return Usage();

// SoundFont/synth construction is LAZY (Step 9): `transcribe` never touches a synthesizer, so it
// must run with no .sf2 present. Only `render`/`play` resolve and construct it, on first use.
string? soundFontOption = TryReadOption(args, "--soundfont");
var synthesizer = new Lazy<MeltySynthSynthesizer>(() => new MeltySynthSynthesizer(SoundFontLocator.Resolve(soundFontOption)));

switch (args[0])
{
    case "transcribe" when args.Length >= 2:
        {
            // claudio transcribe <in.wav> --tempo N [--out-dir .]
            double tempo = double.Parse(
                TryReadOption(args, "--tempo")
                    ?? throw new ArgumentException("transcribe requires --tempo <bpm>"),
                System.Globalization.CultureInfo.InvariantCulture);
            string outDir = TryReadOption(args, "--out-dir") ?? ".";
            TranscribeCommand.Run(args[1], tempo, outDir);
            return 0;
        }
    case "render" when args.Length >= 3:
        {
            // Step 7 reader: load the committed/source MIDI into domain NoteEvents.
            var notes = MidiFileReader.ReadFile(args[1], rate).Events;
            RenderCommand.RenderToWav(notes, synthesizer.Value, rate, args[2]);
            return 0;
        }
    case "play" when args.Length >= 2:
        {
            var notes = MidiFileReader.ReadFile(args[1], rate).Events;
            PlayCommand.Play(notes, synthesizer.Value, rate);
            return 0;
        }
    case "listen":
        {
            // claudio listen --tempo N [--out-dir .] [--view]
            // The composition root — the ONLY place adapters are constructed (Section 7) and
            // Ctrl+C is wired. The mic is just one more IAudioSource; the live print streams from
            // pipeline.StreamNotes; the accurate files come from pipeline.Transcribe on stop.
            int tempo = int.Parse(
                TryReadOption(args, "--tempo")
                    ?? throw new ArgumentException("listen requires --tempo <bpm>"),
                System.Globalization.CultureInfo.InvariantCulture);
            string outDir = TryReadOption(args, "--out-dir") ?? ".";
            bool view = Array.IndexOf(args, "--view") >= 0;
            const int SampleRateHz = 44100, FrameSize = 1024, Hop = 256;

            var settings = TranscriptionSettings.ForTempo(tempo) with { FrameSize = FrameSize, Hop = Hop };
            var pipeline = new TranscriptionPipeline(settings, new Radix2Fft()); // Domain FFT (Step 3 Option A)

            using var micSource = new PortAudioAudioSource(SampleRateHz, FrameSize, Hop, channels: 1);
            var midiWriter = new DryWetMidiWriter(); // implements INoteEventWriter + IScoreWriter
            var session = new LiveTranscriptionSession(pipeline.StreamNotes, pipeline.Transcribe);

            // --view (live-notation view, Phase-2 §8 item 3): a local HTTP server + browser tab
            // rendering the growing score. Both stay null when --view is absent, so plain
            // `listen` behavior (R10.3) is completely unchanged.
            using var server = view
                ? new LiveNotationServer(Path.Combine(AppContext.BaseDirectory, "wwwroot"))
                : null;
            var projector = view
                ? new LiveScoreProjector(new QuantizationGrid(new SampleRate(SampleRateHz), new Tempo(tempo),
                                                               TimeSignature.FourFour, Subdivision.Sixteenth))
                : null;

            Action<NoteEvent>? onLiveNote = null;
            Action<Score>? onFinalScore = null;
            if (server is not null && projector is not null)
            {
                server.Start();
                Console.WriteLine($"Live notation view: {server.BaseUrl}");
                TryOpenBrowser(server.BaseUrl);

                LiveNotationServer liveServer = server;
                LiveScoreProjector liveProjector = projector;
                onLiveNote = n => liveServer.PublishScore(liveProjector.Add(n));
                onFinalScore = s => liveServer.PublishScore(s);
            }

            // R10.3: listen now emits score.musicxml on stop, alongside raw.mid/score.mid.
            var listen = new ListenCommand(session, midiWriter, midiWriter, Console.WriteLine,
                                            musicXmlWriter: new MusicXmlScoreWriter(),
                                            onLiveNote: onLiveNote, onFinalScore: onFinalScore);

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; micSource.Stop(); cts.Cancel(); };
            micSource.Start();
            listen.Run(micSource, tempo, outDir, cts.Token);

            if (server is not null)
                Thread.Sleep(TimeSpan.FromSeconds(1)); // let the final (accurate) SSE push reach the browser
            return 0;
        }
    default:
        return Usage();
}

static int Usage()
{
    Console.Error.WriteLine("usage: claudio <transcribe|listen|render|play> ...");
    Console.Error.WriteLine("  transcribe <in.wav> --tempo <bpm> [--out-dir <dir>]   -> raw.mid, score.mid, score.musicxml");
    Console.Error.WriteLine("  listen --tempo <bpm> [--out-dir <dir>] [--view]       -> live; raw.mid, score.mid, score.musicxml on Ctrl+C; --view opens a browser sheet-music view");
    Console.Error.WriteLine("  render|play <in.mid> [<out.wav>] [--soundfont <path>]");
    return 1;
}

static string? TryReadOption(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

// Best-effort cross-platform default-browser open (Phase-2 §8 item 3). Never throws: the URL is
// already printed above, so the user can always open it by hand if this fails.
static void TryOpenBrowser(string url)
{
    try
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("cmd", $"/c start \"\" \"{url}\"") { CreateNoWindow = true });
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", url);
        else if (OperatingSystem.IsLinux())
            Process.Start("xdg-open", url);
    }
    catch
    {
        // best effort only; the URL was already printed above for the user to open by hand.
    }
}
```

**Verify:**

```bash
dotnet build
dotnet test --filter "Category=Fast"
```

Expected: clean build (warnings-as-errors), full fast suite green — this
change adds no automated coverage of its own (see the note above) but must
not regress anything.

**Commit** (fresh IDs from `but status -fv`; @gitbutler skill):

```bash
dotnet format
but status -fv
but commit live-notation-view \
  -m "feat(cli): listen --view wiring (live notation server + browser open)" \
  --changes <ids> --status-after
```

---

### Task 6: Manual acceptance script (documented here; not automated)

No audio device and no browser exist in CI/sandbox — the same reason Step
10's mic path and Step 11's MuseScore load are manual, documented checks
rather than automated tests. Run this for real, on a machine with a working
microphone and default browser, then record the result in `DECISIONS.md`
under the "Live notation view" heading Task 1 started, the same way the
R11.2 MuseScore check is recorded there.

1. `dotnet run --project src/AudioClaudio.Cli -- listen --tempo 100 --view`
2. Confirm the console prints a `Live notation view: http://localhost:<port>/`
   line and the default browser opens to that URL automatically.
3. Confirm the page loads with an empty (or near-empty) staff and no
   JavaScript console errors in the browser's devtools.
4. Play a short, simple phrase (e.g. a five-note major-scale fragment) on
   the microphone at the declared tempo.
5. Confirm notes appear on the rendered staff shortly after each is played
   — not batched at the end. This is the one thing only a real browser can
   prove (the design doc's Testing section calls this out explicitly).
6. Open a **second** browser tab to the same URL mid-session; confirm it
   immediately shows the *current* sheet, not a blank page — the late-join
   guarantee, now seen with a real browser rather than `HttpClient`.
7. Press Ctrl+C. Confirm: the console prints the same `Wrote raw.mid...` /
   `score.mid` / `score.musicxml` lines plain `listen` already prints today;
   **both** open browser tabs' sheets update to the final, accurate score
   (durations may visibly change from their live/provisional values — this
   is expected, see the design doc's "Live view is approximate" decision);
   `raw.mid`, `score.mid`, and `score.musicxml` exist in the output
   directory exactly as plain `listen` (without `--view`) already produces
   them.
8. Run `dotnet run --project src/AudioClaudio.Cli -- listen --tempo 100`
   (**without** `--view`) once more and confirm behavior is unchanged from
   before this plan: no browser opens, no port is bound, console output is
   identical. (This is the regression check for LV1 — "zero change to
   `listen` when `--view` is absent.")

Record the result — pass/fail, and any deviation observed — as a new
`DECISIONS.md` entry under "Live notation view," in the same shape as the
R11.2 MuseScore record:

```markdown
- Manual acceptance (Task 6): `listen --tempo 100 --view` opens the browser,
  renders notes live as played, a second tab late-joins to the current
  sheet, and both tabs snap to the accurate final score on Ctrl+C; plain
  `listen` (no `--view`) is unchanged. Checked on <OS/browser> (checked
  <date>).
```

---

## Verify (plan exit criteria)

- [ ] **LV2:** `LiveScoreProjector`'s i-th live `Score` contains exactly the
      first i+1 notes, quantized identically to a direct `Quantizer.Quantize`
      call, for both a concrete example and a 200-case CsCheck property;
      past `Score`s are never mutated by later `Add` calls.
- [ ] **LV3/LV4:** `LiveNotationServer` serves `/` and the bundle path with
      the right bytes/content-type; unknown paths 404; `/events` delivers
      base64 MusicXML that decodes to `MusicXmlScoreWriter.WriteToString`
      for the published `Score`; a late joiner gets the current score
      without a fresh publish; multiple simultaneous clients all receive a
      publish; two server instances never collide on the same
      auto-assigned port.
- [ ] **LV5:** `ListenCommand`'s two new hooks fire correctly when supplied
      (once per live note; once with the accurate batch `Score`) and are
      safely absent (null, no error) when not — the two pre-existing
      `ListenCommandTests` are untouched and still green.
- [ ] **LV6:** the vendored OSMD bundle and its BSD-3-Clause license file
      are committed and sanity-checked; `DECISIONS.md` records
      source/version/size/hash/license; no `<PackageReference>` was added
      to any `.csproj`.
- [ ] `index.html`/`app.js` reference the right paths (`/events`, the OSMD
      bundle) and call `osmd.load(...).then(() => osmd.render())`.
- [ ] **LV1:** `claudio listen --tempo <bpm>` (no `--view`) is behaviorally
      unchanged (Task 6 step 8).
- [ ] `claudio listen --tempo <bpm> --view` opens a browser tab that renders
      the growing score live and snaps to the accurate final score on
      Ctrl+C (Task 6, manual).

## Definition of Done

- [ ] `dotnet build` succeeds with warnings-as-errors (Step 0 setting) clean.
- [ ] `dotnet format` reports no changes.
- [ ] All new tests green: `dotnet test --filter "FullyQualifiedName~LiveView"`,
      `dotnet test --filter "FullyQualifiedName~LiveScoreProjector"`,
      `dotnet test --filter "FullyQualifiedName~ListenCommandTests"`; the
      full fast suite stays green: `dotnet test --filter "Category=Fast"`.
- [ ] Dependency rule intact: `LiveScoreProjector` lives in
      `AudioClaudio.Application.UseCases` and imports only Domain types (plus
      its own project's `Quantizer`/`QuantizationGrid`); `LiveNotationServer`/
      `FreeTcpPort` live in `AudioClaudio.Infrastructure.LiveView` and import
      only Domain + the sibling `AudioClaudio.Infrastructure.MusicXml`
      namespace; Domain gained no reference; no new `<PackageReference>` in
      any `.csproj` — confirm via `but diff` on the three touched `.csproj`
      files (only `AudioClaudio.Cli.csproj` changes, and only to add the
      `wwwroot` `<Content>` glob).
- [ ] `ListenCommand`'s two new constructor parameters are optional and
      default to `null`; every pre-existing call site (both in
      `ListenCommandTests.cs` and in `Program.cs` before this plan) continues
      to compile and behave identically.
- [ ] `claudio listen` (no `--view`) is unchanged: no port bound, no browser
      opened, identical console output and output files (Task 6 manual
      regression check).
- [ ] `DECISIONS.md` carries the OSMD vendoring entry (version, source URL,
      path, size, SHA-256, license) and the Task 6 manual-acceptance record,
      in the same shape as the Step 8 SoundFont / Step 11 MuseScore entries.
- [ ] Committed via GitButler on branch `live-notation-view`, one commit per
      task/part above (Task 1; Task 2; Task 3 Part A; Task 3 Part B; Task 4;
      Task 5 Part A; Task 5 Part B) — no raw `git` commands used anywhere.
- [ ] Requirement-coverage table (LV1–LV8) fully satisfied.
