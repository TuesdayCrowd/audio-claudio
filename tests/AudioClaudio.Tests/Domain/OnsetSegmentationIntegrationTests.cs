using System;
using System.Collections.Generic;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Tests.Signals;
using Xunit;

namespace AudioClaudio.Tests.Domain;

/// <summary>
/// The one end-to-end test that runs Step 5 over the REAL upstream chain, with no
/// hand-built spectra: Step 2 <see cref="SignalGenerator"/> renders a small note
/// sequence, Step 2 <see cref="Framing"/> splits it, Step 3 <see cref="SpectralFrontEnd"/>
/// produces the magnitude spectra fed to <see cref="OnsetDetector"/>, and Step 4
/// <see cref="YinPitchDetector"/> produces the per-frame pitch track fed (as
/// <see cref="FrameObservation"/>s) to <see cref="NoteSegmenter"/>. This proves the
/// detector fires on genuine spectral flux and the segmenter labels a genuine YIN track
/// — the assertions the synthetic-input unit tests cannot make. Matches the MVP corpus
/// (Step 9): a grid rest between notes, so no hard no-gap legato case here.
/// </summary>
public sealed class OnsetSegmentationIntegrationTests
{
    private static readonly SampleRate Rate = new(44100);
    private const int FrameSize = 2048;
    private const int Hop = 512;

    // MIDI 45..67 — solidly inside YIN's proven range (its property tests cover 33..96).
    private static readonly int[] Midis = { 45, 52, 60, 67 };

    [Fact]
    [Trait("Category", "Slow")]
    public void RealSignalChain_YieldsOneEventPerNote_WithCorrectPitches()
    {
        int noteSamples = (int)(0.4 * Rate.Hz);   // ~0.4 s per note
        int gapSamples = 8 * Hop;                  // a few frames of true silence between notes

        // Build the buffer: lead silence, then each note (harmonic stack) followed by a
        // silent gap. Record each note's true start sample for a loose onset sanity check.
        var buffer = new List<float>();
        var trueStarts = new List<long>();

        buffer.AddRange(new float[gapSamples]);   // lead-in silence: first onset is mid-buffer
        foreach (int midi in Midis)
        {
            trueStarts.Add(buffer.Count);
            double hz = new Pitch(midi).Frequency();
            buffer.AddRange(SignalGenerator.HarmonicStack(hz, noteSamples, Rate, partials: 6, decay: 1.0));
            buffer.AddRange(new float[gapSamples]);
        }

        float[] samples = buffer.ToArray();

        // Step 2: frame it. Step 3: real windowed FFT. Step 4: real YIN, per frame.
        IReadOnlyList<Frame> frames = Framing.Split(samples, Rate, new FrameParameters(FrameSize, Hop));
        var frontEnd = new SpectralFrontEnd(FrameSize, new Radix2Fft());

        var spectra = new List<IReadOnlyList<double>>(frames.Count);
        var observations = new List<FrameObservation>(frames.Count);
        foreach (Frame frame in frames)
        {
            spectra.Add(frontEnd.Analyze(frame).Magnitudes);

            PitchEstimate estimate = YinPitchDetector.Detect(frame);
            observations.Add(new FrameObservation(frame.Start, ToPitch(estimate), Rms(frame.Samples)));
        }

        // Step 5: onsets from real flux, then segmentation over the real pitch track.
        IReadOnlyList<int> onsetFrames = new OnsetDetector().Detect(spectra);

        var segmenter = new NoteSegmenter(new NoteSegmenterOptions
        {
            MinNoteDuration = new SampleDuration((long)(0.05 * Rate.Hz), Rate),   // ~50 ms
            StabilityFrames = 2,
            Velocity = 64,
        });
        IReadOnlyList<NoteEvent> events = segmenter.Segment(observations, onsetFrames);

        // The real onset detector must fire on each note's genuine spectral-flux attack.
        // (It also fires an extra peak at each note's *offset*: the harmonic stack is a
        // constant-amplitude tone with a hard cutoff, and truncating a tone mid-window
        // smears its spectrum into new bins, which registers as positive flux. Those
        // offset-smear onsets land 0.71..0.33 of the max — overlapping the real attacks'
        // 1.0..0.71 — so no threshold separates them; a real piano's gradual decay would
        // not smear this way. The segmenter absorbs them (they carry no min-duration-long
        // stable pitch), so the *event* stream is exactly one per note. Hence we assert
        // per-note onset coverage here, and the exact count on the events below.)
        foreach (long start in trueStarts)
        {
            int trueFrame = (int)(start / Hop);
            bool covered = false;
            foreach (int o in onsetFrames)
            {
                if (Math.Abs(o - trueFrame) <= 4)
                {
                    covered = true;
                    break;
                }
            }
            Assert.True(covered,
                $"no onset detected near note-start frame {trueFrame}; onsets=[{string.Join(",", onsetFrames)}]");
        }

        // The mandated assertions: one event per note, each pitch exactly right. NOT loosened.
        Assert.Equal(Midis.Length, events.Count);
        for (int i = 0; i < Midis.Length; i++)
        {
            Assert.Equal(Midis[i], events[i].Pitch.MidiNumber);

            // Loose onset sanity: windowing can shift the detected attack by a frame or two,
            // so allow a small tolerance — this is a placement check, not the pitch/count claim.
            long drift = Math.Abs(events[i].Onset.Samples - trueStarts[i]);
            Assert.True(drift <= 4L * Hop,
                $"note {i} (MIDI {Midis[i]}) onset drifted {drift} samples (> 4 hops) from its true start");
        }
    }

    /// <summary>
    /// Convert a YIN estimate to a domain <see cref="Pitch"/> (or null for unvoiced /
    /// out-of-range). YIN's search band tops out above the 88-key range, so a transient
    /// frame can land above C8; the real composition root must guard this rather than let
    /// <see cref="Pitch"/>'s constructor throw. Uses the same nearest-note rounding as
    /// <see cref="Pitch.FromFrequency"/>.
    /// </summary>
    private static Pitch? ToPitch(PitchEstimate estimate)
    {
        if (!estimate.IsVoiced)
        {
            return null;
        }

        double exact = 69.0 + 12.0 * Math.Log2(estimate.FrequencyHz / 440.0);
        int midi = (int)Math.Round(exact, MidpointRounding.AwayFromZero);
        return midi is >= Pitch.MinMidi and <= Pitch.MaxMidi ? new Pitch(midi) : null;
    }

    /// <summary>Per-frame RMS level, used for the FrameObservation energy.</summary>
    private static double Rms(float[] samples)
    {
        double sum = 0.0;
        foreach (float s in samples)
        {
            sum += (double)s * s;
        }
        return Math.Sqrt(sum / samples.Length);
    }
}
