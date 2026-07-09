namespace AudioClaudio.Domain.Evaluation;

/// <summary>
/// Tolerances for matching a candidate note to a reference note (<see cref="TranscriptionEvaluator"/>).
/// Onset tolerance is stated in <b>seconds</b>, not samples, deliberately: a candidate and a
/// reference may be denominated at different sample rates (e.g. a 22.05 kHz model output vs a
/// 44.1 kHz reference), so the comparison happens in the one common, perceptual unit — the same
/// edge conversion <see cref="SamplePosition.ToSeconds"/> exists for. Offsets are ignored by
/// default: a <i>performance</i>'s note durations (rubato, pedal) need not match a <i>score</i>'s.
/// </summary>
public sealed record NoteMatchOptions(double OnsetToleranceSeconds)
{
    /// <summary>±50 ms onset window, exact pitch — the standard MIR note-onset tolerance.</summary>
    public static NoteMatchOptions Default { get; } = new(0.05);
}
