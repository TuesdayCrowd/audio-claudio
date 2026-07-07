namespace AudioClaudio.Domain;

/// <summary>
/// Combines detected onsets with the per-frame pitch track to produce discrete
/// NoteEvents (R5.2). Each onset opens a note; the note is labelled with the first
/// stable voiced pitch found at/after the onset and closed at a transition to
/// unvoiced or a different pitch. Pure and deterministic (non-negotiable 3).
/// </summary>
public sealed class NoteSegmenter
{
    private readonly NoteSegmenterOptions _options;

    public NoteSegmenter(NoteSegmenterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Segments a pitch track into NoteEvents. <paramref name="frames"/> holds one
    /// observation per analysis frame in order; <paramref name="onsetFrames"/> holds
    /// onset frame indices (from <see cref="OnsetDetector.Detect"/>) in ascending order.
    /// </summary>
    public IReadOnlyList<NoteEvent> Segment(
        IReadOnlyList<FrameObservation> frames,
        IReadOnlyList<int> onsetFrames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(onsetFrames);

        var events = new List<NoteEvent>();
        int n = frames.Count;
        if (n == 0 || onsetFrames.Count == 0)
        {
            return events;
        }

        SampleRate rate = frames[0].Start.Rate;
        long hop = n >= 2 ? frames[1].Start.Samples - frames[0].Start.Samples : 0;

        for (int i = 0; i < onsetFrames.Count; i++)
        {
            int startFrame = onsetFrames[i];
            int limit = i + 1 < onsetFrames.Count ? onsetFrames[i + 1] : n;

            (Pitch Pitch, int StableStart)? stable = FindStablePitch(frames, startFrame, limit);
            if (stable is not { } found)
            {
                continue;   // spurious onset with no stable voiced pitch → drop
            }

            int endFrame = FindEndFrame(frames, found.StableStart, limit, found.Pitch);

            SamplePosition onset = frames[startFrame].Start;
            long endSamples = endFrame < n
                ? frames[endFrame].Start.Samples
                : frames[n - 1].Start.Samples + hop;
            long durationSamples = endSamples - onset.Samples;

            if (durationSamples < _options.MinNoteDuration.Samples)
            {
                continue;   // shorter than the minimum note duration → flicker (R5.3)
            }

            events.Add(new NoteEvent(
                found.Pitch,
                onset,
                new SampleDuration(durationSamples, rate),
                _options.Velocity));
        }

        return events;
    }

    /// <summary>
    /// Returns the first pitch that stays constant across StabilityFrames consecutive
    /// voiced frames in [start, limit), together with the frame that run begins on;
    /// or null if no such run exists.
    /// </summary>
    private (Pitch Pitch, int StableStart)? FindStablePitch(
        IReadOnlyList<FrameObservation> frames, int start, int limit)
    {
        int runStart = -1;
        Pitch runPitch = default;
        int runLength = 0;

        for (int j = start; j < limit; j++)
        {
            if (frames[j].Pitch is not { } p)
            {
                runLength = 0;
                continue;
            }

            if (runLength > 0 && p.MidiNumber == runPitch.MidiNumber)
            {
                runLength++;
            }
            else
            {
                runPitch = p;
                runStart = j;
                runLength = 1;
            }

            if (runLength >= _options.StabilityFrames)
            {
                return (runPitch, runStart);
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the first frame in [stableStart, limit) at which the note terminates —
    /// a transition to unvoiced, a change of pitch, or (when DecayFloorRatio &gt; 0) a
    /// level below that fraction of the note's running peak — or <paramref name="limit"/>
    /// if it never terminates within the window.
    /// </summary>
    private int FindEndFrame(
        IReadOnlyList<FrameObservation> frames, int stableStart, int limit, Pitch pitch)
    {
        double peak = 0.0;

        for (int j = stableStart; j < limit; j++)
        {
            FrameObservation f = frames[j];
            if (f.Pitch is not { } p || p.MidiNumber != pitch.MidiNumber)
            {
                return j;   // transition to unvoiced or a different pitch
            }

            if (f.Energy > peak)
            {
                peak = f.Energy;
            }

            if (_options.DecayFloorRatio > 0.0 && peak > 0.0 &&
                f.Energy < peak * _options.DecayFloorRatio)
            {
                return j;   // decayed below the amplitude floor (R5.2)
            }
        }

        return limit;
    }
}
