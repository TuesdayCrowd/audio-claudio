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
            // R5.2's decay-below-floor is the composition root's call (Step 5's R5.2 coverage
            // row); disabled here (TranscriptionSettings.DecayFloorRatio defaults to 0) in favor
            // of the RefineOffsets post-process below — see its remarks for why.
            DecayFloorRatio = _settings.DecayFloorRatio,
        });

        IReadOnlyList<NoteEvent> events = segmenter.Segment(observations, onsetFrames);
        events = RefineOffsets(
            events, observations, _settings.OffsetSettleFrames, _settings.OffsetReleaseRatio,
            _settings.OffsetPersistFrames, rate);

        // Step 6: build the grid and quantize (static, no `new Quantizer()`).
        var grid = new QuantizationGrid(
            rate, new Tempo(_settings.TempoBpm), _settings.TimeSignature, _settings.Subdivision);
        Score score = Quantizer.Quantize(events, grid);

        return new TranscriptionResult(score, events);
    }

    /// <summary>
    /// Incremental note feed for the live `listen` view (Step 10). For the MVP this simply
    /// surfaces the raw events of a full pass; Step 10 iterates it as notes are produced.
    /// </summary>
    public IEnumerable<NoteEvent> StreamNotes(IAudioSource source) => Transcribe(source).RawEvents;

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
