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
timing to a tempo — declared by you, or auto-estimated from your playing if
you don't pass one — and can sing the transcription back to you through a
synthesized piano.

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
claudio transcribe <in.wav> [--tempo 120] [--out-dir .] [--note-names] [--poly [--model <path>] [--key -4] [--onset-threshold 0.5] [--frame-threshold 0.3] [--min-note-len 11]]  # -> raw.mid, score.mid, score.musicxml; --tempo auto-estimated if omitted; --poly uses the polyphonic Basic Pitch engine (--key spells accidentals, thresholds tune note density)
claudio listen [--tempo 100] [--out-dir .] [--view] [--record] [--skip-silence] [--note-names]  # live; writes the same trio on Ctrl+C; --tempo auto-estimated if omitted
claudio play <file.mid> [--soundfont <path>]                                           # play a MIDI file through MeltySynth
claudio render <file.mid> <out.wav> [--soundfont <path>]                               # deterministically render a MIDI file to WAV
```

### Options reference

Every command and flag, exhaustively. Options are order-independent; unknown
flags are ignored. A `--flag <value>` option reads the token that follows it;
a bare `--flag` is a boolean switch.

**`transcribe <in.wav>`** — batch-transcribe a WAV file.

| Option | Default | Effect |
|---|---|---|
| `<in.wav>` | *(required)* | 16/24-bit PCM WAV, mono or multichannel (downmixed to mono). |
| `--tempo <bpm>` | auto-estimate | Grid tempo. Omit to estimate it from the notes' onset spacing (see [Limitations](#limitations)). |
| `--out-dir <dir>` | `.` | Where `raw.mid`, `score.mid`, `score.musicxml`, and `log.txt` are written. Created if absent. |
| `--note-names` | off | Print each note's scientific-pitch name (e.g. `C4`, `F#5`) as a lyric beneath it in `score.musicxml`. |
| `--poly` | off | Use the **polyphonic** Basic Pitch engine (many simultaneous notes) instead of the default monophonic YIN pipeline. See the note below — the polyphony lands in `raw.mid`; `score.mid`/`score.musicxml` are still monophonic for now. Does not auto-estimate tempo (uses `--tempo`, else 120 BPM). |
| `--model <path>` | committed model | With `--poly`, use a different Basic Pitch `.onnx` model. Default is the committed `fixtures/models/basic-pitch-nmp.onnx`, resolved relative to the repo. |
| `--key <fifths>` | `0` (C major) | With `--poly`, the key signature as a count of sharps (positive) or flats (negative) — `-4` = A♭ major, `2` = D major. Sets `score.musicxml`'s `<key>` and spells every accidental to match (A♭ major → E♭/A♭/B♭, not D#/G#/A#). |
| `--onset-threshold <v>` | `0.5` | With `--poly`, minimum onset activation to *start* a note. Raise it for fewer, more-confident note starts. See the tuning note below. |
| `--frame-threshold <v>` | `0.3` | With `--poly`, minimum sustained activation to *keep* a note. The dominant density lever — raising it most reduces the note count. |
| `--min-note-len <frames>` | `11` | With `--poly`, the flicker floor: notes shorter than this many frames (~128 ms at 11) are discarded. Raise it to shed short spurious notes. |

**Polyphonic transcription (`--poly`).** By default `transcribe` runs the
monophonic YIN pipeline (one note at a time). Pass `--poly` to instead run the
neural **Basic Pitch** model (Spotify, Apache-2.0) through ONNX Runtime, which
recovers many simultaneous notes — chords, two hands. What it produces and how
to steer it:

- **All three outputs are polyphonic.** `raw.mid` is the honest, un-quantized
  many-note output (exact performance timing). `score.mid` and `score.musicxml`
  are built by a polyphonic **grand-staff** quantizer: chords in both hands, a
  treble/bass split at middle C, each staff's measures conserving a full 4/4 bar.
  `render` and `play` on any of them are fully polyphonic. (The score is
  homophonic-per-staff — a stack of chords per hand — not yet independent inner
  voices; and its timing is snapped to the grid, where `raw.mid` keeps the exact
  performance.)
- **Key signature & spelling.** Pass `--key <fifths>` (sharps positive, flats
  negative — `-4` for A♭ major, `2` for D major) to set `score.musicxml`'s key
  signature and spell every accidental to match it: a piece in A♭ major engraves
  its black keys as E♭/A♭/B♭, not the default C-major sharps. A MIDI number alone
  can't be spelled correctly without the key, so this is a *declared* value (like
  the tempo), defaulting to C major.
- **Tuning the note density.** The three thresholds trade recall for precision.
  On dense piano they barely move note-level accuracy — which is bounded by onset
  timing, not by over-generation — but they do control how *cleanly* the score
  reads. `--onset-threshold 0.6 --frame-threshold 0.4 --min-note-len 16` cuts the
  note count roughly in half, toward the engraved score's density, at essentially
  no accuracy cost; the stock defaults (`0.5`/`0.3`/`11`) maximize recall.
- Inference is deterministic per build, but — like the MeltySynth mixdown — not
  guaranteed bit-identical across CPU architectures (SIMD).

**`listen`** — live microphone capture (writes the same trio on stop).

| Option | Default | Effect |
|---|---|---|
| `--tempo <bpm>` | auto-estimate | Grid tempo for the saved score. Omit to estimate it on stop. The live preview always uses a 120 BPM fallback grid, since the estimate is only known after the whole-signal batch pass. |
| `--out-dir <dir>` | `.` | Where session files are written (stable latest paths in the root, plus a timestamped archive per recording). Created if absent. |
| `--view` | off | Open a live browser sheet-music view with Start/Stop recording buttons — multiple takes per run, each saved under its own start-timestamp. Degrades to a plain single recording if the server can't start. |
| `--record` | off | Also write `input.wav` (the captured mic audio, losslessly reconstructed) and `recreation.wav` (the transcription re-synthesized) for A/B comparison. |
| `--skip-silence` | off | Collapse pauses longer than ~500 ms out of both `input.wav` and `recreation.wav`, cut from each in alignment. Implies `--record`; never affects the MIDI/MusicXML timing. |
| `--note-names` | off | Show each note's scientific-pitch name beneath it in both the `--view` rendering and `score.musicxml`. |

**`play <file.mid>`** — play a MIDI file aloud through MeltySynth (PortAudio output).

| Option | Default | Effect |
|---|---|---|
| `<file.mid>` | *(required)* | The MIDI file to play. |
| `--soundfont <path>` | committed GS SoundFont | Use a different `.sf2`. Required when running outside the repo tree (the default is resolved relative to the repo). |

**`render <file.mid> <out.wav>`** — deterministically render a MIDI file to a WAV.

| Option | Default | Effect |
|---|---|---|
| `<file.mid>` | *(required)* | The MIDI file to render. |
| `<out.wav>` | *(required)* | The output WAV path. |
| `--soundfont <path>` | committed GS SoundFont | Use a different `.sf2` (see `play`). |

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
- **`--note-names`.** Pass `--note-names` to print each note's scientific-pitch
  name (e.g. `C4`, `F#5`) beneath it in the notation — both the live `--view`
  rendering and the saved `score.musicxml`. Opt-in and off by default; `transcribe`
  also accepts the same flag for `score.musicxml`.
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

- **Monophonic by default; polyphony is opt-in and still maturing.** The
  default pipeline is **monophonic** — one note at a time — and everything the
  closed-loop suite proves is about that path. `transcribe --poly` runs a neural
  model (Basic Pitch) behind the same `ITranscriber` port and recovers chords and
  overlapping voices: `raw.mid` is the honest many-note output, and `score.mid`/
  `score.musicxml` are now a real grand staff — chords in both hands, a treble/bass
  split, key-aware accidental spelling, tunable note density, quantized to a 4/4
  grid. What it still lacks: any closed-loop correctness guarantee like the
  monophonic path; independent inner voices (each staff is a chord stack, not
  separately-moving lines); and high note-level accuracy on dense real audio —
  measured against an engraved reference, note-level F1 is modest (~15–22 %
  depending on onset tolerance), bounded by onset timing and exact-pitch matching,
  not by the notation. Treat `--poly` as a capable preview, not a finished feature.
- **Tempo: a declared value by default, optional to override.** Pass
  `--tempo` for a declared tempo used exactly; omit it and the pipeline
  instead estimates tempo from the detected notes' onset spacing (the
  median inter-onset interval — the most common gap between note starts is
  taken to be one beat). The estimator assumes a mostly-even melody
  dominated by one note value (a beginner's quarter-note tune, say); on a
  heavily syncopated or mixed-rhythm performance it can lock onto an
  octave-wrong tempo — double or half the true BPM, since it cannot tell
  "quarters at T" from "eighths at 2T" — so pass `--tempo` explicitly
  whenever exact timing matters. (This estimator is not part of the
  closed-loop suite's timing checks, which still run at a known declared
  tempo — see The closed loop, above.)
- **Single staff (monophonic path).** The default monophonic pipeline's
  MusicXML is one staff, clef chosen by range, fixed 4/4 time signature. The
  `--poly` path instead emits a two-staff grand staff (treble/bass split at
  middle C); a treble/bass split for the monophonic path remains Phase-2 work.
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
