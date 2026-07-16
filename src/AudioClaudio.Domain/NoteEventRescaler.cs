namespace AudioClaudio.Domain;

/// <summary>
/// Converts a note list declared at one <see cref="SampleRate"/> into another by scaling onset/
/// duration sample counts by the exact rate ratio -- e.g. 2x between a transcriber's internal rate
/// (Basic Pitch's 22 050 Hz, Transkun's own rate) and a common downstream rate such as 44 100 Hz.
/// Pure and BCL-only (no I/O, no clock) -- it performs the EXPLICIT, exact conversion that the
/// Domain's mixed-sample-rate non-negotiable (CLAUDE.md &#167;4) requires before notes produced by
/// transcribers running at different internal rates can be merged or handed to rate-sensitive
/// helpers (e.g. <c>ISynthesizer.Render</c>) that require notes and audio to share ONE declared
/// rate. Originally a private helper on <c>LivePolyphonicView</c> (the live-poly `--record` path);
/// lifted here (multi-instrument Stage 2) so the live path AND the batch per-stem path
/// (<c>MultiStemTranscriber</c>) share one implementation.
/// </summary>
public static class NoteEventRescaler
{
    /// <summary>
    /// Rescales every note in <paramref name="notes"/> to <paramref name="targetRate"/>. A no-op
    /// (returns <paramref name="notes"/> unchanged) when the list is empty. A duration that would
    /// round to zero samples is clamped to at least one (the domain requires a sounding note to
    /// have positive length).
    /// </summary>
    public static IReadOnlyList<NoteEvent> Rescale(IReadOnlyList<NoteEvent> notes, SampleRate targetRate)
    {
        ArgumentNullException.ThrowIfNull(notes);
        if (notes.Count == 0)
        {
            return notes;
        }

        var rescaled = new List<NoteEvent>(notes.Count);
        foreach (NoteEvent n in notes)
        {
            double ratio = (double)targetRate.Hz / n.Onset.Rate.Hz;
            long onset = (long)Math.Round(n.Onset.Samples * ratio);
            long duration = Math.Max(1, (long)Math.Round(n.Duration.Samples * ratio));
            rescaled.Add(new NoteEvent(
                n.Pitch, new SamplePosition(onset, targetRate), new SampleDuration(duration, targetRate), n.Velocity));
        }

        return rescaled;
    }
}
