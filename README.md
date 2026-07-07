# audio-claudio
Audiō Claudiō — "I hear, by means of Claude." Real-time piano-to-sheet-music transcription in C#, built with Claude Code. Public domain.

<!-- NOTE: this is a placeholder README; Step 12 writes the full one. The "Live capture"
     section below is required by the Step 10 plan (usage, the latency figure, and the
     manual-acceptance procedure) and will be folded into the Step 12 rewrite. -->

## Live capture (`listen`)

    dotnet run --project src/AudioClaudio.Cli -- listen --tempo 100 [--out-dir .]

Captures from the default input device, prints each detected note as it is played,
and on Ctrl+C writes `raw.mid` (unquantized performance) and `score.mid` (quantized)
into `--out-dir`. `score.musicxml` is added once Step 11 lands the MusicXML writer —
`listen` is already wired for it (an optional injected writer), so no change to
`listen` is needed then.

The microphone is *just an adapter*: `listen` feeds the same transcription pipeline
the file path uses, so live and file transcription of the same audio are identical by
construction (proven device-free in `CaptureWavEquivalenceTests`). The live on-screen
note list is a low-latency incremental *preview* (`TranscriptionPipeline.StreamNotes`);
the written files come from the accurate whole-signal batch pass over the session's
audio, run on stop.

**Latency.** Worst-case *algorithmic* onset latency (key-strike to the onset being
known) at the default live parameters (44.1 kHz, frame 1024, hop 256, look-ahead 3) is
**~41 ms** (`LatencyBudget`, asserted < 150 ms by `LatencyBudgetTests`). The printed
note follows once its pitch has settled (a few more frames), and end-to-end latency
additionally includes the PortAudio input buffer and OS scheduling — measure that on
your machine with the loopback procedure below (R10.2 is a measured/documented target,
not a promise).

**macOS microphone permission.** The primary dev machine is an M3 Max; the first
`listen` run triggers a TCC prompt to grant your terminal (or the built app)
microphone access. If no prompt appears and capture is silent, enable it under
System Settings → Privacy & Security → Microphone.

**Manual acceptance (loopback / by ear).** There is no audio device in CI, so the
device path (`PortAudioAudioSource.Start()` and the native capture callback) is
verified by hand, not in the automated suite — the automated correctness burden stays
on the Step 9 closed loop. To run the check once:

1. `dotnet run --project src/AudioClaudio.Cli -- listen --tempo 100 --out-dir /tmp/claudio-listen`
2. Play a fixture WAV aloud through the speakers (or play a few piano notes).
3. Confirm notes print as they sound, then press Ctrl+C.
4. Confirm `raw.mid` and `score.mid` were written to the out-dir, and compare
   `score.mid` loosely against the notes played.

Exact correctness is owned by the Step 9 closed-loop suite; this check only confirms
the capture adapter is wired and audible.
