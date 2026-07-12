using System;
using System.Collections.Generic;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Evaluation;

namespace AudioClaudio.Cli.Commands;

/// <summary>
/// The <c>evaluate</c> command: score a candidate transcription's notes against a reference
/// note-set and print precision/recall/F1 — the yardstick for "does the audio transcription
/// match the score?". The comparison itself is the Domain <see cref="TranscriptionEvaluator"/>;
/// this command only reads the two note lists (from MIDI, in the composition root) and formats
/// the report.
/// </summary>
public static class EvaluateCommand
{
    public static NoteSetEvaluation Run(
        IReadOnlyList<NoteEvent> candidate,
        IReadOnlyList<NoteEvent> reference,
        NoteMatchOptions options,
        Action<string> print)
    {
        ArgumentNullException.ThrowIfNull(print);
        NoteSetEvaluation e = TranscriptionEvaluator.Evaluate(candidate, reference, options);

        print($"Reference notes: {e.ReferenceCount}");
        print($"Candidate notes: {e.CandidateCount}");
        print($"Matched (TP):    {e.TruePositives}");
        print($"Missed  (FN):    {e.FalseNegatives}");
        print($"Extra   (FP):    {e.FalsePositives}");
        print($"Precision:       {CliFormat.Percent(e.Precision)}");
        print($"Recall:          {CliFormat.Percent(e.Recall)}");
        print($"F1:              {CliFormat.Percent(e.F1)}");
        print($"(onset tolerance ±{options.OnsetToleranceSeconds * 1000.0:F0} ms, exact pitch, offsets ignored)");
        return e;
    }
}
