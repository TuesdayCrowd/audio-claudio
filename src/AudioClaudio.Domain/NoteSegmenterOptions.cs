namespace AudioClaudio.Domain;

/// <summary>Parameters for turning onsets + a pitch track into NoteEvents (R5.2–R5.4).</summary>
public sealed record NoteSegmenterOptions
{
    /// <summary>
    /// Notes shorter than this are discarded as flicker (R5.3). Expressed in integer
    /// samples via <see cref="SampleDuration"/>; the CLI derives it from ~50 ms and the
    /// sample rate. Required — a duration without its rate is a bug (non-negotiable 1).
    /// </summary>
    public required SampleDuration MinNoteDuration { get; init; }

    /// <summary>
    /// A note's pitch is committed only after this many consecutive voiced frames agree
    /// on one MIDI number, so attack-transient flicker cannot mislabel it (R5.2, R5.3).
    /// </summary>
    public int StabilityFrames { get; init; } = 2;

    /// <summary>
    /// If &gt; 0, a note ends when its level falls below this fraction of its peak level
    /// (the decay-below-floor condition, R5.2). 0 disables the amplitude floor, leaving
    /// termination to the next onset or the unvoiced transition. The default is 0
    /// (disabled), so the shipped default never exercises R5.2's third termination path;
    /// enabling it with a sensible ratio is a follow-up obligation on the Step 9/Step 10
    /// composition root (see the R5.2 requirements-coverage row), not something Step 5
    /// turns on by default.
    /// </summary>
    public double DecayFloorRatio { get; init; } = 0.0;

    /// <summary>Constant MVP velocity for emitted NoteEvents (R1.4).</summary>
    public int Velocity { get; init; } = 64;
}
