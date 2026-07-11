using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;

namespace AudioClaudio.Application;

/// <summary>
/// Composes Steps 3-6 into a single audio-to-score transform. Pure with respect to the
/// domain: it constructs only Domain algorithm objects (the FFT is injected as the Domain
/// interface <see cref="IFourierTransform"/>) and never touches Infrastructure or the wall
/// clock.
/// </summary>
public sealed class TranscriptionPipeline : ITranscriber
{
    private const int FallbackSampleRateHz = 44100;

    private readonly TranscriptionSettings _settings;
    private readonly IFourierTransform _fft;

    // FFT injected (CONTRACTS §3): under Option A the test/composition root hands us a
    // Domain `new Radix2Fft()`; under Option B it would hand us an Infrastructure adapter.
    // Either way Application never references the concrete FFT — the dependency rule
    // stays physical.
    public TranscriptionPipeline(TranscriptionSettings settings, IFourierTransform fft)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(fft);
        _settings = settings;
        _fft = fft;
    }

    public TranscriptionResult Transcribe(IAudioSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Pull all frames once (Step 2 pull model, `Frames` is a property). Deterministic
        // order in => deterministic output.
        var frames = source.Frames.ToList();
        if (frames.Count == 0)
        {
            // No audio: a real WAV always yields >= 1 frame; guard so the grid below has a rate.
            var emptyGrid = new QuantizationGrid(
                new SampleRate(FallbackSampleRateHz), new Tempo(_settings.TempoBpm),
                _settings.TimeSignature, _settings.Subdivision);
            return new TranscriptionResult(
                Quantizer.Quantize(Array.Empty<NoteEvent>(), emptyGrid), Array.Empty<NoteEvent>());
        }

        var rate = frames[0].Rate; // Step 2: Frame.Rate => Start.Rate
        var frontEnd = new SpectralFrontEnd(_settings.FrameSize, _fft); // Step 3 (FFT injected)
        var yinOptions = new YinOptions(threshold: _settings.YinThreshold); // Step 4

        // Per-frame: YIN estimate, magnitude spectrum, and a FrameObservation for the segmenter.
        // Per-frame: YIN estimate, magnitude spectrum, and a FrameObservation for the segmenter. Plain
        // YIN (no continuity): the pYIN-lite octave-correction seam (YinPitchDetector.Detect's previous
        // param + ApplyContinuity) exists and is unit-tested, but it is NOT wired here — a causal
        // continuity correction cannot be fed safely by this pipeline (it fights real octave leaps next to
        // a ringing note and homogenizes the pitch track the segmenter relies on), so the proven plain-YIN
        // path stays the default. See DECISIONS.md, "pYIN-lite".
        var magnitudeSpectra = new List<IReadOnlyList<double>>(frames.Count);
        var observations = new List<FrameObservation>(frames.Count);
        foreach (var frame in frames)
        {
            PitchEstimate estimate = YinPitchDetector.Detect(frame, yinOptions); // Step 4 (static)
            MagnitudeSpectrum spectrum = frontEnd.Analyze(frame);

            magnitudeSpectra.Add(spectrum.Magnitudes);

            double energy = Rms(frame.Samples);
            Pitch? pitch = GuardedPitchFromEstimate(estimate);
            observations.Add(new FrameObservation(frame.Start, pitch, energy));
        }

        // Step 5 is two stages: OnsetDetector finds attack frames, NoteSegmenter bounds notes.
        IReadOnlyList<int> onsetFrames = new OnsetDetector(new OnsetDetectorOptions
        {
            ThresholdMultiplier = _settings.OnsetThreshold,
            MinGapFrames = _settings.OnsetMinGapFrames,
        }).Detect(magnitudeSpectra);

        long minSamples = (long)Math.Round(_settings.MinNoteMilliseconds / 1000.0 * rate.Hz);
        var segmenter = new NoteSegmenter(new NoteSegmenterOptions
        {
            MinNoteDuration = new SampleDuration(minSamples, rate), // R5.3 flicker floor
            StabilityFrames = _settings.StabilityFrames,
            RecoverLegato = _settings.RecoverLegato, // v2 Stage 2: opt-in legato recovery (default off)
            // R5.2's decay-below-floor is the composition root's call (Step 5's R5.2 coverage
            // row); disabled here (TranscriptionSettings.DecayFloorRatio defaults to 0) in favor
            // of the RefineOffsets post-process below — see its remarks for why.
            DecayFloorRatio = _settings.DecayFloorRatio,
        });

        IReadOnlyList<NoteEvent> events = segmenter.Segment(observations, onsetFrames);
        events = RefineOffsets(
            events, observations, _settings.OffsetSettleFrames, _settings.OffsetReleaseRatio,
            _settings.OffsetPersistFrames, rate);

        // Stage 2 (v2): stamp a real per-note velocity from the attack energy, so the score carries
        // dynamics (pp..ff) instead of a flat constant. A pure 1:1 relabel — same count/pitch/onset/
        // duration — so the closed loop's R9.2 checks are untouched.
        events = RefineVelocities(events, observations, _settings.VelocityAttackFrames);

        // Step 6: build the grid and quantize (static, no `new Quantizer()`). Tempo is estimated
        // from the just-detected events when requested — valid because detection is tempo-independent
        // (CLAUDE.md background) and tempo is only ever consumed here, at quantization.
        Tempo tempo = _settings.EstimateTempo
            ? TempoEstimator.Estimate(events, new Tempo(_settings.TempoBpm))
            : new Tempo(_settings.TempoBpm);
        var grid = new QuantizationGrid(rate, tempo, _settings.TimeSignature, _settings.Subdivision);
        // Coarse-grid note-off (opt-in): snap note values to an eighth-note grid (half a quarter beat). Off
        // by default (coarseGrid = 0 → the proven full standard-value set the closed loop runs on).
        int coarseGrid = _settings.CoarseRhythm ? grid.TicksPerBeat / 2 : 0;
        Score score = Quantizer.Quantize(events, grid, coarseGrid);

        return new TranscriptionResult(score, events);
    }

    /// <summary>
    /// Incremental, low-latency note feed for the live <c>listen</c> view (Step 10, R10.3).
    /// Genuinely lazy and causal: it pulls frames from <paramref name="source"/> ONE AT A TIME
    /// and <c>yield return</c>s a <see cref="NoteEvent"/> shortly after each note is detected — it
    /// is deliberately NOT a batch dump. <see cref="Transcribe"/> is the accurate whole-signal pass
    /// the live session runs on stop for the output files, and it alone owns duration refinement.
    /// Here a live note is reported at its ONSET with the detected pitch and a PROVISIONAL duration
    /// (the flicker-floor minimum) — the live print wants the pitch and timing of the attack, not a
    /// finalized note-off.
    ///
    /// Mechanism (causal analogues of the batch Step 5 blocks, so no detection logic is duplicated
    /// in the capture adapter — R10.4). Per frame: Hann+FFT magnitude (<see cref="SpectralFrontEnd"/>),
    /// one YIN estimate (<see cref="YinPitchDetector"/>), and the half-wave-rectified spectral flux
    /// vs. the previous frame (<see cref="SpectralFlux"/>'s definition, one pair). Then a two-stage
    /// onset→pitch machine:
    ///  * <b>onset peak</b> — a candidate frame <c>c</c> is confirmed once <c>c + lookahead</c> has
    ///    arrived if its flux is a local maximum, stands out from its local mean by
    ///    <c>multiplier</c> (a scale-invariant RATIO — the batch's global-max normalization is not
    ///    available causally), clears a running-max silence floor, and is <c>MinGapFrames</c> past
    ///    the last onset. This opens a <i>pending</i> onset;
    ///  * <b>pitch settle + sustain</b> — the note is emitted only once a single voiced pitch has
    ///    SUSTAINED <c>minVoiced</c> frames past the onset. This is load-bearing: YIN reads
    ///    <i>unvoiced</i> on the partial attack frames (the pitch locks a few frames late), and a
    ///    note OFFSET makes its own flux spike (broadband leakage from truncating the tone) that
    ///    would otherwise be a false onset — but that "note" goes unvoiced immediately, so it never
    ///    sustains and is dropped. Same principle as the batch segmenter's flicker floor (R5.3).
    /// Latency: the onset is <i>known</i> within one frame + <c>lookahead</c> hops (~41 ms at the
    /// live defaults — see <c>LatencyBudget</c>); the note is <i>emitted</i> once its pitch has
    /// sustained, adding <c>minVoiced</c> hops (well under the R10.2 150 ms budget). Deterministic:
    /// every tie-break is defined and there is no clock read.
    /// </summary>
    public IEnumerable<NoteEvent> StreamNotes(IAudioSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var frontEnd = new SpectralFrontEnd(_settings.FrameSize, _fft);
        var yinOptions = new YinOptions(threshold: _settings.YinThreshold);

        // Reuse the Domain onset defaults (window/delta/radius) so the live picker's shape
        // matches the batch OnsetDetector exactly — only the multiplier and min-gap are
        // settings-driven (as in Transcribe). No magic numbers.
        var onsetDefaults = new OnsetDetectorOptions
        {
            ThresholdMultiplier = _settings.OnsetThreshold,
            MinGapFrames = _settings.OnsetMinGapFrames,
        };
        int window = onsetDefaults.ThresholdWindowFrames;
        double delta = onsetDefaults.ThresholdDelta;
        int radius = Math.Max(0, onsetDefaults.LocalMaxRadiusFrames);
        double multiplier = onsetDefaults.ThresholdMultiplier;
        int minGap = onsetDefaults.MinGapFrames;
        int stabilityFrames = Math.Max(1, _settings.StabilityFrames);
        int lookahead = Math.Max(radius, _settings.OnsetLookaheadFrames);

        // A live note must show a single voiced pitch SUSTAINED this many frames before it is
        // emitted — enough to clear the attack transient and reject a note-offset's flux leakage,
        // small enough to stay well inside the R10.2 latency budget.
        int minVoiced = Math.Max(stabilityFrames + 1, 5);
        int hardCap = window + minVoiced; // give up on a pending onset that never locks a pitch

        var flux = new List<double>();
        var observations = new List<FrameObservation>();
        double maxFlux = 0.0;
        int lastOnset = int.MinValue / 2; // no previous onset; avoids sentinel-subtraction overflow
        IReadOnlyList<double>? previousMagnitudes = null; // prior frame's magnitudes (distinct array each frame)

        // Pure peak test on RAW flux (a local function cannot yield): is candidate frame c a
        // spectral-flux onset? A scale-invariant RATIO threshold (stands out from its local mean by
        // `multiplier`) replaces the batch's global-max normalization — not available causally, and
        // actively wrong here: the first note (starting at sample 0, a whole frame inside the note)
        // makes one huge flux spike, while every later note starts mid-frame from a silence gap and
        // ramps in over two frames at ~half that magnitude, so normalizing against the first spike
        // buries the genuine later onsets. A running-max FLOOR rejects near-silence numerical blips.
        // Pitch is NOT decided here — the pending state machine below owns that.
        bool IsOnsetPeak(int c, int available)
        {
            if (maxFlux <= 0.0)
            {
                return false;
            }

            double value = flux[c];

            // Local maximum: strictly greater than left neighbours, >= right neighbours
            // (first frame of a plateau wins — matches OnsetDetector, non-negotiable 3).
            for (int j = c - radius; j <= c + radius; j++)
            {
                if (j < 0 || j > available || j == c)
                {
                    continue;
                }

                double neighbour = flux[j];
                if (j < c && value <= neighbour)
                {
                    return false;
                }

                if (j > c && value < neighbour)
                {
                    return false;
                }
            }

            // Prominence: exceed `multiplier` times the local mean over as much of
            // [c-window, c+window] as has arrived (causal ratio test — scale-invariant).
            double sum = 0.0;
            int count = 0;
            for (int j = c - window; j <= c + window; j++)
            {
                if (j < 0 || j > available)
                {
                    continue;
                }

                sum += flux[j];
                count++;
            }

            double localMean = count > 0 ? sum / count : 0.0;
            if (value < multiplier * localMean)
            {
                return false;
            }

            // Silence floor: reject tiny flux blips in near-silence (their local ratio can be large
            // even though nothing was played). `delta` is a fraction of the loudest onset so far.
            return value >= delta * maxFlux;
        }

        // Emit a live note at its ONSET with the sustained pitch and a PROVISIONAL duration (the
        // flicker-floor minimum); the batch pass owns the finalized note-off.
        NoteEvent MakeNote(Pitch pitch, int onsetFrame)
        {
            SamplePosition onset = observations[onsetFrame].Start;
            long provisional = Math.Max(1, (long)Math.Round(_settings.MinNoteMilliseconds / 1000.0 * onset.Rate.Hz));
            return new NoteEvent(pitch, onset, new SampleDuration(provisional, onset.Rate), NoteEvent.DefaultVelocity);
        }

        int pendingOnset = -1; // an onset whose flux peak is confirmed but whose pitch is still settling
        int i = -1;
        foreach (Frame frame in source.Frames)
        {
            i++;

            MagnitudeSpectrum spectrum = frontEnd.Analyze(frame);
            IReadOnlyList<double> magnitudes = spectrum.Magnitudes; // distinct array per frame — safe to retain

            double f = 0.0;
            for (int k = 0; k < magnitudes.Count; k++)
            {
                double prev = previousMagnitudes is not null && k < previousMagnitudes.Count ? previousMagnitudes[k] : 0.0;
                double diff = magnitudes[k] - prev;
                if (diff > 0.0)
                {
                    f += diff; // half-wave-rectified spectral flux (SpectralFlux.Compute, one pair)
                }
            }

            flux.Add(f);
            if (f > maxFlux)
            {
                maxFlux = f;
            }

            previousMagnitudes = magnitudes;

            // Live uses plain YIN (no continuity): pYIN-lite's octave correction needs onset-aware reset,
            // which the batch Transcribe pass owns — and the live session's SAVED files come from that pass.
            PitchEstimate estimate = YinPitchDetector.Detect(frame, yinOptions);
            observations.Add(new FrameObservation(frame.Start, GuardedPitchFromEstimate(estimate), Rms(frame.Samples)));

            // (1) Onset peak: confirm the candidate `lookahead` frames back. A fresh peak opens a
            // pending onset; any still-unconfirmed pending is abandoned (it never sustained).
            int candidate = i - lookahead;
            if (candidate >= 0 && candidate - lastOnset >= minGap && IsOnsetPeak(candidate, i))
            {
                lastOnset = candidate;
                pendingOnset = candidate;
            }

            // (2) Pitch settle + sustain: emit once a single voiced pitch has held `minVoiced`
            // frames past the onset; drop the pending if the voicing dies too short (leakage /
            // flicker) or never locks within the cap.
            if (pendingOnset >= 0)
            {
                if (SustainedPitch(observations, pendingOnset, i, minVoiced) is { } pitch)
                {
                    yield return MakeNote(pitch, pendingOnset);
                    pendingOnset = -1;
                }
                else
                {
                    bool currentVoiced = observations[i].Pitch is not null;
                    bool seenVoiced = AnyVoiced(observations, pendingOnset, i);
                    if ((seenVoiced && !currentVoiced) || (i - pendingOnset > hardCap))
                    {
                        pendingOnset = -1;
                    }
                }
            }
        }

        // Flush at end-of-stream: scan the final `lookahead` frames for a late onset, then emit a
        // still-pending note under a relaxed bar (cut short by the stop, not spurious).
        for (int c = Math.Max(0, i - lookahead + 1); c <= i; c++)
        {
            if (c - lastOnset >= minGap && IsOnsetPeak(c, i))
            {
                lastOnset = c;
                pendingOnset = c;
            }
        }

        if (pendingOnset >= 0 && SustainedPitch(observations, pendingOnset, i, stabilityFrames) is { } tail)
        {
            yield return MakeNote(tail, pendingOnset);
        }
    }

    /// <summary>
    /// The first voiced pitch whose run of consecutive same-pitch frames reaches
    /// <paramref name="minRun"/> within <c>[onset, available]</c>, or null. The live detector's
    /// "the pitch has settled and sustained" test (a causal cousin of <see cref="NoteSegmenter"/>'s
    /// stability rule): a wrong pitch flickering during the attack never reaches the run length.
    /// </summary>
    private static Pitch? SustainedPitch(
        IReadOnlyList<FrameObservation> frames, int onset, int available, int minRun)
    {
        Pitch runPitch = default;
        int runLength = 0;
        for (int j = onset; j <= available && j < frames.Count; j++)
        {
            if (frames[j].Pitch is { } p)
            {
                if (runLength > 0 && p.MidiNumber == runPitch.MidiNumber)
                {
                    runLength++;
                }
                else
                {
                    runPitch = p;
                    runLength = 1;
                }

                if (runLength >= minRun)
                {
                    return runPitch;
                }
            }
            else
            {
                runLength = 0;
            }
        }

        return null;
    }

    /// <summary>True if any frame in <c>[onset, available]</c> carries a voiced pitch.</summary>
    private static bool AnyVoiced(IReadOnlyList<FrameObservation> frames, int onset, int available)
    {
        for (int j = onset; j <= available && j < frames.Count; j++)
        {
            if (frames[j].Pitch is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Converts a YIN estimate to a domain <see cref="Pitch"/>, guarding the case
    /// <see cref="Pitch.FromFrequency"/> cannot handle safely: a voiced estimate whose
    /// nearest MIDI note falls outside the 88-key range (21..108) becomes <c>null</c>
    /// (unvoiced) rather than letting the constructor throw. A single wild frame — e.g.
    /// during an attack transient, where YIN's lag search can briefly lock onto a very
    /// short or very long period — must never crash the whole pipeline (§4 non-negotiable
    /// 3's determinism promise is worthless if the pipeline cannot even finish).
    /// </summary>
    public static Pitch? GuardedPitchFromEstimate(PitchEstimate estimate)
    {
        if (!estimate.IsVoiced)
        {
            return null;
        }

        double hz = estimate.FrequencyHz;
        if (hz <= 0.0 || double.IsNaN(hz) || double.IsInfinity(hz))
        {
            return null;
        }

        double exact = 69.0 + (12.0 * Math.Log2(hz / 440.0));
        int midi = (int)Math.Round(exact, MidpointRounding.AwayFromZero);
        return midi is >= Pitch.MinMidi and <= Pitch.MaxMidi ? new Pitch(midi) : null;
    }

    /// <summary>Per-frame RMS level, used for the segmenter's decay-below-floor termination (R5.2).</summary>
    private static double Rms(float[] samples)
    {
        double sum = 0.0;
        foreach (float s in samples)
        {
            sum += (double)s * s;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    /// <summary>
    /// Recomputes each note's duration from its energy envelope, using an EARLY reference level
    /// (frame <paramref name="settleFrames"/> past onset — just clear of the attack transient) and
    /// bounding the search by the NEXT note's onset. It is the authority on duration, overriding
    /// the segmenter's: the note ends at the first frame whose level stays below
    /// <paramref name="releaseRatio"/> of that reference for <paramref name="persistFrames"/>
    /// consecutive frames, or at the next onset if it never does.
    ///
    /// Why override the segmenter (rather than only shorten its result, as an earlier version did):
    /// two low notes close in pitch overlap acoustically — a low piano note rings on through the
    /// rest into the next note — and their partials BEAT, wobbling the YIN pitch track. Step 5's
    /// <see cref="NoteSegmenter"/> ends a note on any pitch change, so a momentary wobble truncates
    /// the note in the DOMAIN, where this Application-layer pass can no longer see the note's true
    /// extent if it only shortened. Recomputing duration straight from energy (which the audible-
    /// duration corpus cap keeps above threshold until note-off — DECISIONS.md) sidesteps the
    /// pitch-track wobble entirely. The early reference matters too: an earlier version sampled at
    /// 20 frames (232 ms), which for a short note fell almost at the note-off, driving the threshold
    /// absurdly low and causing gross overshoot.
    ///
    /// Runs entirely in Application (Step 9's own composition), after Step 5 — Domain's
    /// <see cref="NoteSegmenter"/> is unchanged and its own decay floor stays disabled
    /// (<see cref="TranscriptionSettings.DecayFloorRatio"/> defaults to 0). It sets only duration,
    /// never pitch, onset, or note count, so it cannot affect those three (proven across the full
    /// keyboard by the full-range closed-loop test).
    /// </summary>
    private static IReadOnlyList<NoteEvent> RefineOffsets(
        IReadOnlyList<NoteEvent> events,
        IReadOnlyList<FrameObservation> observations,
        int settleFrames,
        double releaseRatio,
        int persistFrames,
        SampleRate rate)
    {
        if (events.Count == 0 || observations.Count < 2)
        {
            return events;
        }

        long baseSample = observations[0].Start.Samples;
        long hop = observations[1].Start.Samples - baseSample;
        int frameCount = observations.Count;

        int FrameIndexOf(long samplePos) =>
            (int)Math.Clamp((samplePos - baseSample) / hop, 0, frameCount);

        var refined = new List<NoteEvent>(events.Count);
        for (int idx = 0; idx < events.Count; idx++)
        {
            NoteEvent e = events[idx];
            int onsetFrame = FrameIndexOf(e.Onset.Samples);

            // The duration search is bounded by the NEXT note's onset (monophonic corpus), or the
            // end of the frame stream for the last note — NOT the segmenter's (possibly truncated)
            // duration.
            int searchEnd = idx + 1 < events.Count ? FrameIndexOf(events[idx + 1].Onset.Samples) : frameCount;
            int endFrame = searchEnd; // no crossing found -> runs to the bound

            int settleIdx = onsetFrame + settleFrames;
            if (settleIdx > onsetFrame && settleIdx < searchEnd && settleIdx < frameCount
                && observations[settleIdx].Energy > 0.0)
            {
                double threshold = observations[settleIdx].Energy * releaseRatio;
                for (int j = settleIdx; j < searchEnd; j++)
                {
                    if (observations[j].Energy < threshold && StaysBelow(observations, j, searchEnd, persistFrames, threshold))
                    {
                        endFrame = j;
                        break;
                    }
                }
            }

            long refinedEndSample = baseSample + ((long)endFrame * hop);
            long refinedDuration = Math.Max(1, refinedEndSample - e.Onset.Samples);
            refined.Add(new NoteEvent(e.Pitch, e.Onset, new SampleDuration(refinedDuration, rate), e.Velocity));
        }

        return refined;
    }

    /// <summary>
    /// Stamps each note with a velocity estimated from its attack energy — the peak per-frame RMS in the
    /// first <paramref name="attackFrames"/> frames from its onset — via <see cref="VelocityEstimator"/>.
    /// A pure 1:1 relabel: pitch, onset, and duration are copied unchanged and the note count is preserved,
    /// so it cannot touch the closed loop's count/pitch/onset/duration checks (velocity is not one of them).
    /// Runs in Application after offset refinement; the Domain segmenter's constant velocity is overridden
    /// here rather than in Domain, keeping the energy→velocity model a composition-root concern.
    /// </summary>
    private static IReadOnlyList<NoteEvent> RefineVelocities(
        IReadOnlyList<NoteEvent> events,
        IReadOnlyList<FrameObservation> observations,
        int attackFrames)
    {
        if (events.Count == 0 || observations.Count < 2)
        {
            return events;
        }

        long baseSample = observations[0].Start.Samples;
        long hop = observations[1].Start.Samples - baseSample;
        int frameCount = observations.Count;
        int window = Math.Max(1, attackFrames);

        int FrameIndexOf(long samplePos) =>
            (int)Math.Clamp((samplePos - baseSample) / hop, 0, frameCount - 1);

        var refined = new List<NoteEvent>(events.Count);
        for (int idx = 0; idx < events.Count; idx++)
        {
            NoteEvent e = events[idx];
            int onsetFrame = FrameIndexOf(e.Onset.Samples);
            // Bound the attack search by the NEXT note's onset (as RefineOffsets does), so a longer
            // attack window can never pick up the next note's onset energy — explicit, not coincidental.
            int nextOnset = idx + 1 < events.Count ? FrameIndexOf(events[idx + 1].Onset.Samples) : frameCount;
            int last = Math.Min(Math.Min(onsetFrame + window, frameCount), nextOnset);
            double attackPeak = 0.0;
            for (int j = onsetFrame; j < last; j++)
            {
                if (observations[j].Energy > attackPeak)
                {
                    attackPeak = observations[j].Energy;
                }
            }

            int velocity = VelocityEstimator.FromAttackEnergy(attackPeak);
            refined.Add(new NoteEvent(e.Pitch, e.Onset, e.Duration, velocity));
        }

        return refined;
    }

    /// <summary>True if the energy stays strictly below <paramref name="threshold"/> for the next
    /// <paramref name="persistFrames"/> frames (or through <paramref name="endFrame"/> / the end of
    /// the frame stream, whichever comes first).</summary>
    private static bool StaysBelow(
        IReadOnlyList<FrameObservation> observations, int from, int endFrame, int persistFrames, double threshold)
    {
        int last = Math.Min(Math.Min(from + persistFrames, endFrame), observations.Count);
        for (int k = from; k < last; k++)
        {
            if (observations[k].Energy >= threshold)
            {
                return false;
            }
        }

        return true;
    }
}
