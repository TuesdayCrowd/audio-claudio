namespace AudioClaudio.Domain;

/// <summary>
/// Combines detected onsets with the per-frame pitch track to produce discrete
/// NoteEvents (R5.2). Each onset opens a note; <b>within that onset's span, a legato
/// pitch change</b> — a new stable pitch reached with no re-attack of its own — opens a
/// further note (v2 Stage 2). Each note is labelled with its stable voiced pitch and
/// closed at a transition to unvoiced or a different pitch. Pure and deterministic
/// (non-negotiable 3).
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
            int spanStart = onsetFrames[i];
            int spanLimit = i + 1 < onsetFrames.Count ? onsetFrames[i + 1] : n;

            // Walk the span, emitting one note per stable-pitch RUN. The first run's note is timestamped
            // at the onset (the detected attack). A later run of a DIFFERENT stable pitch is a LEGATO
            // transition — connected notes with no re-attack of their own — so it becomes a new note
            // starting at the pitch change. A run whose pitch equals the note just emitted (a YIN wobble
            // returning to the same pitch, or the still-voiced tail after a decay-floor cut) is skipped,
            // so a momentary flicker never splits one note into two (R5.3/R5.4 preserved). On the
            // closed-loop corpus — every note followed by an unvoiced rest, no in-span pitch change — this
            // emits exactly one note per onset, unchanged.
            int cursor = spanStart;
            int emittedInSpan = 0;
            Pitch lastEmitted = default;
            bool firstRun = true;   // only the span's FIRST run may claim the onset frame (the attack)
            while (cursor < spanLimit)
            {
                (Pitch Pitch, int StableStart)? stable = FindStablePitch(frames, cursor, spanLimit);
                if (stable is not { } found)
                {
                    break;   // no (more) stable voiced pitch remaining in this span
                }

                int endFrame = FindEndFrame(frames, found.StableStart, spanLimit, found.Pitch);

                long endSamples = endFrame < n
                    ? frames[endFrame].Start.Samples
                    : frames[n - 1].Start.Samples + hop;

                // The span's FIRST run carries the onset (the attack transient may precede the stable
                // pitch, so duration is measured from the onset — exactly the pre-legato behavior). A
                // legato run starts at its own pitch-change boundary (there onset == StableStart, so this
                // is its run length). The `firstRun` flag — not "nothing emitted yet" — is what claims the
                // onset, so a dropped short first run can never lend its span-start to a later wobble run.
                SamplePosition onset = firstRun ? frames[spanStart].Start : frames[found.StableStart].Start;
                long durationSamples = endSamples - onset.Samples;

                bool sameAsPrevious = emittedInSpan > 0 && found.Pitch.MidiNumber == lastEmitted.MidiNumber;
                if (sameAsPrevious)
                {
                    // A wobble returning to the note's pitch (or the still-voiced tail after a decay cut):
                    // don't split — extend the note just emitted through this run rather than dropping it.
                    NoteEvent prev = events[^1];
                    events[^1] = new NoteEvent(
                        prev.Pitch, prev.Onset, new SampleDuration(endSamples - prev.Onset.Samples, rate), prev.Velocity);
                }
                else if (durationSamples >= _options.MinNoteDuration.Samples)
                {
                    events.Add(new NoteEvent(
                        found.Pitch, onset, new SampleDuration(durationSamples, rate), _options.Velocity));
                    lastEmitted = found.Pitch;
                    emittedInSpan++;
                }

                firstRun = false;
                cursor = endFrame > cursor ? endFrame : cursor + 1;   // guarantee forward progress

                // Without legato recovery, only the span's FIRST stable run becomes a note (one note per
                // onset — the proven default). Legato recovery continues the walk to catch in-span pitch
                // changes.
                if (!_options.RecoverLegato)
                {
                    break;
                }
            }
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
