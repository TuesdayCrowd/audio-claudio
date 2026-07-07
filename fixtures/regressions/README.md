<!-- fixtures/regressions/README.md -->
# Closed-loop regression corpus

Each `*.mid` here is a previously-failing closed-loop case (the *generated performance*,
written by `Quarantine.Persist`). `RegressionCorpusTests` re-runs every one through
`transcribe . synthesize` and asserts it still returns within tolerance.

## Promotion procedure (R9.3)
1. The closed-loop suite fails and quarantines `<id>.mid` + `<id>.wav` under
   `artifacts/closed-loop-quarantine/`.
2. Reproduce, then fix the underlying Step 3-8 bug.
3. Copy the quarantined `<id>.mid` into this directory (the WAV is regenerated
   deterministically by the synth, so only the MIDI is committed).
4. Commit. The corpus only ever grows — the suite only ever gets harder.
