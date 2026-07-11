# Audio Claudio

*Audiō Claudiō* — "I hear, by means of Claude." A real-time piano transcriber
in C# / .NET 10 (LTS), built in open collaboration with Claude Code. Audio
comes in from a microphone or a WAV file; note events come out; notation is
emitted as MusicXML; and the transcribed piece can be played back through a
synthesized piano. **Three engines share one pipeline**, each with its own
*earned* guarantee: a **polyphonic** engine (Basic Pitch) for chords and two
hands — the default, closed-loop-proven at note-level F1 ≥ 0.75 (v2); a
**monophonic** core (YIN), reached with `--mono`, that carries the stronger
*exact-recovery* closed-loop proof below; and a self-contained **Transkun**
engine (`--model transkun`, v2) — a notation-fidelity neural transcriber (real
durations, velocity, sustain/soft pedal) that runs in-process via ONNX with no
Python, validated **note-identical to the reference PyTorch implementation**
(≥ 99 % parity — measured 100 % + exact velocity). Public domain (`UNLICENSE`).

## What it is

Play a melody line — into a microphone, or as a WAV file — and Audio Claudio
tells you which notes were played, when they started, and how long they
lasted, then writes the result out as MIDI and as sheet music (MusicXML). Its
polyphonic engine (the default for `transcribe` since v0.2.0) recovers chords
and two hands at once; its monophonic core (`--mono`) listens to one note at a
time and carries the stronger *exact-recovery* closed-loop proof, while the
polyphonic default has its own *statistical* closed-loop gate (see
[Limitations](#limitations) below). It detects each note's
pitch and attack from the raw audio, snaps the timing to a tempo — declared by
you, or auto-estimated from your playing if you don't pass one — and can sing
the transcription back to you through a synthesized piano.

It is a small, honest transcriber. Its correctness claims are not asserted —
they are *earned*, by the closed-loop suites described below (exact recovery for
the monophonic core, a statistical F1 gate for the polyphonic default).

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

The polyphonic engine has its own closed-loop gate on the same principle, but
*statistical* rather than exact: it requires note-level **F1 ≥ 0.75 at ±50 ms**
over a fixed-seed chord corpus (measured 79.6 %; see [Limitations](#limitations)
and `docs/CORPUS.md`). The **Transkun** engine (`--model transkun`) earns a third
kind of guarantee — a **parity** one: because it is an exact ONNX re-derivation of
a published PyTorch model, a CI gate requires its C# output to agree with the
native `transkun` reference at **≥ 99 % note-level F1** (measured **100 % at
±25 ms, with exact velocity** on every note, against committed reference
transcriptions). **The three guarantees are ranked, never flattened:** monophonic
= *exact* closed-loop recovery; polyphonic default = a *statistical* F1 bar;
Transkun = *parity* with its reference implementation. Each states its own number
and its own limits.

The corpus this is measured on — its committed distribution, seeds, and the
per-engine baseline numbers (monophonic *and* polyphonic) — is documented in
[`docs/CORPUS.md`](docs/CORPUS.md), and it is the source of every **headline**
number: each engine's baseline comes from generated scores, never from a single
recording. (The one real-recording figure reported under Limitations below is
labelled as such, and never used to state a ceiling.)

## Usage

Prerequisite: the **.NET 10 SDK (LTS)**.

```bash
dotnet build
dotnet test                        # full suite: unit + property + the closed loop
dotnet test --filter Category=Fast # skip the slow closed-loop properties
```

Conceptually, the CLI (composition root: `src/AudioClaudio.Cli`) has six
commands:

```bash
claudio transcribe <in.wav> [--tempo 120] [--out-dir .] [--note-names] [--mono] [--model <path|transkun>] [--key <fifths>] [--triplets] [--onset-threshold 0.5] [--frame-threshold 0.3] [--min-note-len 11]  # -> raw.mid, score.mid, score.musicxml; POLYPHONIC (Basic Pitch, grand staff) by default (closed-loop-proven, F1 ≥ 0.75 @ ±50 ms); --mono for the monophonic YIN pipeline (exact-recovery path); --model transkun for the self-contained Transkun engine (notation fidelity, ≥99% PyTorch parity); key auto-detected (--key overrides); --triplets engraves triplets
claudio listen [--tempo 100] [--out-dir .] [--view] [--record] [--skip-silence] [--note-names]  # live; writes the same trio on Ctrl+C; --tempo auto-estimated if omitted
claudio play <file.mid> [--soundfont <path>]                                           # play a MIDI file through MeltySynth
claudio render <file.mid> <out.wav> [--soundfont <path>]                               # deterministically render a MIDI file to WAV
claudio evaluate <candidate.mid> <reference.mid> [--onset-tolerance-ms 50] [--align|--warp]  # note-level precision/recall/F1 vs a reference; --align/--warp remove tempo drift/rubato before scoring
claudio evaluate-audio <original.wav> <reproduction.wav>  # timbre-robust pitch-content (chroma) similarity: does a re-synthesis SOUND like the original? 1.0 = identical notes over time
```

### Options reference

Every command and flag, exhaustively. Options are order-independent; unknown
flags are ignored. A `--flag <value>` option reads the token that follows it;
a bare `--flag` is a boolean switch.

**`transcribe <in.wav>`** — batch-transcribe a WAV file.

| Option | Default | Effect |
|---|---|---|
| `<in.wav>` | *(required)* | 16/24-bit PCM WAV, mono or multichannel (downmixed to mono). |
| `--mono` | off (polyphonic) | Use the **monophonic** YIN pipeline (one note at a time) — the *exact-recovery* closed-loop path — instead of the default polyphonic engine (which has its own, *statistical*, closed-loop gate; see [Limitations](#limitations)). Auto-estimates tempo when `--tempo` is omitted. |
| `--legato` | off | *(with `--mono`)* Recover legato notes — a pitch change with no re-attack opens a new note. A deliberate trade-off: a monophonic detector cannot perfectly separate a real legato slur from a pitch wobble, so this recovers connected notes at the cost of the occasional spurious note. Off keeps the proven one-note-per-onset behavior. |
| `--coarse-rhythm` | off | *(with `--mono`)* Floor note *values* at an eighth note, so uneven/beginner playing engraves as cleaner rhythm (drops jittery sixteenths and dotted-eighths). Onsets are never coarsened, only note lengths. |
| `--tempo <bpm>` | 120 (poly) / auto (`--mono`) | Grid tempo. The polyphonic default uses 120 BPM unless set; `--mono` estimates it from the notes' onset spacing when omitted (see [Limitations](#limitations)). |
| `--out-dir <dir>` | `.` | Where `raw.mid`, `score.mid`, `score.musicxml`, and `log.txt` are written. Created if absent. |
| `--note-names` | off | Print each note's scientific-pitch name (e.g. `C4`, `F#5`) as a lyric beneath it in `score.musicxml`. |
| `--model <path\|transkun>` | committed Basic Pitch | `--model transkun` selects the self-contained **Transkun** engine (notation fidelity — real durations, velocity, sustain/soft pedal; ≥ 99 % PyTorch parity). Otherwise a path to the polyphonic Basic Pitch `.onnx` (default: the committed `fixtures/models/basic-pitch-nmp.onnx`). Ignored by `--mono`. |
| `--key <fifths>` | *auto-detected* | The key signature (sharps positive, flats negative — `-4` = A♭ major, `2` = D major). By default it is **auto-detected** from the notes (Krumhansl-Schmuckler); pass `--key` to override. It sets `score.musicxml`'s `<key>` and spells every accidental to match (A♭ major → E♭/A♭/B♭, not D#/G#/A#). Ignored by `--mono`. |
| `--triplets` | off | *(polyphonic)* Engrave eighth-note triplets. Off by default because auto-quantizing to triplets manufactures spurious triplets on straight music — opt in when the piece has them. |
| `--onset-threshold <v>` | `0.5` | Polyphonic (Basic Pitch) decoder: minimum onset activation to *start* a note. Raise it for fewer, more-confident note starts. See the tuning note below. |
| `--frame-threshold <v>` | `0.3` | Polyphonic (Basic Pitch) decoder: minimum sustained activation to *keep* a note. The dominant density lever — raising it most reduces the note count. |
| `--min-note-len <frames>` | `11` | Polyphonic (Basic Pitch) decoder: the flicker floor — notes shorter than this many frames (~128 ms at 11) are discarded. Raise it to shed short spurious notes. |

**Polyphonic transcription (the default).** As of v0.2.0 `transcribe` runs the
neural **Basic Pitch** model (Spotify, Apache-2.0) through ONNX Runtime by
default, recovering many simultaneous notes — chords, two hands. It is
**closed-loop-proven** (v2): a CI property gate synthesizes a fixed-seed corpus
of random chord scores, transcribes them, and requires **note-level F1 ≥ 0.75 at
±50 ms** — currently measuring **79.6 %** (81.4 % at ±100 ms, 82.0 % at ±150 ms)
over 32 cases / 451 notes. This is a *statistical* guarantee, not the monophonic
engine's exact recovery — a neural model never returns a score bit-for-bit — so
the two guarantees are ranked, not flattened (see [Limitations](#limitations)).
(Pass `--mono` for the monophonic YIN pipeline — the exact-recovery path.) What
the polyphonic engine produces and how to steer it:

- **All three outputs are polyphonic.** `raw.mid` is the honest, un-quantized
  many-note output (exact performance timing). `score.mid` and `score.musicxml`
  are built by a polyphonic **grand-staff** quantizer: chords in both hands, a
  **temporal treble/bass hand-split** (v2 — two hand-centres that track the two
  lines over time, so a hand crossing middle C keeps its notes, where a fixed cut
  would not), each staff's measures conserving a full 4/4 bar. `render` and `play`
  on any of them are fully polyphonic. (The score is homophonic-per-staff — a
  stack of chords per hand — not yet independent inner voices; and its timing is
  snapped to the grid, where `raw.mid` keeps the exact performance.)
- **Key signature & spelling — auto-detected (v2).** The key signature is
  **detected from the notes** (Krumhansl-Schmuckler) and used to spell every
  accidental: a piece in A♭ major engraves its black keys as E♭/A♭/B♭, not the
  default C-major sharps. Pass `--key <fifths>` (sharps positive, flats negative —
  `-4` for A♭ major, `2` for D major) to override the detection. Dynamics
  (velocity → *pp*…*ff*) and sustain-pedal marks are engraved too.

**The Transkun engine (`--model transkun`).** A third `transcribe` engine, for
**notation fidelity**: Yujia Yan's Neural Semi-CRF piano transcriber (MIT, 0.984
MAESTRO), running **entirely in-process via ONNX — no Python/PyTorch at runtime**.
The transformer + scorer + attribute heads are a committed 53 MB ONNX export; the
mel front end and the semi-CRF Viterbi decode are reimplemented in C# (the parts
ONNX can't hold), and the whole chain is validated **note-identical to the native
`transkun` reference — 100 % note-level F1 at ±25 ms with exact velocity**. Its
edge over the Basic Pitch default is real key-press **durations**, native
**velocity**, and **sustain/soft pedal**; it feeds the same grand-staff notation.
It is **selectable, not the default** — reach for it when engraving fidelity
matters most. The artifact (ONNX + buffers + decode spec + license) lives under
`fixtures/models/transkun/`, crediting the original authors.
- **Tuning the note density.** The three thresholds trade recall for precision.
  On dense piano they barely move note-level accuracy — which is bounded by onset
  timing, not by over-generation — but they do control how *cleanly* the score
  reads. `--onset-threshold 0.6 --frame-threshold 0.4 --min-note-len 16` cuts the
  note count by roughly a third (1887 → 1188 on the test piece), toward the
  engraved score's density, at essentially no accuracy cost; the stock defaults
  (`0.5`/`0.3`/`11`) maximize recall.
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

**`evaluate <candidate.mid> <reference.mid>`** — score a transcription's notes against a reference note-set (precision/recall/F1).

| Option | Default | Effect |
|---|---|---|
| `<candidate.mid>` | *(required)* | The transcription to score — typically a `transcribe`/`listen` output (`raw.mid` or `score.mid`). |
| `<reference.mid>` | *(required)* | The note-set to compare against — a hand-corrected transcription, an OMR export of engraved sheet music, or similar. None is bundled with the repo (see below). |
| `--onset-tolerance-ms <ms>` | `50` | Onset match window, in milliseconds. Pitch must still match exactly regardless of tolerance — an octave error is always a miss, never partial credit. |
| `--align` | off | Globally rescale the candidate's onset span onto the reference's before scoring, cancelling one overall tempo ratio between a performance and a score. |
| `--warp` | off | Dynamic-time-warp the candidate onto the reference, additionally cancelling *local* rubato (not just one global ratio). Wins over `--align` if both are passed. |

`evaluate` reports note-level precision, recall, and F1 — the standard MIR
note-detection metric — between a candidate transcription and a reference
note-set. A candidate note counts as matched only when its pitch is exactly
equal to a reference note's (an octave error is always a miss, never partial
credit) and its onset falls within `--onset-tolerance-ms`; matching is
one-to-one, so a duplicated candidate cannot inflate the score, and note
durations are ignored entirely (a performance's rubato and pedalling need not
match a score's). `--align`/`--warp` remove timing drift — one global tempo
ratio, or local rubato — from the candidate *before* scoring, using onset times
only and never pitch, so the reported F1 measures pitch recovery honestly
rather than being deflated by a tempo mismatch between a recording and a score.
This is the yardstick the polyphonic engine was tuned against (see
[Limitations](#limitations)): no reference MIDI ships with the repo, because the
piece used to develop and measure it there is a commercially copyrighted
recording kept outside the repository — point `evaluate` at any note-set you
consider ground truth.

**`evaluate-audio <original.wav> <reproduction.wav>`** — how much a re-synthesis
*sounds like* the original, by pitch content.

| Option | Default | Effect |
|---|---|---|
| `<original.wav>` | *(required)* | The source recording. |
| `<reproduction.wav>` | *(required)* | A `render` of the transcription's `raw.mid` — the notes played back through the SoundFont. |

This sidesteps the reference problem entirely: the original recording *is* the
ground truth, so there is no OMR error and no performance-vs-score rubato to
fight. It compares the two files' **chromagrams** — each moment's spectral
energy folded into the 12 pitch classes — so a real piano and a SoundFont
re-synthesis are comparable by the *notes they play*, not their (very different)
timbre. The score is the mean per-frame cosine similarity (offset-searched to
absorb latency), `1.0` for identical pitch content over time. As calibration, an
identical file scores `100%` and a version with the same rhythm but transposed to
the wrong pitches scores `~18%` — so a real transcription re-synthesis landing in
the high-70s genuinely reproduces most of the original's notes. Because `raw.mid`
keeps the performance's own timing, the two recordings stay aligned frame-for-frame.

There is no packaged `claudio` binary yet, so run them today through
`dotnet run`:

```bash
dotnet run --project src/AudioClaudio.Cli -- transcribe song.wav --key -4 --out-dir out/   # polyphonic (default)
dotnet run --project src/AudioClaudio.Cli -- transcribe song.wav --mono --out-dir out/     # monophonic, tempo auto-estimated
dotnet run --project src/AudioClaudio.Cli -- listen --tempo 100 --out-dir out/
dotnet run --project src/AudioClaudio.Cli -- play out/score.mid
dotnet run --project src/AudioClaudio.Cli -- render out/score.mid out/score.wav
dotnet run --project src/AudioClaudio.Cli -- evaluate out/score.mid reference.mid --onset-tolerance-ms 50 --warp
```

`play` and `render` look for the committed SoundFont
(`fixtures/soundfont/GeneralUser-GS.sf2`) automatically when run from inside
the repository; pass `--soundfont <path>` to use a different one or to run
outside the repo tree. `transcribe` never touches a SoundFont at all — it
only detects notes, it does not synthesize them. Both `play` and `render`
honor the MIDI **sustain pedal** (CC64): a transcription that models the pedal
as short notes plus a held pedal (as a good piano transcriber does) rings as
intended, rather than playing dry.

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

- **Polyphonic by default; three *different* guarantees, ranked (never flattened).**
  As of v0.2.0 `transcribe` defaults to the polyphonic Basic Pitch engine (chords,
  two hands, a real grand-staff `score.mid`/`score.musicxml`), closed-loop-proven by a
  **statistical** gate — note-level **F1 ≥ 0.75 at ±50 ms** on a fixed-seed synthetic
  corpus (measured 79.6 %; 82.0 % at ±150 ms) — *not* the monophonic engine's (`--mono`)
  **exact** recovery (count/pitch/onset bit-for-bit), and *not* the Transkun engine's
  (`--model transkun`) **≥ 99 % parity** with its PyTorch reference (measured 100 % +
  exact velocity). Three kinds of proof — exact / statistical / parity — each with its
  own number and limits. On one real, engraved-reference recording the polyphonic F1
  falls to ~15–22 %, but that gap is performance-vs-score rubato and the reference's own
  OMR error — *not* the engine's pitch/onset fidelity (see `DECISIONS.md`). No engine's
  score has independent inner voices yet (each staff is a chord stack). Transkun's
  ~53 MB ONNX runs at ~1.3× realtime (a 172 MB/segment score matrix dominates). Reach
  for `--mono` for exact recovery, or `--model transkun` for the cleanest engraving.
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
- **Single staff (monophonic path only).** The `--mono` pipeline's MusicXML is
  one staff, clef chosen by range, fixed 4/4. The polyphonic default and the
  Transkun engine emit a two-staff grand staff with a temporal hand-split; porting
  the grand staff (and velocity dynamics) back to the monophonic writer is future
  work.
- **No packaged binary yet.** There is no self-contained `claudio` executable —
  run the commands through `dotnet run` (see Usage). Packaging per platform and
  cross-platform (macOS + Windows) validation are the **v2.1** cycle.
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
