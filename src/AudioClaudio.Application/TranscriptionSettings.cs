using AudioClaudio.Domain;

namespace AudioClaudio.Application;

/// <summary>
/// Every pipeline knob in one place (R2.4: frame/hop are parameters, not scattered
/// constants — extended here to the rest of the pipeline's tunables). The defaults are
/// the values the Step 9 closed-loop suite settled on against real MeltySynth-rendered
/// piano audio; see <c>TranscriptionPipeline</c>'s remarks for the tuning rationale.
/// </summary>
public sealed record TranscriptionSettings
{
    /// <summary>Analysis window length N in samples. Must be a power of two.</summary>
    public int FrameSize { get; init; } = 2048;

    /// <summary>Hop H in samples between successive frames.</summary>
    public int Hop { get; init; } = 512;

    /// <summary>YIN CMND threshold separating voiced from unvoiced (Step 4).</summary>
    public double YinThreshold { get; init; } = 0.15;

    /// <summary>Adaptive spectral-flux peak threshold multiplier → `OnsetDetectorOptions.ThresholdMultiplier` (Step 5).</summary>
    public double OnsetThreshold { get; init; } = 1.5;

    /// <summary>Minimum spacing between accepted onsets, in frames → `OnsetDetectorOptions.MinGapFrames`.</summary>
    public int OnsetMinGapFrames { get; init; } = 3;

    /// <summary>Flicker floor as milliseconds; converted to integer samples at the edge → `NoteSegmenterOptions.MinNoteDuration` (R5.3).</summary>
    public double MinNoteMilliseconds { get; init; } = 50.0;

    /// <summary>Consecutive voiced frames required to commit a note's pitch → `NoteSegmenterOptions.StabilityFrames`.</summary>
    public int StabilityFrames { get; init; } = 2;

    /// <summary>
    /// If &gt; 0, a note ends when its level falls below this fraction of its peak level
    /// (R5.2's decay-below-floor termination) → `NoteSegmenterOptions.DecayFloorRatio`. Left
    /// disabled (0) for the closed loop: a real piano note's attack transient towers over its
    /// own sustain level (by a pitch-dependent amount), so a floor relative to the ATTACK PEAK
    /// cannot separate "still sustaining" from "released" consistently across the keyboard —
    /// see <see cref="OffsetSettleFrames"/>/<see cref="OffsetReleaseRatio"/> for the mechanism
    /// that replaces it.
    /// </summary>
    public double DecayFloorRatio { get; init; }

    /// <summary>
    /// Offset refinement (Application-layer, applied after Step 5's segmentation — see
    /// <c>TranscriptionPipeline.RefineOffsets</c>): how many frames after a note's onset to
    /// sample its reference level. Deliberately SMALL (early — ~58 ms at 512-sample hop /
    /// 44.1 kHz): the reference must be taken just past the attack transient but well before
    /// any plausible note-off, so it captures the note's loud early-sustain level rather than a
    /// heavily-decayed near-note-off level. An earlier tuning used 20 frames (232 ms), which for
    /// a short (eighth-note) note fell almost AT the note-off, driving the reference — and hence
    /// the release threshold — absurdly low and causing gross overshoot; the fix was this early
    /// reference (DECISIONS.md, "Step 9").
    /// </summary>
    public int OffsetSettleFrames { get; init; } = 5;

    /// <summary>
    /// A note's audible end is the first point after <see cref="OffsetSettleFrames"/> where its
    /// level falls below this fraction of the early reference level. 0.50 (with the corpus's
    /// audible-duration cap derived at the SAME ratio) makes the constrained closed loop recover
    /// DURATION within tolerance across the corpus: high enough that a low note's gradual damped
    /// release crosses it promptly after note-off (no overshoot), while the cap guarantees the held
    /// note stays above it until note-off (no undershoot). Chosen by a joint (settle, ratio, margin)
    /// sweep over the real pipeline (DECISIONS.md).
    /// </summary>
    public double OffsetReleaseRatio { get; init; } = 0.50;

    /// <summary>
    /// A note is only considered ended where the level stays below the release threshold for this
    /// many CONSECUTIVE frames — a debounce. Without it, a single momentary energy dip ends the
    /// note early: two low notes a semitone or so apart overlap acoustically (a low piano note
    /// rings on through the rest into the next), and adjacent partials BEAT, oscillating the RMS
    /// so it briefly dips below threshold well before the note-off. Requiring persistence rejects
    /// those transient beats while still catching the real (monotonic) release. A single held note
    /// decays monotonically, so this does not change the audible-duration measurement.
    /// </summary>
    public int OffsetPersistFrames { get; init; } = 3;

    /// <summary>User-declared tempo (R6.3): MVP never estimates it.</summary>
    public required double TempoBpm { get; init; }

    public TimeSignature TimeSignature { get; init; } = TimeSignature.FourFour;

    public Subdivision Subdivision { get; init; } = Subdivision.Sixteenth;

    public static TranscriptionSettings ForTempo(double bpm) => new() { TempoBpm = bpm };
}
