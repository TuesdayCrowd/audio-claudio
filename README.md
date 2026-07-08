# Audio Claudio

*Audiō Claudiō* — "I hear, by means of Claude." A real-time, monophonic piano
transcriber in C# / .NET 10 (LTS), built in open collaboration with Claude
Code. Audio comes in from a microphone or a WAV file; note events come out;
notation is emitted as MusicXML; and the transcribed piece can be played back
through a synthesized piano. Public domain (`UNLICENSE`).

## What it is

Play a single melody line — into a microphone, or as a WAV file — and Audio
Claudio tells you which notes were played, when they started, and how long
they lasted, then writes the result out as MIDI and as sheet music
(MusicXML). It listens to one note at a time (see [Limitations](#limitations)
below); it detects each note's pitch and attack from the raw audio, snaps the
timing to a tempo you declare, and can sing the transcription back to you
through a synthesized piano.

It is a small, honest transcriber. Its correctness claim is not asserted — it
is *earned*, by the closed-loop test described below.

## Pipeline

```
microphone / WAV file
      │
      ▼
  audio frames ──▶ spectral analysis ──▶ pitch + onset detection
      │                                        │
      ▼                                        ▼
  (adapters)                            note events (domain)
                                               │
                        ┌──────────────────────┼──────────────────────┐
                        ▼                      ▼                      ▼
                  quantization           MIDI export           MusicXML emission
                        │                      │                      │
                        ▼                      ▼                      ▼
                  a "score"            playback (MeltySynth)    notation render
```

Audio enters only through **adapters** — a WAV-file reader, or a live
PortAudio capture. Everything from *note events* onward is pure domain logic
with no knowledge of files or devices: the microphone is just one more
adapter, and it was the last thing added to the pipeline, not the first.

## The non-negotiables

Four invariants hold everywhere in the domain, unconditionally. A change
that violates one is a bug, regardless of whether it compiles or passes some
other test:

1. **Time is integer samples.** Every position and duration in the domain is
   an integer count of samples, carried together with its declared sample
   rate. Seconds are a display conversion applied only at the edge — the
   domain itself never accumulates floating-point time.
2. **The domain never reads the wall clock.** "Now" enters the system only
   through the `IClock` port, and only in the Application/Infrastructure
   layers. Even live-capture timestamps are sample counts read from the
   audio stream, never `DateTime` reads.
3. **Determinism.** The same WAV file produces the identical sequence of
   note events on every run, on every machine, bit-for-bit. There is no
   randomness anywhere in the domain; every tie-break an algorithm makes is
   defined, not incidental.
4. **Pitch decisions are made in cents/MIDI space, not raw Hz**, so a stated
   tolerance means the same thing at the bottom of the keyboard as at the
   top. (A cent is 1/100 of a semitone.)

## The closed loop

This project contains both halves of a round trip: a transcriber (audio →
notes) and a synthesizer (notes → audio). Composed together, they check each
other, and the synthesizer becomes the **oracle** — no hand-labelled "ground
truth" audio is ever needed:

> generate a random, constrained score → **synthesize** it to audio with
> MeltySynth → **transcribe** that audio back through the whole pipeline →
> demand the original score back, within stated tolerance.

This is `transcribe ∘ synthesize ≈ id` on a constrained corpus, and it is the
project's headline test. Here is exactly what it proves, stated precisely
rather than as one round number:

- Across **MIDI 33–96** (A1–C7), note **count**, **pitch**, and **onset**
  are recovered exactly (onset within one grid subdivision). This is
  checked on every push (40 fresh, reproducible cases); a larger 1,000-case
  unpinned sweep, for hunting rarer failures, runs on demand (a manual
  workflow) rather than on a schedule.
- **Duration** is *additionally* recovered exactly (within one grid
  subdivision) across **MIDI 33–71** (up to just below C5). Above that, the
  corpus is deliberately capped rather than tested unfairly: with this
  SoundFont, a high note's rendered audio decays below an audible threshold
  before a long declared duration would elapse, and the offset detector
  measures duration from acoustic energy — asking it to recover a sustain
  the audio does not contain would be testing the SoundFont's decay curve,
  not the transcriber. Count, pitch, and onset are still checked at the top
  of the keyboard; duration alone is not claimed there.
- In exploratory runs of up to 500 generated cases, exactly **two
  failures** were observed (roughly 0.4%): one YIN octave error (a note
  detected an octave away from the one played) and one missed note onset.
  Both are pre-existing limitations of the monophonic pitch/onset detector
  on real synthesized audio — unrelated to the duration cap, which only
  ever affects duration, never count, pitch, or onset. The on-demand deep
  closed-loop sweep surfaces and quarantines cases like these (rather than
  let them block every push); hardening the detector further (a
  probabilistic pitch tracker, better onset recall) is recorded as Phase-2
  work, not hidden behind a false 100% claim.

No claim here is "always correct." The claim is narrower and verified:
within its stated pitch, tempo, and duration constraints, the pipeline
recovers a performance from its own synthesized rendering with exact count,
pitch, and onset, and with exact duration wherever the audio can actually
support a duration claim — at a known, tracked failure rate of roughly four
in a thousand cases, not zero, and not hidden.

## Usage

Prerequisite: the **.NET 10 SDK (LTS)**.

```bash
dotnet build
dotnet test                        # full suite: unit + property + the closed loop
dotnet test --filter Category=Fast # skip the slow closed-loop properties
```

Conceptually, the CLI (composition root: `src/AudioClaudio.Cli`) has four
commands:

```bash
claudio transcribe <in.wav> --tempo 120 [--out-dir .]    # -> raw.mid, score.mid, score.musicxml
claudio listen --tempo 100 [--out-dir .] [--record]      # live; writes the same trio on Ctrl+C; --record also writes input.wav + recreation.wav
claudio play <file.mid> [--soundfont <path>]             # play a MIDI file through MeltySynth
claudio render <file.mid> <out.wav> [--soundfont <path>] # deterministically render a MIDI file to WAV
```

There is no packaged `claudio` binary yet, so run them today through
`dotnet run`:

```bash
dotnet run --project src/AudioClaudio.Cli -- transcribe song.wav --tempo 120 --out-dir out/
dotnet run --project src/AudioClaudio.Cli -- listen --tempo 100 --out-dir out/
dotnet run --project src/AudioClaudio.Cli -- play out/score.mid
dotnet run --project src/AudioClaudio.Cli -- render out/score.mid out/score.wav
```

`play` and `render` look for the committed SoundFont
(`fixtures/soundfont/GeneralUser-GS.sf2`) automatically when run from inside
the repository; pass `--soundfont <path>` to use a different one or to run
outside the repo tree. `transcribe` never touches a SoundFont at all — it
only detects notes, it does not synthesize them.

### `listen` — live microphone capture

`listen` feeds the microphone into the exact same transcription pipeline the
file path uses, so live and file transcription of the same audio agree by
construction. The note list printed while listening is a low-latency
*preview* (`TranscriptionPipeline.StreamNotes`); the files written on stop
come from an accurate whole-signal batch pass over the session's recorded
audio, not the live preview.

- **Output files.** The out-dir root always holds the LATEST session's
  `raw.mid`/`score.mid`/`score.musicxml` (plus `--record`'s `input.wav`/
  `recreation.wav`) at the same stable filenames, so tools can always point at
  the same paths. On start, `listen` clears any previous run's `*.mid`/
  `*.musicxml`/`*.wav` from the out-dir root (top-level only, so earlier
  archives are untouched); on stop, that same set is additionally copied into
  `<out-dir>/<YYYYMMDD_HHMMSS>/`, timestamped by when the session started.
- **`--view`.** Pass `--view` to open a live browser sheet-music view with Start/Stop
  recording buttons: each Start begins a new recording (opens the mic) and each Stop
  saves it into its own `<out-dir>/<YYYYMMDD_HHMMSS>/` (timestamp = when Start was
  pressed) and freezes the displayed score. Ctrl+C exits at any time, auto-saving a
  recording still in progress.
- **`--record`.** Pass `--record` to additionally write `input.wav` (the real
  captured microphone audio, reconstructed losslessly from the same frames
  the session already buffers) and `recreation.wav` (the raw transcription
  synthesized back to audio) into the output directory — load both into a
  waveform editor (e.g. Audacity) to compare the performance against what was
  transcribed. Omitted by default, so plain `listen` is unaffected and still
  never touches a SoundFont.
- **`--skip-silence`.** Pass `--skip-silence` for continuous playback: it removes
  pauses longer than ~500 ms from BOTH `input.wav` and `recreation.wav`, cutting
  the same sample spans from each so the two stay aligned, while shorter musical
  rests are left untouched. Implies `--record` (it only ever affects those two
  WAVs) — `raw.mid`/`score.mid`/`score.musicxml` are never touched and keep the
  true performance timing.
- **Latency.** The worst-case *algorithmic* onset latency — key-strike to
  the onset being known — is about **41 ms** at the default live parameters
  (44.1 kHz, 1024-sample frame, 256-sample hop, look-ahead 3), measured and
  asserted under a 150 ms budget. End-to-end latency additionally includes
  the input buffer and OS scheduling; measure it on your own machine with
  the loopback procedure below. This is a measured, documented figure, not
  a promise.
- **macOS microphone permission.** On the primary dev machine (an M3 Max),
  the first `listen` run triggers a system prompt to grant microphone
  access to your terminal. If capture is silent and no prompt appeared,
  enable it under System Settings → Privacy & Security → Microphone.
- **Manual acceptance.** There is no audio input device in CI, so the
  device-callback path is verified by hand, not by an automated test — the
  automated correctness burden stays entirely on the closed-loop suite
  above. To check it yourself: run `listen`, play a few notes (or a fixture
  WAV) aloud through your speakers, confirm notes print as they sound,
  press Ctrl+C, and confirm `raw.mid`/`score.mid`/`score.musicxml` were
  written and roughly match what was played.

## Limitations

This is a deliberately scoped MVP. It declines the following rather than
faking them:

- **Monophonic only.** One note at a time — chords and overlapping voices
  are out of scope for v0.1.0. Polyphony, via a neural model behind the
  same `ITranscriber` port, is the first item on the Phase-2 list.
- **Declared tempo, not estimated.** You pass `--tempo`; the MVP does not
  guess it. Hiding an unreliable tempo estimator inside the pipeline would
  quietly poison the closed-loop suite's timing checks, so tempo estimation
  is deferred rather than half-done.
- **Single staff.** MusicXML output is one staff, clef chosen by range,
  fixed 4/4 time signature. A treble/bass split is Phase-2 work.
- **Duration recovery has a pitch ceiling.** A note's *duration* is only
  recoverable while it stays audibly above the release threshold with this
  SoundFont — roughly MIDI 33–71 for a note an eighth or longer (see The
  closed loop, above). Above that, roughly C5 and up, onset and pitch are
  still transcribed correctly, but the notated duration is not something
  the rendered audio can support, so it is not claimed there either.
- **A rare (~0.4%) octave-error / missed-onset residual** exists on real
  synthesized piano audio, surfaced by the on-demand deep closed-loop run
  rather than hidden; see The closed loop, above. Hardening the pitch/onset
  detector further is Phase-2 work.
- **Live microphone capture and the MusicXML "loads in MuseScore" check are
  both manual, not automated.** No audio device exists in CI, so
  `listen`'s device path is verified by hand (see Usage, above). Likewise,
  the MusicXML writer's structural conformance to MusicXML 4.0 is checked
  automatically (a byte-identical golden file plus a schema-shape review),
  but an actual "opens and renders correctly in MuseScore" pass by a human
  has not yet been recorded — see `DECISIONS.md` for the open action item.
- Pitch accuracy is characterized across MIDI 33–96 (A1–C7); the extreme
  low and high keys of the full 88-key range are legitimately harder, and
  that is documented rather than silently assumed away.

## License

This is free and unencumbered software released into the public domain. See
[`UNLICENSE`](UNLICENSE) at the repository root.
