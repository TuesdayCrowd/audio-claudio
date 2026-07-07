using System;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Infrastructure.Synthesis;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Synthesis;

public class MeltySynthSynthesizerTests
{
    private static readonly SampleRate Rate = new SampleRate(44100);

    private static MeltySynthSynthesizer NewSynth() => new(RepoPaths.SoundFontPath);

    [Fact]
    [Trait("Category", "Fast")]
    public void Render_produces_expected_length_including_release_tail()
    {
        // One A4 (MIDI 69), 0.5 s long, starting at sample 0.
        var notes = new[]
        {
            new NoteEvent(
                new Pitch(69),
                new SamplePosition(0, Rate),
                new SampleDuration(22050, Rate),
                100)
        };

        float[] pcm = NewSynth().Render(notes, Rate);

        // note ends at 22050; default release tail is 1500 ms = 66150 samples @ 44.1 kHz.
        Assert.Equal(22050 + 66150, pcm.Length);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Render_is_silent_before_onset_and_energetic_during_note()
    {
        const long onset = 22050; // 0.5 s in
        var notes = new[]
        {
            new NoteEvent(
                new Pitch(69),
                new SamplePosition(onset, Rate),
                new SampleDuration(22050, Rate),
                100)
        };

        float[] pcm = NewSynth().Render(notes, Rate);

        double preRms = Rms(pcm, 0, (int)onset);
        double noteRms = Rms(pcm, (int)onset, (int)onset + 11025); // first 0.25 s of the note

        Assert.True(preRms < 1e-6, $"expected silence before onset, got RMS {preRms}");
        Assert.True(noteRms > 1e-3, $"expected an audible note, got RMS {noteRms}");
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Render_rejects_notes_whose_sample_rate_differs_from_the_render_rate()
    {
        var wrongRate = new SampleRate(48000);
        var notes = new[]
        {
            new NoteEvent(new Pitch(69), new SamplePosition(0, wrongRate), new SampleDuration(1000, wrongRate))
        };

        Assert.Throws<ArgumentException>(() => NewSynth().Render(notes, Rate));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Render_of_an_empty_note_list_is_silence_of_just_the_release_tail()
    {
        float[] pcm = NewSynth().Render(Array.Empty<NoteEvent>(), Rate);

        Assert.Equal(66150, pcm.Length); // default 1500 ms release tail, no notes
        Assert.True(Rms(pcm, 0, pcm.Length) < 1e-6, "expected silence when no notes are rendered");
    }

    // Spectral sanity check (cross-platform-robust, unlike a byte/hash comparison): render a
    // single sustained A4, analyze a steady-state window well clear of the attack transient
    // and of the note's own release, and assert the FFT's peak bin lands within a few cents
    // of A4's true 440 Hz. At N=32768 the bin width near 440 Hz is ~5.3 cents, so peak-bin
    // quantization alone bounds this to roughly +/-3 cents; 15 cents leaves headroom for
    // windowing/spectral leakage. This is a coarse cross-check, not a replacement for the
    // Step 4 YIN detector's +/-10 cent contract (which targets isolated single-cycle accuracy,
    // not a synthesized/enveloped instrument tone).
    [Fact]
    [Trait("Category", "Fast")]
    public void Render_of_a_sustained_note_has_dominant_frequency_matching_its_pitch()
    {
        var notes = new[]
        {
            new NoteEvent(
                new Pitch(69), // A4 = 440 Hz
                new SamplePosition(0, Rate),
                new SampleDuration(88200, Rate), // 2.0 s — long enough to hold a steady analysis window
                100)
        };

        float[] pcm = NewSynth().Render(notes, Rate);

        const int frameSize = 32768;
        const int analysisStart = 20000; // well past the attack transient; well before note-off at 88200
        var window = new float[frameSize];
        Array.Copy(pcm, analysisStart, window, 0, frameSize);
        var frame = new Frame(window, new SamplePosition(0, Rate));

        var frontEnd = new SpectralFrontEnd(frameSize, new Radix2Fft());
        MagnitudeSpectrum spectrum = frontEnd.Analyze(frame);
        double observedHz = spectrum.FrequencyOf(spectrum.PeakBin());

        double cents = Math.Abs(PitchMath.CentsBetween(observedHz, 440.0));
        Assert.True(cents < 15.0, $"expected ~440 Hz (A4), observed {observedHz:F2} Hz ({cents:F1} cents off)");
    }

    private static double Rms(float[] x, int start, int end)
    {
        double sum = 0;
        int n = 0;
        for (int i = start; i < end && i < x.Length; i++)
        {
            sum += (double)x[i] * x[i];
            n++;
        }
        return n == 0 ? 0 : Math.Sqrt(sum / n);
    }
}
