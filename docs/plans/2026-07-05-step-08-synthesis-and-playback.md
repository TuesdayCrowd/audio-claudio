# Step 8 — Synthesis and Playback (MeltySynth) — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Build-spec step:** Section 6 Step 8 (R8.1, R8.2, R8.3)
**Goal:** Turn `[NoteEvent]` back into sound — an `ISynthesizer` port that renders note events to deterministic mono PCM via MeltySynth and the committed SoundFont, plus CLI `render` (WAV out) and `play` (PortAudio out) commands.
**Architecture:** The `ISynthesizer` port lives in **Application** (`AudioClaudio.Application.Ports`); its MeltySynth adapter, the production WAV writer, and the PortAudio player live in **Infrastructure**; the `render`/`play` commands and all wiring live in **Cli** (the composition root). The Domain is untouched — synthesis is an outward adapter over the domain's `NoteEvent` currency, so no inward dependency is introduced. This adapter is also the **test oracle for Step 9's closed loop**, which is why determinism (R8.2) is load-bearing, not cosmetic.
**Tech Stack:** MeltySynth (pure-C# SoundFont synth, MIT), PortAudioSharp2 (device output, MIT, bundles native PortAudio), a hand-rolled 16-bit PCM WAV writer, xUnit + CsCheck (already present from Step 1), a committed GeneralUser GS SoundFont fixture.
**Prerequisites:** Steps **0, 1, 7** green and committed (Section 1 rule 3). Step 0 gives the four-project solution and CI; Step 1 gives `Pitch`, `SampleRate`, `SamplePosition`, `SampleDuration`, `NoteEvent`; Step 7 gives the DryWetMIDI reader `MidiFileReader.ReadFile(string path, SampleRate rate) -> MidiReadResult` (namespace `AudioClaudio.Infrastructure.Midi`) used by the CLI to load a `.mid` into `[NoteEvent]` (via `.Events`), and the writer used once to mint the `two-bar.mid` fixture.
**Commit (spec):** `feat(infra): MeltySynth rendering and playback`

---

**No design-decision gate for this step.** Section 6 Step 8 lists no *Design decision*, so there is no Cornelius gate here. Three third-party assets are nonetheless introduced and **each SHALL be recorded in `DECISIONS.md`** (Section 1 rule 7): the MeltySynth package (MIT), the PortAudioSharp2 package (MIT, native PortAudio also MIT), and the SoundFont data asset (a freely-licensed GM piano — the stack table names GeneralUser GS; its license is permissive and non-copyleft, and it is a *data* fixture, not a code dependency). Pin exact versions and the SoundFont's SHA-256 there so renders stay reproducible.

## Approach

MeltySynth's natural input is a MIDI file, but the port speaks the domain's currency — `NoteEvent`s carrying integer sample positions — so the adapter **drives the synth directly** rather than round-tripping through MIDI ticks. The algorithm is a sample-accurate event scheduler:

1. Expand each `NoteEvent` into two scheduled events: a **note-on** at `Onset.Samples` and a **note-off** at `Onset.Samples + Duration.Samples`, tagged with the pitch's MIDI number and velocity.
2. Sort the events by sample position, with **defined tie-breaks** (note-offs before note-ons at the same instant, then by key) so a re-struck same pitch retriggers cleanly and the ordering is deterministic (non-negotiable 3 — no incidental tie-breaks).
3. Walk a sample cursor through the timeline. Between consecutive events, ask MeltySynth to render exactly `nextEventSample − cursor` samples into the output buffer at the cursor; then apply the `NoteOn`/`NoteOff`. After the last event, render a fixed **release tail** so the piano's decay is captured rather than clipped.
4. Downmix MeltySynth's stereo output to mono by averaging the two channels — the domain and the whole pipeline are mono (Section 2, MVP scope).

Determinism (R8.2) falls out because MeltySynth is pure, allocation-stable DSP with no randomness, the SoundFont bytes are pinned, and reverb/chorus is disabled (a tighter, more version-stable render). We **load the SoundFont once in the constructor** and spin up a fresh `Synthesizer` per `Render` call — SoundFont loading is the expensive part, and Step 9 will call `Render` thousands of times. The golden test hashes the WAV bytes of a fixed two-bar melody; because synthesis is float DSP, that SHA-256 is pinned to the CI runner's build (see the cross-architecture caveat in Task 5 and `DECISIONS.md`).

## Requirements coverage

| Requirement | Task(s) | Proven by (test) |
|---|---|---|
| **R8.1** `ISynthesizer` port renders `[NoteEvent]` to PCM via a MeltySynth adapter with the committed SoundFont | Task 1 (SoundFont + package), Task 2 (port + adapter) | `SoundFontFixtureTests.Committed_soundfont_loads_and_contains_presets`; `MeltySynthSynthesizerTests.Render_produces_expected_length_including_release_tail`; `MeltySynthSynthesizerTests.Render_is_silent_before_onset_and_energetic_during_note` |
| **R8.2** Rendering is deterministic: same input, same samples | Task 3 (property), Task 5 (golden) | `SynthesisDeterminismTests.Rendering_the_same_notes_twice_yields_bit_identical_samples`; `GoldenRenderTests.Two_bar_melody_renders_to_the_committed_sha256` |
| **R8.3** CLI `play` (PortAudio out) and `render` (deterministic WAV) commands | Task 4 (WAV writer), Task 6 (`render`), Task 7 (`play` + PortAudio) | `WavFileWriterTests.Writes_a_valid_16bit_pcm_wav_header_and_samples`; `RenderCommandTests.Render_command_writes_a_wav_matching_the_golden_hash`; manual by-ear acceptance for `play` (documented) |

---

## Task 1: MeltySynth package, committed SoundFont fixture, and a path helper

Bring in MeltySynth, commit the SoundFont data asset with its license, and add a test helper that resolves the repo `fixtures/` directory from the test output folder. Use @superpowers:test-driven-development — the red here is "the SoundFont cannot be loaded."

**Files:**
- Modify: `src/AudioClaudio.Infrastructure/AudioClaudio.Infrastructure.csproj` (add MeltySynth `PackageReference`)
- Create: `fixtures/soundfont/GeneralUser-GS.sf2` (downloaded binary asset)
- Create: `fixtures/soundfont/LICENSE-GeneralUserGS.txt` (the SoundFont's license text)
- Modify: `DECISIONS.md` (record MeltySynth version + license, SoundFont version + license + SHA-256)
- Create: `tests/AudioClaudio.Tests/TestSupport/TestPaths.cs`
- Test: `tests/AudioClaudio.Tests/Synthesis/SoundFontFixtureTests.cs`

**Step 1 — Write the failing test:**

`tests/AudioClaudio.Tests/TestSupport/TestPaths.cs`:

```csharp
using System;
using System.IO;

namespace AudioClaudio.Tests.TestSupport;

/// <summary>
/// Resolves committed test assets relative to the repository root (the directory
/// containing AudioClaudio.sln), found by walking up from the test output folder.
/// </summary>
public static class TestPaths
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string FixturesDirectory => Path.Combine(RepositoryRoot, "fixtures");
    public static string SoundFontPath => Path.Combine(FixturesDirectory, "soundfont", "GeneralUser-GS.sf2");
    public static string GoldenDirectory => Path.Combine(FixturesDirectory, "golden");

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AudioClaudio.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate AudioClaudio.sln above the test output directory.");
    }
}
```

`tests/AudioClaudio.Tests/Synthesis/SoundFontFixtureTests.cs`:

```csharp
using AudioClaudio.Tests.TestSupport;
using MeltySynth;
using Xunit;

namespace AudioClaudio.Tests.Synthesis;

public class SoundFontFixtureTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Committed_soundfont_loads_and_contains_presets()
    {
        var soundFont = new SoundFont(TestPaths.SoundFontPath);

        Assert.NotEmpty(soundFont.Presets);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~SoundFontFixtureTests"
```

Expected FAILURE: first a **compile error** — `MeltySynth` is not a known namespace (package not yet referenced). Once the package is added but before the `.sf2` is committed, it fails at runtime with `FileNotFoundException` / `InvalidOperationException` from `TestPaths` because the SoundFont fixture is absent. Both are the intended red.

**Step 3 — Minimal implementation:**

Add the package to Infrastructure (confirm the latest permissively-licensed version at execution time and pin it):

```xml
<ItemGroup>
  <PackageReference Include="MeltySynth" Version="2.4.1" />
</ItemGroup>
```

Download and commit the SoundFont data asset and its license:

```bash
# GeneralUser GS — a freely-licensed General MIDI SoundFont (S. Christian Collins).
# Download the current release, then place the .sf2 and its license text under fixtures/.
mkdir -p fixtures/soundfont
# Download GeneralUser GS (see https://schristiancollins.com/generaluser.php),
# copy the piano-capable .sf2 to the path below, and copy its bundled license text:
cp /path/to/downloaded/GeneralUser-GS.sf2 fixtures/soundfont/GeneralUser-GS.sf2
cp /path/to/downloaded/LICENSE.txt        fixtures/soundfont/LICENSE-GeneralUserGS.txt

# Pin the exact bytes so renders stay reproducible; record this hash in DECISIONS.md.
shasum -a 256 fixtures/soundfont/GeneralUser-GS.sf2
```

Record the assets in `DECISIONS.md` (create the file's package-license section if this is the first entry):

```markdown
## NuGet / asset licenses

- **MeltySynth 2.4.1** — MIT. Pure-C# SoundFont synthesizer, no native deps. Used by the ISynthesizer adapter (Step 8).
- **SoundFont: GeneralUser GS** (S. Christian Collins) — freely redistributable, permissive, non-copyleft license (license text committed at `fixtures/soundfont/LICENSE-GeneralUserGS.txt`). Data fixture, not a code dependency. File SHA-256: `<paste from shasum above>`. Chosen per the Section 3 pinned-stack "freely-licensed GM piano" row.
```

> Substitution note: a smaller piano-only permissively-licensed `.sf2` is an acceptable swap — record the exact file, license, and SHA-256 in `DECISIONS.md` and re-bless the Task 5 golden. This is a data-asset choice, not a Section 1 rule 2 design gate.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~SoundFontFixtureTests"
```

Expected PASS: `Committed_soundfont_loads_and_contains_presets` is green — MeltySynth loads the committed SoundFont and it has presets.

**Step 5 — Commit:** use the @gitbutler skill. Create and mark the step branch, then commit this slice.

```bash
but branch new step-08-synthesis-playback && but mark step-08-synthesis-playback
but status -fv    # read the fresh change IDs for the files touched above
but commit step-08-synthesis-playback \
  -m "feat(infra): add MeltySynth and commit GeneralUser GS soundfont fixture" \
  --changes <ids> --status-after
```

---

## Task 2: The `ISynthesizer` port and the MeltySynth adapter

Define the output port in Application and implement the sample-accurate MeltySynth adapter in Infrastructure. TDD the adapter's rendering shape: correct length (note span + release tail) and correct energy (silent before onset, audible during the note).

**Files:**
- Create: `src/AudioClaudio.Application/Ports/ISynthesizer.cs`
- Create: `src/AudioClaudio.Infrastructure/Synthesis/MeltySynthSynthesizer.cs`
- Test: `tests/AudioClaudio.Tests/Synthesis/MeltySynthSynthesizerTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Synthesis;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Synthesis;

public class MeltySynthSynthesizerTests
{
    private static readonly SampleRate Rate = new SampleRate(44100);

    private static MeltySynthSynthesizer NewSynth() => new(TestPaths.SoundFontPath);

    [Fact]
    [Trait("Category", "Fast")]
    public void Render_produces_expected_length_including_release_tail()
    {
        // One A4 (MIDI 69), 0.5 s long, starting at sample 0.
        var notes = new[]
        {
            new NoteEvent(
                new Pitch(69),
                new SamplePosition(0, Rate),
                new SampleDuration(22050, Rate),
                100)
        };

        float[] pcm = NewSynth().Render(notes, Rate);

        // note ends at 22050; default release tail is 1500 ms = 66150 samples @ 44.1 kHz.
        Assert.Equal(22050 + 66150, pcm.Length);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Render_is_silent_before_onset_and_energetic_during_note()
    {
        const long onset = 22050; // 0.5 s in
        var notes = new[]
        {
            new NoteEvent(
                new Pitch(69),
                new SamplePosition(onset, Rate),
                new SampleDuration(22050, Rate),
                100)
        };

        float[] pcm = NewSynth().Render(notes, Rate);

        double preRms = Rms(pcm, 0, (int)onset);
        double noteRms = Rms(pcm, (int)onset, (int)onset + 11025); // first 0.25 s of the note

        Assert.True(preRms < 1e-6, $"expected silence before onset, got RMS {preRms}");
        Assert.True(noteRms > 1e-3, $"expected an audible note, got RMS {noteRms}");
    }

    private static double Rms(float[] x, int start, int end)
    {
        double sum = 0;
        int n = 0;
        for (int i = start; i < end && i < x.Length; i++)
        {
            sum += (double)x[i] * x[i];
            n++;
        }
        return n == 0 ? 0 : Math.Sqrt(sum / n);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~MeltySynthSynthesizerTests"
```

Expected FAILURE: compile error — neither `ISynthesizer` nor `MeltySynthSynthesizer` exists yet.

**Step 3 — Minimal implementation:**

`src/AudioClaudio.Application/Ports/ISynthesizer.cs`:

```csharp
using System.Collections.Generic;
using AudioClaudio.Domain;

namespace AudioClaudio.Application.Ports;

/// <summary>
/// Output port: renders a sequence of <see cref="NoteEvent"/> to a mono PCM buffer
/// (float in [-1, 1]) at the given sample rate. Implementations SHALL be
/// deterministic (R8.2): identical input yields bit-identical output. Used as the
/// test oracle for the Step 9 closed loop.
/// </summary>
public interface ISynthesizer
{
    /// <param name="notes">Note events; their onsets/durations SHALL carry the same sample rate as <paramref name="sampleRate"/>.</param>
    /// <param name="sampleRate">Render sample rate in Hz.</param>
    /// <returns>Mono PCM samples, float in [-1, 1], covering the last note's end plus a release tail.</returns>
    float[] Render(IReadOnlyList<NoteEvent> notes, SampleRate sampleRate);
}
```

`src/AudioClaudio.Infrastructure/Synthesis/MeltySynthSynthesizer.cs`:

```csharp
using System;
using System.Collections.Generic;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using MeltySynth;

namespace AudioClaudio.Infrastructure.Synthesis;

/// <summary>
/// Renders NoteEvent sequences to mono PCM using MeltySynth and a committed SoundFont.
/// Deterministic (R8.2): the SoundFont is loaded once; a fresh Synthesizer is created
/// per render; reverb/chorus is disabled for a tight, version-stable render; and note
/// scheduling uses defined tie-breaks. See Step 8 §Approach.
/// </summary>
public sealed class MeltySynthSynthesizer : ISynthesizer
{
    private const int Channel = 0;
    private const int ProgramChangeCommand = 0xC0;

    private readonly SoundFont _soundFont;
    private readonly int _midiProgram;
    private readonly int _releaseTailMilliseconds;

    /// <param name="soundFontPath">Path to the committed .sf2.</param>
    /// <param name="midiProgram">GM program number; 0 = Acoustic Grand Piano.</param>
    /// <param name="releaseTailMilliseconds">Silence/decay rendered after the last note-off.</param>
    public MeltySynthSynthesizer(string soundFontPath, int midiProgram = 0, int releaseTailMilliseconds = 1500)
    {
        _soundFont = new SoundFont(soundFontPath);
        _midiProgram = midiProgram;
        _releaseTailMilliseconds = releaseTailMilliseconds;
    }

    public float[] Render(IReadOnlyList<NoteEvent> notes, SampleRate sampleRate)
    {
        var settings = new SynthesizerSettings(sampleRate.Hz) { EnableReverbAndChorus = false };
        var synth = new Synthesizer(_soundFont, settings);
        synth.ProcessMidiMessage(Channel, ProgramChangeCommand, _midiProgram, 0); // select the piano program

        // Expand notes into a sample-sorted schedule of note-on / note-off events.
        var events = new List<ScheduledEvent>(notes.Count * 2);
        long lastEnd = 0;
        foreach (var n in notes)
        {
            if (n.Onset.Rate.Hz != sampleRate.Hz)
                throw new ArgumentException(
                    $"NoteEvent sample rate {n.Onset.Rate.Hz} Hz does not match render rate {sampleRate.Hz} Hz.",
                    nameof(notes));

            long on = n.Onset.Samples;
            long off = on + n.Duration.Samples;
            events.Add(new ScheduledEvent(on, IsOn: true, n.Pitch.MidiNumber, n.Velocity));
            events.Add(new ScheduledEvent(off, IsOn: false, n.Pitch.MidiNumber, n.Velocity));
            if (off > lastEnd) lastEnd = off;
        }
        events.Sort(ScheduledEvent.Compare); // defined, deterministic ordering (non-negotiable 3)

        long tailSamples = (long)_releaseTailMilliseconds * sampleRate.Hz / 1000L;
        int total = checked((int)(lastEnd + tailSamples));

        var left = new float[total];
        var right = new float[total];

        int cursor = 0;
        foreach (var ev in events)
        {
            int target = (int)Math.Min(ev.Sample, total);
            int count = target - cursor;
            if (count > 0)
            {
                synth.Render(left.AsSpan(cursor, count), right.AsSpan(cursor, count));
                cursor += count;
            }

            if (ev.IsOn) synth.NoteOn(Channel, ev.Key, ev.Velocity);
            else synth.NoteOff(Channel, ev.Key);
        }
        if (cursor < total)
            synth.Render(left.AsSpan(cursor), right.AsSpan(cursor));

        // Downmix stereo to mono — the whole pipeline is mono (Section 2, MVP scope).
        var mono = new float[total];
        for (int i = 0; i < total; i++)
            mono[i] = 0.5f * (left[i] + right[i]);
        return mono;
    }

    private readonly record struct ScheduledEvent(long Sample, bool IsOn, int Key, int Velocity)
    {
        public static int Compare(ScheduledEvent a, ScheduledEvent b)
        {
            int c = a.Sample.CompareTo(b.Sample);
            if (c != 0) return c;
            c = a.IsOn.CompareTo(b.IsOn); // false(0) < true(1): note-offs before note-ons at the same sample
            if (c != 0) return c;
            return a.Key.CompareTo(b.Key);
        }
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~MeltySynthSynthesizerTests"
```

Expected PASS: both tests green — the buffer is the note span plus the 66150-sample tail, is exactly silent before the onset, and carries real energy once the note sounds.

**Step 5 — Commit:** via the @gitbutler skill.

```bash
but status -fv
but commit step-08-synthesis-playback \
  -m "feat: ISynthesizer port and MeltySynth adapter (sample-accurate render)" \
  --changes <ids> --status-after
```

---

## Task 3: Determinism property (R8.2)

Prove "same input → same samples" over randomly generated note lists with a CsCheck property. This is the invariant the Step 9 oracle rests on. Heavy render loop → `Category=Slow` so `--filter Category=Fast` skips it.

**Files:**
- Test: `tests/AudioClaudio.Tests/Synthesis/SynthesisDeterminismTests.cs`

**Step 1 — Write the failing test:**

```csharp
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Synthesis;
using AudioClaudio.Tests.TestSupport;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.Synthesis;

public class SynthesisDeterminismTests
{
    private static readonly SampleRate Rate = new SampleRate(44100);

    [Fact]
    [Trait("Category", "Slow")]
    public void Rendering_the_same_notes_twice_yields_bit_identical_samples()
    {
        var synth = new MeltySynthSynthesizer(TestPaths.SoundFontPath);

        var genNote =
            from midi in Gen.Int[33, 96]
            from onset in Gen.Long[0, 88200]
            from duration in Gen.Long[4410, 44100]
            from velocity in Gen.Int[40, 120]
            select new NoteEvent(
                new Pitch(midi),
                new SamplePosition(onset, Rate),
                new SampleDuration(duration, Rate),
                velocity);

        Gen.List[genNote, 0, 6].Sample(notes =>
        {
            float[] a = synth.Render(notes, Rate);
            float[] b = synth.Render(notes, Rate);
            return a.AsSpan().SequenceEqual(b);
        }, iter: 50, seed: "0");
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~SynthesisDeterminismTests"
```

Expected FAILURE (as a guard first): if the adapter had any nondeterminism (e.g. a shared, un-reset `Synthesizer` leaking voice state across renders), CsCheck would find a shrunk counterexample where `a != b`. With the Task 2 implementation (fresh `Synthesizer` per call) it should already pass — to *see red first*, temporarily hoist the `Synthesizer` into a reused field and rerun; the property fails on the second render. Restore the per-call construction. (If a genuine failure appears, drive it with @superpowers:systematic-debugging.)

**Step 3 — Minimal implementation:** none beyond Task 2 — the per-call `Synthesizer` construction and pinned SoundFont already satisfy determinism. Keep the guard experiment out of the committed tree.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~SynthesisDeterminismTests"
dotnet test --filter Category=Fast    # confirm the Slow property is correctly excluded from the fast lane
```

Expected PASS: 50 random note lists each render bit-identically twice; the fast filter run does **not** execute this test.

**Step 5 — Commit:** via the @gitbutler skill.

```bash
but status -fv
but commit step-08-synthesis-playback \
  -m "test: MeltySynth render determinism property (R8.2)" \
  --changes <ids> --status-after
```

---

## Task 4: Production WAV writer (Infrastructure)

The `render` command writes a WAV; hand-roll a 16-bit PCM RIFF serializer (the repo takes no dependency for WAV — same discipline as Step 2's reader). `BinaryWriter` is always little-endian, so output is byte-stable across machines.

> Seam note: Step 2's signal generator writes WAV inside *test utilities*; this is the **production** writer that the CLI ships. The tiny duplication is deliberate — the test-utility writer must not become a runtime dependency of the CLI.

**Files:**
- Create: `src/AudioClaudio.Infrastructure/Audio/WavFileWriter.cs`
- Test: `tests/AudioClaudio.Tests/Audio/WavFileWriterTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System;
using System.Text;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Audio;
using Xunit;

namespace AudioClaudio.Tests.Audio;

public class WavFileWriterTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Writes_a_valid_16bit_pcm_wav_header_and_samples()
    {
        var rate = new SampleRate(48000);
        float[] pcm = { 0f, 1f, -1f, 0.5f };

        byte[] wav = WavFileWriter.ToBytes(pcm, rate);

        Assert.Equal(44 + pcm.Length * 2, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(16, BitConverter.ToInt32(wav, 16));      // PCM fmt chunk size
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));       // audio format = PCM
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));       // channels = mono
        Assert.Equal(48000, BitConverter.ToInt32(wav, 24));   // sample rate
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));      // bits per sample
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(pcm.Length * 2, BitConverter.ToInt32(wav, 40)); // data chunk size

        Assert.Equal(0, BitConverter.ToInt16(wav, 44));       // 0.0
        Assert.Equal(32767, BitConverter.ToInt16(wav, 46));   // +1.0 -> 32767
        Assert.Equal(-32767, BitConverter.ToInt16(wav, 48));  // -1.0 -> -32767
        Assert.Equal(16384, BitConverter.ToInt16(wav, 50));   // 0.5 -> round(16383.5) -> 16384 (to-even)
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~WavFileWriterTests"
```

Expected FAILURE: compile error — `WavFileWriter` does not exist.

**Step 3 — Minimal implementation:**

`src/AudioClaudio.Infrastructure/Audio/WavFileWriter.cs`:

```csharp
using System;
using System.IO;
using AudioClaudio.Domain;

namespace AudioClaudio.Infrastructure.Audio;

/// <summary>
/// Writes mono float PCM (values clamped to [-1, 1]) as a canonical 16-bit PCM WAV.
/// Deterministic: BinaryWriter emits little-endian on every platform, and the
/// float→short conversion is a plain clamp+round.
/// </summary>
public static class WavFileWriter
{
    public static void Write(string path, ReadOnlySpan<float> monoPcm, SampleRate sampleRate)
        => File.WriteAllBytes(path, ToBytes(monoPcm, sampleRate));

    public static byte[] ToBytes(ReadOnlySpan<float> monoPcm, SampleRate sampleRate)
    {
        const int channels = 1;
        const int bitsPerSample = 16;
        int sampleRateHz = sampleRate.Hz;
        int blockAlign = channels * bitsPerSample / 8;
        int byteRate = sampleRateHz * blockAlign;
        int dataBytes = monoPcm.Length * blockAlign;

        using var ms = new MemoryStream(44 + dataBytes);
        using var w = new BinaryWriter(ms);

        // RIFF header
        w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
        w.Write(36 + dataBytes);
        w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });

        // fmt chunk
        w.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
        w.Write(16);              // PCM fmt chunk size
        w.Write((short)1);        // audio format = PCM
        w.Write((short)channels);
        w.Write(sampleRateHz);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write((short)bitsPerSample);

        // data chunk
        w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
        w.Write(dataBytes);
        foreach (float x in monoPcm)
        {
            float clamped = Math.Clamp(x, -1f, 1f);
            int s = (int)MathF.Round(clamped * 32767f);
            w.Write((short)Math.Clamp(s, short.MinValue, short.MaxValue));
        }

        w.Flush();
        return ms.ToArray();
    }
}
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~WavFileWriterTests"
```

Expected PASS: header fields and the four decoded samples match exactly.

**Step 5 — Commit:** via the @gitbutler skill.

```bash
but status -fv
but commit step-08-synthesis-playback \
  -m "feat(infra): 16-bit PCM WAV writer" \
  --changes <ids> --status-after
```

---

## Task 5: The golden two-bar render (determinism made tangible)

Render a fixed two-bar melody, hash the WAV, and pin the SHA-256 as a committed golden (Section 5 fixture policy: reviewed once, never blindly regenerated). The melody is defined in-code so this golden does not depend on any other step's API surface.

> **Faithfulness note.** The spec's Verify says "a committed two-bar MIDI renders to a WAV whose SHA-256 matches the checked-in hash." The automated golden renders the equivalent **in-code** `TwoBarMelody` (self-contained, no Step 7 reader-name coupling). Task 6 also commits `fixtures/golden/two-bar.mid`, generated from the same melody via the Step 7 writer; by R7.2 (lossless round-trip) `claudio render fixtures/golden/two-bar.mid out.wav` yields the identical WAV, so the CLI path over the committed MIDI is verified too.

> **Cross-architecture caveat.** Synthesis is float DSP, so the golden SHA-256 is pinned to the CI runner's build. MeltySynth avoids fused-multiply-add and uses plain IEEE-754 float ops, so it should be bit-identical across x64/arm64; if a cross-arch mismatch ever surfaces, bless the hash on the CI runner, investigate with @superpowers:systematic-debugging, and record the finding in `DECISIONS.md`. R8.2's per-build determinism (Task 3) is unconditional; the checked-in hash is the CI reference.

**Files:**
- Create: `tests/AudioClaudio.Tests/TestSupport/TwoBarMelody.cs`
- Test: `tests/AudioClaudio.Tests/Synthesis/GoldenRenderTests.cs`
- Create: `fixtures/golden/two-bar.wav.sha256` (blessed once, then committed)

**Step 1 — Write the failing test:**

`tests/AudioClaudio.Tests/TestSupport/TwoBarMelody.cs`:

```csharp
using System.Collections.Generic;
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.TestSupport;

/// <summary>
/// A fixed, deterministic two-bar monophonic line (C major scale, one note per beat,
/// 4/4 at 120 BPM). Used as the synthesis golden fixture and by the CLI render path.
/// </summary>
public static class TwoBarMelody
{
    public static IReadOnlyList<NoteEvent> Notes(SampleRate rate)
    {
        int[] midi = { 60, 62, 64, 65, 67, 69, 71, 72 }; // C4..C5, all within MIDI 33..96
        const long step = 22050;    // one quarter note at 120 BPM @ 44.1 kHz
        const long noteLen = 20000; // slightly detached, leaving a gap before the next onset
        const int velocity = 100;

        var notes = new List<NoteEvent>(midi.Length);
        for (int i = 0; i < midi.Length; i++)
        {
            var onset = new SamplePosition(step * i, rate);
            var duration = new SampleDuration(noteLen, rate);
            notes.Add(new NoteEvent(new Pitch(midi[i]), onset, duration, velocity));
        }
        return notes;
    }
}
```

`tests/AudioClaudio.Tests/Synthesis/GoldenRenderTests.cs`:

```csharp
using System;
using System.IO;
using System.Security.Cryptography;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Infrastructure.Synthesis;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Synthesis;

public class GoldenRenderTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Two_bar_melody_renders_to_the_committed_sha256()
    {
        var rate = new SampleRate(44100);
        var synth = new MeltySynthSynthesizer(TestPaths.SoundFontPath);

        float[] pcm = synth.Render(TwoBarMelody.Notes(rate), rate);
        byte[] wav = WavFileWriter.ToBytes(pcm, rate);
        string actual = Convert.ToHexString(SHA256.HashData(wav)).ToLowerInvariant();

        string goldenPath = Path.Combine(TestPaths.GoldenDirectory, "two-bar.wav.sha256");

        // Deliberate, reviewed bless: run once with AUDIO_CLAUDIO_BLESS=1 to mint the hash,
        // listen to the rendered WAV to confirm it is a real piano scale, then commit.
        if (Environment.GetEnvironmentVariable("AUDIO_CLAUDIO_BLESS") == "1")
        {
            Directory.CreateDirectory(TestPaths.GoldenDirectory);
            File.WriteAllText(goldenPath, actual + "\n");
        }

        string expected = File.ReadAllText(goldenPath).Trim();
        Assert.Equal(expected, actual);
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~GoldenRenderTests"
```

Expected FAILURE: `FileNotFoundException` on `two-bar.wav.sha256` — the golden has not been blessed yet.

**Step 3 — Minimal implementation:** bless the golden once, then confirm audibly.

```bash
# Mint the golden hash from the current render:
AUDIO_CLAUDIO_BLESS=1 dotnet test --filter "FullyQualifiedName~GoldenRenderTests"

# Sanity-listen: write the same render to a WAV and play it (a C-major scale on piano).
# (Uses the render command wired in Task 6; do this after Task 6 if listening now, or
#  temporarily dump the WAV from a scratch run.) Review, then keep the committed hash.
cat fixtures/golden/two-bar.wav.sha256
```

No production code changes — the golden is a reviewed fixture.

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~GoldenRenderTests"
```

Expected PASS: the render's SHA-256 equals the committed hash. Re-running is stable (determinism).

**Step 5 — Commit:** via the @gitbutler skill.

```bash
but status -fv
but commit step-08-synthesis-playback \
  -m "test: golden two-bar render SHA-256 (determinism made tangible)" \
  --changes <ids> --status-after
```

---

## Task 6: CLI `render <in.mid> <out.wav>` command

Wire the composition root: load the `.mid` into `[NoteEvent]` (Step 7 reader), render via `ISynthesizer`, write the WAV. Keep the file-writing logic in a testable handler; the MIDI loading stays in `Program` where the Step 7 adapter is wired.

**Files:**
- Create: `src/AudioClaudio.Cli/Commands/RenderCommand.cs`
- Create: `src/AudioClaudio.Cli/Composition/SoundFontLocator.cs`
- Modify: `src/AudioClaudio.Cli/Program.cs` (command dispatch)
- Create: `fixtures/golden/two-bar.mid` (generated once via the Step 7 writer)
- Test: `tests/AudioClaudio.Tests/Cli/RenderCommandTests.cs`

**Step 1 — Write the failing test:**

```csharp
using System;
using System.IO;
using System.Security.Cryptography;
using AudioClaudio.Cli.Commands;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Synthesis;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Cli;

public class RenderCommandTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Render_command_writes_a_wav_matching_the_golden_hash()
    {
        var rate = new SampleRate(44100);
        var synth = new MeltySynthSynthesizer(TestPaths.SoundFontPath);
        string outPath = Path.Combine(Path.GetTempPath(), $"claudio-render-{Guid.NewGuid():N}.wav");

        try
        {
            RenderCommand.RenderToWav(TwoBarMelody.Notes(rate), synth, rate, outPath);

            Assert.True(File.Exists(outPath));
            string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(outPath))).ToLowerInvariant();
            string expected = File.ReadAllText(Path.Combine(TestPaths.GoldenDirectory, "two-bar.wav.sha256")).Trim();
            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }
}
```

**Step 2 — Run to verify it fails:**

```bash
dotnet test --filter "FullyQualifiedName~RenderCommandTests"
```

Expected FAILURE: compile error — `RenderCommand` does not exist.

**Step 3 — Minimal implementation:**

`src/AudioClaudio.Cli/Commands/RenderCommand.cs`:

```csharp
using System.Collections.Generic;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Audio;

namespace AudioClaudio.Cli.Commands;

public static class RenderCommand
{
    /// <summary>Renders notes to PCM via the synthesizer and writes a deterministic WAV.</summary>
    public static void RenderToWav(
        IReadOnlyList<NoteEvent> notes,
        ISynthesizer synthesizer,
        SampleRate sampleRate,
        string outputWavPath)
    {
        float[] pcm = synthesizer.Render(notes, sampleRate);
        WavFileWriter.Write(outputWavPath, pcm, sampleRate);
    }
}
```

`src/AudioClaudio.Cli/Composition/SoundFontLocator.cs`:

```csharp
using System;
using System.IO;

namespace AudioClaudio.Cli.Composition;

/// <summary>
/// Resolves the SoundFont path: an explicit --soundfont wins; otherwise walk up from
/// the executable for the repo's fixtures (works when run via `dotnet run` from the repo).
/// A shipped exe outside the repo SHALL pass --soundfont.
/// </summary>
public static class SoundFontLocator
{
    public static string Resolve(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "fixtures", "soundfont", "GeneralUser-GS.sf2");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "SoundFont not found; pass --soundfont <path> to a .sf2 file.");
    }
}
```

`src/AudioClaudio.Cli/Program.cs` (dispatch; `render` wired now, `play` added in Task 7). The `.mid` → `[NoteEvent]` load uses the **Step 7 reader** `MidiFileReader.ReadFile(string path, SampleRate rate) -> MidiReadResult`; take its `.Events` for the note list (its `.Tempo` is unused by `render`/`play`):

```csharp
using AudioClaudio.Cli.Commands;
using AudioClaudio.Cli.Composition;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi;        // Step 7 reader namespace
using AudioClaudio.Infrastructure.Synthesis;

var rate = new SampleRate(44100);

if (args.Length == 0)
    return Usage();

string? soundFontOption = TryReadOption(args, "--soundfont");
string soundFontPath = SoundFontLocator.Resolve(soundFontOption);
var synthesizer = new MeltySynthSynthesizer(soundFontPath);

switch (args[0])
{
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

static int Usage()
{
    Console.Error.WriteLine("usage: claudio <render|play> <in.mid> [<out.wav>] [--soundfont <path>]");
    return 1;
}

static string? TryReadOption(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}
```

Generate and commit the two-bar MIDI fixture (once), using the Step 7 writer from the same `TwoBarMelody` notes, so the CLI has a real input file to render/play. If the writer's API differs, adapt this one-off accordingly:

```bash
# One-off: write fixtures/golden/two-bar.mid from TwoBarMelody via the Step 7 writer,
# then confirm the CLI reproduces the golden WAV over the committed MIDI:
dotnet run --project src/AudioClaudio.Cli -- render fixtures/golden/two-bar.mid /tmp/two-bar.wav
shasum -a 256 /tmp/two-bar.wav          # must equal fixtures/golden/two-bar.wav.sha256
```

**Step 4 — Run to verify it passes:**

```bash
dotnet test --filter "FullyQualifiedName~RenderCommandTests"
```

Expected PASS: the command writes a WAV whose SHA-256 equals the committed golden.

**Step 5 — Commit:** via the @gitbutler skill.

```bash
but status -fv
but commit step-08-synthesis-playback \
  -m "feat(cli): render command writes deterministic WAV; two-bar.mid fixture" \
  --changes <ids> --status-after
```

---

## Task 7: CLI `play <file.mid>` command and the PortAudio player

First contact with the audio-device layer. `play` renders then streams to the default output device via PortAudio. Per spec, correctness is **verified by ear once** — no automated test opens a device (CI has none). Provide the adapter, wire the command, and document a manual acceptance script.

**Files:**
- Modify: `src/AudioClaudio.Infrastructure/AudioClaudio.Infrastructure.csproj` (add PortAudioSharp2)
- Modify: `DECISIONS.md` (record PortAudioSharp2 + native PortAudio license)
- Create: `src/AudioClaudio.Infrastructure/Audio/PortAudioPlayer.cs`
- Create: `src/AudioClaudio.Cli/Commands/PlayCommand.cs`
- Modify: `src/AudioClaudio.Cli/Program.cs` (add the `play` case)

**Step 1 — Write the failing test:** none automated (device-dependent). The verification is the manual acceptance script in Step 4. To keep the red-green rhythm honest, the "failing" state is: `play` is not yet dispatched, so `dotnet run -- play fixtures/golden/two-bar.mid` prints usage and exits non-zero.

```bash
dotnet run --project src/AudioClaudio.Cli -- play fixtures/golden/two-bar.mid
# Expected before implementation: prints the usage line and exits 1 (no `play` case).
```

**Step 2 — Run to verify it fails:** the command above exits non-zero with usage text.

**Step 3 — Minimal implementation:**

Add the package (confirm the current version/API and pin it):

```xml
<ItemGroup>
  <PackageReference Include="PortAudioSharp2" Version="1.0.0" />
</ItemGroup>
```

Record it in `DECISIONS.md`:

```markdown
- **PortAudioSharp2 1.0.0** — MIT. Managed binding to PortAudio (the bundled native PortAudio is also MIT/BSD-style, non-copyleft). Used by PortAudioPlayer for `play` output (Step 8); the same binding carries Step 10 live capture.
```

`src/AudioClaudio.Infrastructure/Audio/PortAudioPlayer.cs` (PortAudioSharp2 callback pattern — confirm the exact `Stream` ctor/`Callback` signatures against the installed version):

```csharp
using System;
using System.Runtime.InteropServices;
using System.Threading;
using AudioClaudio.Domain;
using PortAudioSharp;

namespace AudioClaudio.Infrastructure.Audio;

/// <summary>
/// Plays mono float PCM to the default output device via PortAudio. First contact with
/// the audio-device layer (R8.3). Correctness is verified by ear (manual acceptance) —
/// never in CI; no test opens a device. Contains no transcription logic.
/// </summary>
public sealed class PortAudioPlayer : IDisposable
{
    private bool _initialized;

    public void Play(float[] monoPcm, SampleRate sampleRate)
    {
        PortAudio.Initialize();
        _initialized = true;

        int device = PortAudio.DefaultOutputDevice;
        var outParams = new StreamParameters
        {
            device = device,
            channelCount = 1,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = PortAudio.GetDeviceInfo(device).defaultLowOutputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        int offset = 0;
        using var done = new ManualResetEventSlim(false);

        Stream.Callback callback = (IntPtr input, IntPtr output, uint frameCount,
            ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData) =>
        {
            int remaining = monoPcm.Length - offset;
            int toCopy = Math.Min(remaining, (int)frameCount);
            if (toCopy > 0)
            {
                Marshal.Copy(monoPcm, offset, output, toCopy);
                offset += toCopy;
            }
            if (toCopy < (int)frameCount)
            {
                // zero-fill the tail of the final buffer, then signal completion
                var zeros = new float[(int)frameCount - toCopy];
                Marshal.Copy(zeros, 0, IntPtr.Add(output, toCopy * sizeof(float)), zeros.Length);
                done.Set();
                return StreamCallbackResult.Complete;
            }
            return StreamCallbackResult.Continue;
        };

        using var stream = new Stream(
            inParams: null,
            outParams: outParams,
            sampleRate: sampleRate.Hz,
            framesPerBuffer: 0, // paFramesPerBufferUnspecified
            streamFlags: StreamFlags.ClipOff,
            callback: callback,
            userData: IntPtr.Zero);

        stream.Start();
        done.Wait();
        stream.Stop();
    }

    public void Dispose()
    {
        if (_initialized)
        {
            PortAudio.Terminate();
            _initialized = false;
        }
    }
}
```

`src/AudioClaudio.Cli/Commands/PlayCommand.cs`:

```csharp
using System.Collections.Generic;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Audio;

namespace AudioClaudio.Cli.Commands;

public static class PlayCommand
{
    public static void Play(IReadOnlyList<NoteEvent> notes, ISynthesizer synthesizer, SampleRate sampleRate)
    {
        float[] pcm = synthesizer.Render(notes, sampleRate);
        using var player = new PortAudioPlayer();
        player.Play(pcm, sampleRate);
    }
}
```

Add the `play` case to `Program.cs` (alongside `render`):

```csharp
    case "play" when args.Length >= 2:
    {
        IReadOnlyList<NoteEvent> notes = MidiFileReader.ReadFile(args[1], rate).Events; // Step 7 reader
        PlayCommand.Play(notes, synthesizer, rate);
        return 0;
    }
```

**Step 4 — Run to verify it passes:** build green, then the documented manual acceptance (by ear, once):

```bash
dotnet build
dotnet format --verify-no-changes
# Manual acceptance (requires an output device; run locally, not in CI):
dotnet run --project src/AudioClaudio.Cli -- play fixtures/golden/two-bar.mid
# Expected: a C-major scale (C4..C5) plays on the default audio output. Confirm by ear.
```

Expected PASS: the project builds and formats clean; `play` audibly renders the scale. The golden hash (Task 5) carries determinism thereafter, so this by-ear check is a one-time acceptance.

**Step 5 — Commit:** via the @gitbutler skill.

```bash
but status -fv
but commit step-08-synthesis-playback \
  -m "feat(infra): PortAudio playback and CLI play command" \
  --changes <ids> --status-after
```

> These per-task commits roll up to the spec message `feat(infra): MeltySynth rendering and playback`. Squash them under that message if a single step commit is preferred (see the @gitbutler skill).

---

## Verify (step exit criteria)

- [ ] **Golden (R8.2):** the committed two-bar melody renders to a WAV whose SHA-256 matches `fixtures/golden/two-bar.wav.sha256` (`GoldenRenderTests`), and the CLI `render` over `fixtures/golden/two-bar.mid` reproduces that same hash.
- [ ] **Determinism (R8.2):** rendering the same notes twice yields bit-identical samples across many random note lists (`SynthesisDeterminismTests`).
- [ ] **Rendering (R8.1):** the `ISynthesizer` MeltySynth adapter turns `[NoteEvent]` into mono PCM of the correct length (note span + release tail), silent before onset and audible during the note, using the committed SoundFont (`MeltySynthSynthesizerTests`, `SoundFontFixtureTests`).
- [ ] **CLI (R8.3):** `claudio render <in.mid> <out.wav>` writes a deterministic WAV (`RenderCommandTests`), and `claudio play <file.mid>` plays the transcription aloud through PortAudio — verified by ear once (manual acceptance script above).

## Definition of Done

- [ ] `dotnet build` succeeds.
- [ ] `dotnet format --verify-no-changes` is clean.
- [ ] All new tests green: `dotnet test`; and `dotnet test --filter Category=Fast` is green while excluding the Slow determinism property.
- [ ] Dependency rule intact: `ISynthesizer` in Application; MeltySynth adapter, WAV writer, and PortAudio player in Infrastructure; commands and wiring only in Cli; **Domain untouched** (no audio/MIDI/device type reaches Domain).
- [ ] Committed via GitButler on `step-08-synthesis-playback`, rolling up to `feat(infra): MeltySynth rendering and playback`.
- [ ] Requirement-coverage table fully satisfied (R8.1, R8.2, R8.3).
- [ ] `DECISIONS.md` updated: MeltySynth (MIT) + version, PortAudioSharp2 (MIT) + version, SoundFont (GeneralUser GS, permissive/non-copyleft) + version + SHA-256; and the synthesis-golden cross-architecture caveat noted.
```