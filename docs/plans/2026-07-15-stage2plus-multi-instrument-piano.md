# Stage 2+ — Multi-instrument → "all notes on piano" Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans / subagent-driven-development.
> **Commit discipline:** GitButler only (`but commit <branch> --changes <ids>`), never raw `git`.

> **STATUS: COMPLETE (2026-07-15), on branch `stage-1-source-separation` (not yet on `main`).** All stages
> shipped: Stage 2 (`MultiStemTranscriber` + `MultiStemRouting`), Stage 3 (`MultiTrackMidiWriter` + the
> grand-staff merge), the batch `claudio pianize` verb, the Stage-5 feasibility spike (`SeparationLiveSpike`
> — proved the naive whole-buffer live path degrades: 4.6/8.3/13.8 s per tick at 5/15/30 s), and the
> `listen --separate` live prototype (bounded ~7 s window tick + full-quality capture-then-pianize on Stop).
> **One deviation from §0 as written:** the live seam did NOT stay a single `BuildGrandStaff` insertion —
> the separated path is a sibling method `LivePolyphonicView.BuildSeparatedGrandStaff` (bounded window) with
> the final save routed through `PianizeCommand.PianizeSource`, so the non-separated path is provably
> untouched. Full suite green (Fast 728/728; the new [Slow] separated + buffer tests pass). Open human gate:
> does a real recording's piano `recreation.wav` render a recognizable tune. See `DECISIONS.md`
> "Multi-instrument -> piano" + "Live-separated `listen --separate` prototype".

**Goal:** Turn a multi-instrument recording (`.wav` or live mic) into (a) a faithful **multi-track MIDI** (one track per instrument) and (b) an **"all notes on piano"** rendering — `score.mid` + `score.musicxml` (every note, on a grand staff) + `recreation.wav` (rendered on piano). *Not* a playable reduction (that's the still-deferred Stage 4).

**Architecture:** Bolt onto the completed Stage 1 separator. A separated stem is just an `IAudioSource`, so each pitched stem routes through the **existing** `ITranscriber` (Transkun for piano, Basic Pitch for the rest); the merged notes feed the **existing** polyphonic grand-staff pipeline + MeltySynth. Almost entirely reuse — the only genuinely new pieces are a multi-track MIDI writer and the per-stem routing/merge glue.

**Tech:** .NET 10; existing `BasicPitchTranscriber`/`TranskunTranscriber`, `PolyphonicQuantizer`, `GrandStaffMusicXmlWriter`, `GrandStaffFlattener`, `DryWetMidiWriter`, `MeltySynthSynthesizer`, `LivePolyphonicView`; DryWetMIDI (multi-track). No new NuGet.

**Scope decisions (Cornelius, 2026-07-15 — see `DECISIONS.md` "Multi-instrument → piano"):** drop drums; vocals default-dropped from the piano mix but always a track in the multi-track MIDI, `--include-vocals` folds them in; **Transkun on the `piano` stem, Basic Pitch on `bass`/`other`/`vocals`**; mic input via a live prototype (gated on a spike); output is *dense/faithful*, not playable.

---

## 0. Verified reuse seams (from grounding wf_eab151eb; consume these — do not re-derive)

**Merge → grand-staff piano `score.mid` + `score.musicxml`** (given `IReadOnlyList<NoteEvent> events` at `SampleRate rate`, `Tempo tempo`, int `fifths`):
```csharp
var subdivision = wantTriplets ? Subdivision.Twelfth : Subdivision.Sixteenth;
var grid = new QuantizationGrid(rate, tempo, TimeSignature.FourFour, subdivision);
var chordWindow = new SampleDuration(rate.Hz / 20, rate);                 // ~50 ms
GrandStaffScore gs = PolyphonicQuantizer.Quantize(events, grid, chordWindow);
new GrandStaffMusicXmlWriter(includeNoteNames, fifths: fifths).Write(gs, mxStream);   // score.musicxml
var flat = GrandStaffFlattener.ToNoteEvents(gs, grid);
new DryWetMidiWriter().Write(flat, tempo, midStream);                     // score.mid (INoteEventWriter overload)
```
**Render merged notes on piano → `recreation.wav`:** `RenderCommand.RenderToWav(merged, new MeltySynthSynthesizer(SoundFontLocator.Resolve(null)), rate, path)` — internally `synth.Render(notes, rate)` (program 0 = piano) → `WavFileWriter.Write`. **Precondition:** every `NoteEvent.Onset.Rate.Hz == rate.Hz`, else `Render` throws.
**Per-stem transcription:** `new BasicPitchTranscriber(ModelLocator.Resolve(null), decoderOpts, tempo).Transcribe(stem) -> TranscriptionResult.RawEvents` (at `BasicPitchModel.SampleRateHz` = 22050); `new TranskunTranscriber(TranskunModelLocator.Resolve(), new Radix2Fft()).TranscribeDetailed(stem) -> (notes, pedal)`. **Both emit at their own rate → must rescale to a common rate before merging** (reuse/lift `LivePolyphonicView.RescaleNotes(notes, targetRate)`).
**Multi-track MIDI (NEW — nothing writes multi-track today):** DryWetMIDI `new MidiFile(chunk1, chunk2, …)`; each `chunk = new List<ITimedObject>{ new TimedEvent(new SequenceTrackNameEvent(name),0), new TimedEvent(new ProgramChangeEvent((SevenBitNumber)gmProgram),0), …notes }.ToTrackChunk()`. Reuse `DryWetMidiWriter`'s existing `SamplesToTicks(samples, rate, bpm) = samples*PPQN*bpm/(60*rate.Hz)` + `Note` emission — factor them out or mirror them.
**Live seam:** `LivePolyphonicView.BuildGrandStaff(IReadOnlyList<Frame> frames, out IReadOnlyList<NoteEvent> rawEvents)` (LivePolyphonicView.cs:273-290) is the SINGLE place to insert separation; the tick loop (`TranscribeLoopAsync`, ~1.64 s), the accumulator (`FrameAccumulator.Snapshot()`), the SSE publish (`LiveNotationServer.PublishScoreXml`), and the final save all depend only on its `(GrandStaffScore?, rawEvents)` out-contract.

---

## Stage 2 — Per-stem transcription (route each stem → notes)

**New:** `src/AudioClaudio.Cli/Commands/MultiStemTranscriber.cs` (or Application use-case) — takes the separator's `IReadOnlyList<SeparatedStem>` + the config, returns per-stem tagged, rate-reconciled note lists.

**Task 2.a — the `RescaleNotes` helper is shared, not private.** It currently lives on `LivePolyphonicView`. Lift it to a reusable spot (`AudioClaudio.Domain` — a pure `NoteEvent[]` × `SampleRate` → `NoteEvent[]` rescale) so both the live path and the batch path use one copy. **Test:** rescaling 22050→44100 doubles sample positions/durations, preserves pitch/velocity, round-trips.

**Task 2.b — `MultiStemTranscriber`.** For each stem, by name:
- `drums` → **skip** (dropped).
- `piano` → **Transkun** (`TranskunTranscriber.TranscribeDetailed`).
- `bass`, `other` → **Basic Pitch**.
- `vocals` → **Basic Pitch**, but tag it so the caller can include/exclude it (always transcribed).
Rescale every stem's notes to a **common rate (44100 Hz)** and return `IReadOnlyList<(string Stem, string Gm, IReadOnlyList<NoteEvent> Notes)>` (Gm = the stem's GM program for the multi-track MIDI: bass 32, piano 0, other→e.g. 26 jazz-guitar or 0, vocals→e.g. 54 voice-oohs). Construct each transcriber **once**; dispose. **Tests:** on the committed `golden/test_input_mono.wav` (or a small synthetic mix), assert drums are absent, piano used Transkun (assert against a known signature or that it ran), all returned notes are at 44100, vocals present-but-tagged.

*(This is mostly wiring — the transcribers are proven. Keep it thin; the real content is the routing table + rate reconciliation.)*

---

## Stage 3 — Multi-track MIDI writer

**New:** `src/AudioClaudio.Infrastructure/Midi/MultiTrackMidiWriter.cs` — `void Write(IReadOnlyList<(string Name, int GmProgram, IReadOnlyList<NoteEvent> Notes)> tracks, Tempo tempo, Stream destination)`.
- Build one `TrackChunk` per track: a `SequenceTrackNameEvent(name)` + `ProgramChangeEvent(gm)` at tick 0, then the notes (reuse the exact `SamplesToTicks` + `Note` emission from `DryWetMidiWriter` — factor the shared tick math into an internal helper both writers call, DRY). Pass all chunks to `new MidiFile(chunks…){ TimeDivision = TicksPerQuarterNote }`.
- Include the **vocals** track (Stage-2 always transcribes it) so the multi-track MIDI is complete regardless of `--include-vocals`.
**Tests:** write a 3-track fixture → re-read with `MidiFileReader` (or DryWetMIDI) → assert 3 track chunks, correct track names + program-change events, and notes round-trip to the same pitches/onsets (±1 tick). A track's notes match the input.

---

## Stage 4 (of this plan) — Merge-to-piano + the batch command

**New:** `src/AudioClaudio.Cli/Commands/PianizeCommand.cs` (working name — rename freely) + register `pianize <mix.wav>` in `AppBuilder.Build`.
Options: `--out-dir` (default "out"), `--model` (separator dir), `--tempo` (declared; else estimate via `TempoEstimator` on the merged onsets), `--key` (declared; else `KeyDetector.Detect`), `--include-vocals`, `--note-names`, `--triplets`, `--soundfont`.
Pipeline:
1. `WavAudioSource.FromFile` → `SpleeterSourceSeparator.Separate` → stems (Stage 1).
2. `MultiStemTranscriber` → per-stem tagged notes @44100 (Stage 2).
3. `MultiTrackMidiWriter.Write(all tracks incl. vocals)` → `multitrack.mid` (Stage 3).
4. **Merge for piano:** concat the notes of the *included* stems (exclude `vocals` unless `--include-vocals`; drums already gone) → the §0 grand-staff sequence → `score.mid` + `score.musicxml` (all notes, piano grand staff).
5. **Render on piano:** `RenderCommand.RenderToWav(merged, pianoSynth, rate44100, "recreation.wav")`.
6. Also write the 5 stem WAVs (reuse Stage 1's writer) so the intermediate stems are inspectable.
Root-only output (mirror `transcribe`/`separate`).
**Tests:** end-to-end `[Slow]` on the golden WAV → asserts `multitrack.mid` (N tracks), `score.mid`, `score.musicxml` (valid, non-empty), `recreation.wav` (readable), `--include-vocals` adds the vocal notes to the piano score (more notes than without). CLI-help goldens updated.

---

## Stage 5 (of this plan) — Live: feasibility spike, THEN the prototype

**Task 5.a — feasibility spike (gate).** A `[Trait("Category","Spike")]` (CI-excluded) measurement: on a growing synthetic buffer (5 s, 15 s, 30 s), time `separate + transcribe-each-stem` per tick. Report the per-tick wall-clock vs the 1.64 s budget and the length at which it exceeds real-time. **This sets the honest cadence/length limit before any production code** (same discipline as the existing live-poly spike, `DECISIONS.md` "Live polyphonic capture"). If it's hopeless even for short takes, STOP and reconsider (e.g. separate less often than transcribe).

**Task 5.b — the live prototype** (only if 5.a is acceptable). Modify the SINGLE seam `LivePolyphonicView.BuildGrandStaff`: behind a new `listen` flag (`--separate`, or a `LivePolyphonicView` ctor option), separate the `mono` buffer → transcribe each stem (same routing as Stage 2) → rescale + merge → the existing `PolyphonicQuantizer.Quantize`. Everything downstream (publish, final save) is unchanged. On Stop, also write `multitrack.mid` (Stage 3) alongside the existing outputs. **Honestly flagged prototype** (non-scaling, near-real-time, short-takes, compounded error) in `DECISIONS.md` + the class doc; earns no guarantee. Reuse the browser view as-is (it renders whatever grand-staff MusicXML arrives).
**Tests:** the seam is heavy (mic + 5 ONNX); test what's testable without a device — the `BuildGrandStaff`-equivalent separated path on a fixture buffer produces a merged grand staff; manual acceptance for the live device path (documented, like the existing live view).

---

## Verify & stop
1. `dotnet build`/`dotnet format` clean; full suite green.
2. Domain stays BCL-only (§3).
3. `pianize <mix.wav>` produces multitrack.mid + score.mid + score.musicxml + recreation.wav; the piano audio contains all included instruments' notes.
4. **Human gate:** does the piano `recreation.wav` recognizably render the tune? (subjective; the output is a dense faithful sketch, not a clean arrangement — expected).
5. Update `CLAUDE.md` §7 + `docs/plans/README.md`.

## Honest limitations (carried forward)
- **Dense, not playable** — all notes on one keyboard exceeds two hands. Playable reduction = deferred Stage 4.
- **Compounded error** — separation × per-stem transcription; a faithful *sketch*. Ranked *statistical*, never "proven."
- **Live = prototype** — non-scaling, short-takes, near-real-time; no guarantee.
- **Vocals include/exclude is declared** (`--include-vocals`), never auto-classified (scat-vs-lyrics isn't reliably detectable).
- **Sample-rate discipline** — all merged notes reconciled to 44100 before quantize/render (§0 precondition).
