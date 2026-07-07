namespace AudioClaudio.Domain;

/// <summary>
/// The per-frame evidence the segmenter consumes: the frame's starting position,
/// the voiced pitch (or <c>null</c> when the YIN detector reported unvoiced, R4.1),
/// and a non-negative per-frame level (e.g. RMS amplitude) used for the
/// decay-below-floor end condition (R5.2). Voicing is encoded by <see cref="Pitch"/>
/// being non-null. <see cref="Pitch"/> is assumed to be a value type (Section 1's
/// contract: <c>Pitch { int MidiNumber }</c>).
/// </summary>
public readonly record struct FrameObservation(SamplePosition Start, Pitch? Pitch, double Energy)
{
    /// <summary>True when this frame carries a voiced pitch.</summary>
    public bool IsVoiced => Pitch.HasValue;
}
