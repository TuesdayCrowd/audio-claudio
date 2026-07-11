<!-- fixtures/regressions/polyphonic/README.md -->
# Polyphonic closed-loop regression corpus

Each `*.mid` here is a previously-failing *polyphonic* closed-loop case (a chord score that once dragged
the gate's F1 below 0.75). `PolyphonicRegressionCorpusTests` re-synthesizes and re-transcribes every one
and asserts it still clears the gate's per-case F1 bar (≥ 0.75 @ ±50 ms).

Kept in this **subdirectory** so the monophonic `RegressionCorpusTests` — which scans only the top level of
`fixtures/regressions/` and runs each `.mid` through the one-note-at-a-time YIN pipeline — never picks up a
chord fixture (a polyphonic MIDI cannot survive an exact monophonic comparator).

## Promotion procedure (v2 Stage 1)
1. The polyphonic gate fails and quarantines the worst `<id>.mid` + `<id>.wav` under
   `artifacts/closed-loop-quarantine/polyphonic/`.
2. Reproduce, then fix the underlying decoder/engine cause.
3. Copy the quarantined `<id>.mid` into this directory (the WAV is regenerated deterministically by the
   synth, so only the MIDI is committed).
4. Commit. The corpus only ever grows — the suite only ever gets harder.
