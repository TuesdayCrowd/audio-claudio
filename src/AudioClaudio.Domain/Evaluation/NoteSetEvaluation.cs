namespace AudioClaudio.Domain.Evaluation;

/// <summary>
/// The result of comparing a candidate transcription to a reference note-set: the confusion
/// counts and the derived precision/recall/F1. Every candidate note is either a true positive
/// (matched a reference) or a false positive; every reference note is either matched or a false
/// negative — so <c>CandidateCount = TruePositives + FalsePositives</c> and
/// <c>ReferenceCount = TruePositives + FalseNegatives</c>.
/// </summary>
public readonly record struct NoteSetEvaluation(
    int TruePositives,
    int FalsePositives,
    int FalseNegatives,
    int ReferenceCount,
    int CandidateCount)
{
    /// <summary>TP / (TP + FP). 1.0 when there is nothing to be wrong about (no candidates and no references).</summary>
    public double Precision =>
        CandidateCount == 0 ? (ReferenceCount == 0 ? 1.0 : 0.0) : (double)TruePositives / CandidateCount;

    /// <summary>TP / (TP + FN). 1.0 when there are no reference notes and no candidates.</summary>
    public double Recall =>
        ReferenceCount == 0 ? (CandidateCount == 0 ? 1.0 : 0.0) : (double)TruePositives / ReferenceCount;

    /// <summary>Harmonic mean of precision and recall; 0 when either is 0.</summary>
    public double F1
    {
        get
        {
            double p = Precision;
            double r = Recall;
            return (p + r) == 0.0 ? 0.0 : 2.0 * p * r / (p + r);
        }
    }
}
