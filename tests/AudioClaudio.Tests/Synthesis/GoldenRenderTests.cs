using System;
using System.IO;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Infrastructure.Midi;
using AudioClaudio.Infrastructure.Synthesis;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Synthesis;

/// <summary>
/// The Step 8 synthesis golden: a fixed two-bar melody rendered once and committed as a
/// reference WAV (<c>fixtures/golden/two-bar.wav</c>), compared against a fresh render on
/// every test run <b>within a tolerance</b> rather than by byte-exact hash (see
/// <see cref="WavGoldenComparer"/> for why, and R8.2's cross-arch caveat in DECISIONS.md).
/// Also carries the structural checks from Section 5's fixture policy (golden failures are
/// reviewed, never blindly regenerated) and a cross-platform-robust spectral sanity check.
/// </summary>
public class GoldenRenderTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Two_bar_melody_renders_within_tolerance_of_the_committed_golden()
    {
        var rate = TwoBarMelody.Rate;
        var synth = new MeltySynthSynthesizer(RepoPaths.SoundFontPath);
        var notes = TwoBarMelody.Notes(rate);

        float[] pcm = synth.Render(notes, rate);
        byte[] actualWav = WavFileWriter.ToBytes(pcm, rate);

        string goldenWavPath = RepoPaths.Fixture("golden", "two-bar.wav");
        string goldenMidPath = RepoPaths.Fixture("golden", "two-bar.mid");

        // Deliberate, reviewed bless: run once with AUDIO_CLAUDIO_BLESS=1 to (re)mint both
        // committed fixtures from this exact melody, listen to the rendered WAV to confirm
        // it is a real piano scale, then commit. Never regenerated silently (Section 5).
        if (Environment.GetEnvironmentVariable("AUDIO_CLAUDIO_BLESS") == "1")
        {
            Directory.CreateDirectory(RepoPaths.GoldenDirectory);
            File.WriteAllBytes(goldenWavPath, actualWav);

            using var midiStream = File.Create(goldenMidPath);
            new DryWetMidiWriter().Write(notes, TwoBarMelody.Tempo, midiStream);
        }

        byte[] expectedWav = File.ReadAllBytes(goldenWavPath);
        WavGoldenComparer.AssertWithinTolerance(expectedWav, actualWav);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Two_bar_melody_render_has_the_expected_total_length()
    {
        var rate = TwoBarMelody.Rate;
        var synth = new MeltySynthSynthesizer(RepoPaths.SoundFontPath);
        var notes = TwoBarMelody.Notes(rate);

        float[] pcm = synth.Render(notes, rate);

        // last onset (7 * 22050) + duration (19845) + default 1500 ms release tail (66150).
        const long lastNoteEnd = 7 * 22050 + 19845;
        const long expectedLength = lastNoteEnd + 66150;
        Assert.Equal(expectedLength, pcm.Length);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Two_bar_melody_render_is_silent_before_the_first_onset_and_energetic_during_the_first_note()
    {
        var rate = TwoBarMelody.Rate;
        var synth = new MeltySynthSynthesizer(RepoPaths.SoundFontPath);
        float[] pcm = synth.Render(TwoBarMelody.Notes(rate), rate);

        // The melody's first onset is sample 0, so "before the first onset" is vacuous for
        // this fixture; instead assert silence in the gap between the first note's end
        // (19845) and the second note's onset (22050), and energy inside the first note.
        double duringFirstNote = Rms(pcm, 2000, 15000); // clear of the attack transient
        double gapAfterFirstNote = Rms(pcm, 21500, 22049); // just before the 2nd onset; release is fast

        Assert.True(duringFirstNote > 1e-3, $"expected an audible first note, got RMS {duringFirstNote}");
        Assert.True(gapAfterFirstNote < 5e-2, $"expected the gap to have decayed well below the note, got RMS {gapAfterFirstNote}");
    }

    // Cross-platform-robust spectral sanity check (independent of the tolerance-based
    // comparison above): the melody's first note is C4 (MIDI 60, ~261.63 Hz). Analyze a
    // steady-state window and assert the FFT's peak bin lands within a few cents of it.
    [Fact]
    [Trait("Category", "Fast")]
    public void Two_bar_melody_first_note_has_dominant_frequency_matching_c4()
    {
        var rate = TwoBarMelody.Rate;
        var synth = new MeltySynthSynthesizer(RepoPaths.SoundFontPath);
        float[] pcm = synth.Render(TwoBarMelody.Notes(rate), rate);

        const int frameSize = 16384; // fits inside the first note's 19845-sample span; ~2.7 Hz/bin
        const int analysisStart = 2000; // past the attack transient, before the note ends at 19845
        var window = new float[frameSize];
        Array.Copy(pcm, analysisStart, window, 0, frameSize);
        var frame = new Frame(window, new SamplePosition(0, rate));

        var frontEnd = new SpectralFrontEnd(frameSize, new Radix2Fft());
        MagnitudeSpectrum spectrum = frontEnd.Analyze(frame);
        double observedHz = spectrum.FrequencyOf(spectrum.PeakBin());

        double expectedHz = new Pitch(60).Frequency(); // C4
        double cents = Math.Abs(PitchMath.CentsBetween(observedHz, expectedHz));
        // ~2.7 Hz bin width near C4 (261.6 Hz) is ~17.7 cents wide; 25 cents leaves headroom
        // over the ~half-bin (~8.9 cent) worst case for windowing/spectral leakage.
        Assert.True(cents < 25.0, $"expected ~{expectedHz:F2} Hz (C4), observed {observedHz:F2} Hz ({cents:F1} cents off)");
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
