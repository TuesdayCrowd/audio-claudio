# Live polyphonic capture — design

> **Status:** draft for Cornelius's review (2026-07-12). Evolves the Stage 5 live-view
> (`listen --view`) from a monophonic real-time sketch into **near-real-time polyphonic**
> capture from a microphone, per Cornelius's post-acceptance requirements. Not yet implemented.
> This is a genuinely new capability, larger than the rest of Stage 5 — designed here so we can
> judge feasibility (latency, accuracy) before committing to the full build.

## What Cornelius asked for (locked 2026-07-12)

1. **Polyphonic** — track multiple simultaneous notes, each with its own start and end (his
   example: an A and a C struck together, the A a half note, the C a whole note).
2. **Placeholder at onset → finalize at offset** — a note appears as a **quarter-note placeholder**
   the instant it's heard, and is rewritten to its true length when it ends.
3. **No rest glyphs, real spacing kept** — silence is not drawn as rests; notes stay at their real
   time positions, the gaps simply aren't rendered as rest symbols.
4. **Input is an acoustic microphone** (not MIDI) — chosen with eyes open: this is the hard case.

## The core challenge

Real-time polyphonic transcription *with per-note offsets* from audio is the hard problem in this
field. Our engines don't stream: the real-time detector (`TranscriptionPipeline.StreamNotes`/YIN) is
**monophonic**, and the polyphonic engine (**Basic Pitch**, ONNX) is a neural model, not a causal
detector.

**The enabling fact:** Basic Pitch is *internally windowed*. `BasicPitchModel.Run` maps exactly one
**~2 s window** (`WindowSamples = 43 844` @ 22 050 Hz) to three posteriorgrams (note-frames, onsets,
contour), 86 fps; `BasicPitchTranscriber` runs it over **overlapping ~2 s windows** (hop ≈ 1.64 s,
30-frame overlap trimmed) and stitches them before decoding. So we don't re-infer the whole clip
each tick — we run the *same windowed pipeline incrementally*, one window as each ~1.6 s of audio
arrives. Cost per tick is bounded (one ONNX forward pass), and the floor latency is the window
itself (~2 s), not a growing buffer.

## Architecture

```
mic frames ──▶ rolling audio buffer (22.05 kHz mono, resampled)
                    │  every ~1.6 s (one Basic Pitch hop)
                    ▼
        run BasicPitchModel on the next ~2 s window (background thread)
                    │
                    ▼
        append window posteriorgram → growing stitched posteriorgram
                    │
                    ▼
        re-decode the WHOLE accumulated posteriorgram → current note set   (cheap; decode ≪ inference)
                    │
                    ▼
        reconcile + classify: each note is ONGOING (open at the latest frame → placeholder)
                                          or SETTLED (offset stable → true length)
                    │
                    ▼
        build a polyphonic grand-staff Score → MusicXML → publish over SSE → OSMD re-renders
```

- **Inference is bounded and off the capture thread.** One window per hop on a background worker; the
  mic callback only fills the buffer. Re-decoding the accumulated posteriorgram each tick is cheap
  (array math), so the expensive step stays one ONNX pass per ~1.6 s.
- **Placeholder → finalize falls out naturally.** A note still active at the newest decoded frame has
  no confirmed offset yet → render it as the quarter-note placeholder. When a later window's decode
  shows the note has ended, its offset is fixed → rewrite to the true length. This is exactly the
  requested UX, and it *is* the streaming behavior, not a bolted-on trick.
- **On Stop, the final decode is over the full audio** — identical to a batch `Transcribe`, so the
  saved `score.mid`/`score.musicxml` equal the last live frame (no jump on Stop — the WYSIWYG concern,
  now satisfied *because* the live and final decode are the same computation over the same audio).

## Components (hexagonal placement)

- **Domain** — `LiveNoteReconciler` (pure): given the previous published note set and the freshly
  decoded note set, produce the next set with stable identity (match by pitch + onset within a
  tolerance) and an `Ongoing`/`Settled` flag per note. Deterministic, no I/O. Plus a notation option
  for **invisible rests** (see below).
- **Application** — `LivePolyphonicSession`: orchestrates the buffer → windowed-inference → decode →
  reconcile → publish loop, mirroring `LiveTranscriptionSession`'s role for the mono path. Injects the
  inference + decode functions (Infrastructure) so it stays testable with a fake.
- **Infrastructure** — a **streaming entry** into the Basic Pitch pipeline: expose an incremental
  form of `BasicPitchTranscriber` that accepts appended audio and yields the growing decoded note set
  (reusing the existing window/stitch/decode code — not a reimplementation). Resampling to 22 050 Hz
  reuses `AudioResampler`.
- **Cli / live view** — wire `listen --view` to the polyphonic session; publish the **grand-staff**
  MusicXML (`GrandStaffMusicXmlWriter`) instead of the mono `MusicXmlScoreWriter`; OSMD already renders
  any MusicXML, so the browser side is a writer swap + the Area-D polish already shipped.

## Key design decisions (recommendations; Cornelius's call on the starred ones)

- **★ Replace, or add an engine switch?** `listen` is monophonic today. Recommend `listen --view`
  uses the **polyphonic** path by default (matching `transcribe`'s polyphonic default), and keep the
  monophonic real-time path available (it's lower-latency and exact for single lines) behind `--mono`.
  Alternative: polyphonic-only. *Cornelius decides.*
- **★ Latency vs. responsiveness.** Floor is ~2 s (the window). Hop ≈ 1.6 s sets how often the chart
  updates. We can shorten the *perceived* lag by decoding a partial trailing window, at some accuracy
  cost. Recommend: ship the natural ~1.6 s hop first, measure, then tune. *This is the feasibility
  gate — see below.*
- **No rests = invisible rests.** MusicXML represents "keep spacing, hide the glyph" with rests
  marked `print-object="no"`: the bar's arithmetic still balances (bar-conservation invariant holds)
  and the timing/spacing is preserved, but no rest symbol is drawn. Recommend a `hideRests` mode on
  `GrandStaffMusicXmlWriter` (default off elsewhere, on for the live view). Cleaner than deleting
  rests, which would corrupt bar durations.
- **Placeholder representation.** An ongoing note (open at the newest frame) renders as a quarter
  note; when settled, its real quantized value replaces it. The reconciler flags this; the writer
  renders the flag.
- **Reconciliation matching.** Match a freshly-decoded note to an existing one by **exact pitch +
  onset within ±1 grid tick**; unmatched fresh notes are new; existing notes absent from a fresh
  decode over their span are dropped (a false positive that didn't survive more context). This is what
  keeps the chart from duplicating held notes or flickering every tick.
- **What's decoded each tick.** Re-decode the *whole* accumulated posteriorgram (not just the new
  window) so earlier notes can be revised as more context arrives — the model genuinely improves notes
  near a boundary once the next window lands. Decode is cheap; only inference is gated to per-window.

## Honest limitations (state these plainly; do not oversell)

- **Near-real-time, not instant:** the chart trails your playing by ~2 s (the model's window). This is
  inherent to a 2 s-context neural model; it is not a bug to be tuned away to zero.
- **Accuracy is Basic Pitch's (~80 % F1 on clean piano)** and *live* is harder than batch — notes may
  appear, shift, or vanish as more context arrives; the placeholder→finalize model makes that legible
  rather than jarring, but it does not make it exact.
- **Guarantee positioning:** this is a **live-assist** feature, explicitly **not** a proven-accuracy
  claim. It ranks below every existing guarantee (mono bit-exact / poly statistical F1 / Transkun
  parity). The saved files on Stop carry the existing polyphonic guarantee; the *live* view carries
  none beyond "it's the same engine, shown as it resolves."
- **CPU cost:** one ONNX forward pass per ~1.6 s on the CPU while capturing. Must be measured on the
  M3 Max (primary) — if a window's inference exceeds the hop, the chart falls behind in real time.

## Testing strategy

- **Pure + unit-testable:** `LiveNoteReconciler` (identity/ongoing/settled across synthetic decode
  sequences), the invisible-rests writer mode (golden MusicXML + the bar-conservation property still
  holds), and the incremental-decode equivalence (decoding the full posteriorgram in one pass vs.
  incrementally yields the same final notes).
- **Device/manual:** the mic → live loop is manual-acceptance (no audio device in CI), like the rest
  of live capture. The Stage-5 acceptance checklist gains a polyphonic live-capture item.
- **Measured, not asserted:** per-window inference time and end-to-end lag on the M3 Max, reported as
  numbers (the render-golden/throughput precedent), not promised.

## Build plan — spike first, then commit

1. **Feasibility spike (gate).** Run the existing Basic Pitch windowed pipeline incrementally over a
   *file* replayed as if live; measure per-window inference time and total lag; eyeball whether the
   placeholder→finalize reconciliation is stable. **Kill criterion:** if inference per window exceeds
   the hop on the M3 Max, or the reconciliation flickers unusably, stop and fall back (keep mono live;
   offer polyphonic only on the batch `transcribe`). Stated up front, like the Stage-4 ONNX gate.
2. **Reconciler + invisible-rests writer** (pure, TDD) once the spike is green.
3. **`LivePolyphonicSession`** + the streaming Infrastructure entry, wired to `listen --view`.
4. **Manual acceptance** on the Mac + measured latency; docs/DECISIONS updated with the honest limits.

## Non-goals

- **True low-latency (<~200 ms) real-time** — impossible with a 2 s-context batch model; out of scope.
- **MIDI input** — would make this trivial and exact, but Cornelius chose acoustic mic; MIDI is a
  separate future option, not this design.
- **Transkun streaming** — Transkun's semi-CRF Viterbi is even less stream-friendly; Basic Pitch is
  the streaming engine.
- **A new accuracy claim** — this feature earns no F1 guarantee; it presents the existing engine live.

---

## Adversarial review (2026-07-12) — blockers the draft must resolve

A four-lens review against the actual code found the draft above too optimistic. The material findings:

**The headline blocker — Cornelius's own example isn't representable.** The polyphonic notation model
is **homophonic-per-staff** (recorded in `DECISIONS.md`, Stage 3): a "chord" is one or more pitches
sharing **one onset AND one duration**. Two notes struck together with *different* lengths — an A held
a half note under a C held a whole note, exactly the stated example — **collapse to a single duration**.
Showing them independently requires **multi-voice** notation (independent voices per staff), which the
Domain model does not have. That is a separate, significant capability, not a detail of this feature.

**"Reuse the pipeline incrementally" is actually a rewrite.**
- `BasicPitchTranscriber.Transcribe` has **no incremental entry point** — it materializes the whole
  source and `RunWindows` restarts from sample 0 each call. Streaming needs a new *stateful, resumable*
  Infrastructure class; only `BasicPitchModel.Run` and `BasicPitchNoteDecoder.Decode` reuse unchanged.
- **Decode is superlinear, not "cheap."** `Decode` clones the full grid and (MelodiaTrick on) runs
  repeated full-grid ArgMax; re-decoding the whole posteriorgram every tick is ~O(T²) over a session.
- `AudioResampler` is **whole-buffer and non-causal**; re-resampling grows with length and *retroactively
  rewrites trailing-edge samples*, a second source of live-boundary instability.

**Reconciliation correctness.**
- The decoder emits **only complete (start,end) notes** and never flags "still sounding." The
  placeholder mechanism must be built by detecting boundary-truncation vs. a confirmed energy-drop
  *inside* the decode walk (thread a `Confirmed` flag) — the draft hand-waved this.
- **Settlement isn't monotone:** global onset-rescaling + latest-first claiming, redone from scratch
  each tick, let a note already marked "settled" shift on a later tick. Needs a debounce or pinning.
- Correction to the draft's rationale: the model does **not** revise earlier frames as context arrives;
  the inter-tick changes are decode-algorithm artifacts, not improved model context.

**Other real costs the draft undersold.**
- `LiveNotationServer` is hard-typed to the mono `Score`; grand-staff needs a new publish surface — an
  Infrastructure change, not "a writer swap."
- Default-swapping `listen --view` regresses the *already-accepted* ~41 ms mono live view to ~2 s (~49×).
- CPU contention between capture and inference can **drop audio frames** — the spike must measure the
  dropped-frame rate, not just inference wall-clock, and constrain the ONNX session's thread count.
- Claims like "no jump on Stop" and "it *is* the streaming behavior" must soften to hypotheses the spike
  tests; the still-open tail note at Stop *will* visibly resolve (inherent, but not "WYSIWYG-satisfied").

## Open decisions this review forces (Cornelius's call)

1. **Multi-voice vs. homophonic.** Your A-half/C-whole example needs multi-voice notation the model
   doesn't have. Options: build multi-voice too (larger scope), accept that simultaneously-struck notes
   share one displayed duration (contradicts the example), or change the input (below).
2. **Effort & feasibility.** The mic path is a genuine Infrastructure rewrite with an uncertain spike —
   and even if it all works, it still can't show your example without decision (1).
3. **Reconsider MIDI.** MIDI input makes polyphony, per-note durations, multi-voice, and low latency all
   **native and exact**, sidestepping every blocker above. It was set aside earlier; the review's findings
   materially change that trade-off, so it is worth an explicit re-decision.

## Decision (Cornelius, 2026-07-12): proceed with the acoustic-mic spike

Cornelius chose to proceed with the mic path despite the findings. The build plan's **spike-first**
discipline therefore governs, and the review's blockers become the spike's explicit risk register:

- **Spike step 1 (the performance kill-gate) — do this before any production code.** Measure, on the
  M3 Max: (a) one `BasicPitchModel.Run` window inference; (b) `BasicPitchNoteDecoder.Decode` cost at
  accumulated frame counts for ~30 s / 1 / 2 / 5 / 10 min sessions (the O(T²) concern); (c) a *bounded*
  decode variant (only a trailing context window + still-open notes) to see whether bounding restores
  ~flat per-tick cost. **Kill/adjust criterion:** if one window's inference exceeds the ~1.64 s hop, or
  bounded decode still can't stay under the hop at 10 min, the near-real-time premise fails — report and
  reconsider (MIDI, or scope down) rather than build on it.
- **Deferred until step 1 passes:** the `Confirmed`-flag placeholder mechanism, the settlement debounce,
  the incremental/causal resampler, the `LiveNotationServer` polyphonic publish surface, and — the
  separate capability the A/C example needs regardless — **multi-voice notation** (flagged, not yet
  scoped; a spike that proves detection feasibility does not require it).

**Doc status: mic path approved; executing spike step 1 (performance) next.**
