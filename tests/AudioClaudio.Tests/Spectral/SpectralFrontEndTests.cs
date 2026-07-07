using System;
using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;
using Xunit;

namespace AudioClaudio.Tests.Spectral;

[Trait("Category", "Fast")]
public class SpectralFrontEndTests
{
    // Canonical Step 2 Frame contract: Frame(float[] samples, SamplePosition start) — a 2-arg
    // sealed class. Rate is derived (Frame.Rate => Start.Rate), never a ctor parameter. Members: Samples, Start, Rate.
    private static Frame SineFrame(double frequencyHz, int sampleRateHz, int frameSize)
    {
        var rate = new SampleRate(sampleRateHz);
        var samples = new float[frameSize];
        for (int n = 0; n < frameSize; n++)
            samples[n] = (float)Math.Sin(2.0 * Math.PI * frequencyHz * n / sampleRateHz);
        return new Frame(samples, new SamplePosition(0, rate));
    }

    [Fact]
    public void Analyze_applies_the_hann_window_to_the_frame()
    {
        const int n = 64;
        var rate = new SampleRate(8000);
        var ones = new float[n];
        for (int i = 0; i < n; i++) ones[i] = 1.0f;
        var frame = new Frame(ones, new SamplePosition(0, rate));

        var frontEnd = new SpectralFrontEnd(n, TestFft.Create());
        MagnitudeSpectrum spectrum = frontEnd.Analyze(frame);

        // A windowed all-ones (DC) frame equals the Hann coefficients, so its DC bin
        // magnitude equals their sum (~31.5). Without windowing it would be n (=64).
        double expectedDc = HannWindow.Coefficients(n).Sum();
        Assert.True(Math.Abs(spectrum[0] - expectedDc) / expectedDc < 1e-4,
            $"DC bin {spectrum[0]} should equal the summed Hann coefficients {expectedDc}; the window was not applied.");
    }

    [Theory]
    [InlineData(500.0, 64)]    // 500 Hz sits exactly on bin 64: 64·8000/1024 = 500
    [InlineData(1000.0, 128)]  // 1000 Hz on bin 128: 128·8000/1024 = 1000
    [InlineData(517.0, 66)]    // off-bin: nearest bin to 517 Hz is 66 (515.6 Hz)
    public void Peak_bin_of_windowed_sine_is_the_bin_nearest_the_frequency(double freq, int expectedBin)
    {
        const int n = 1024, sr = 8000;
        var frontEnd = new SpectralFrontEnd(n, TestFft.Create());
        MagnitudeSpectrum spectrum = frontEnd.Analyze(SineFrame(freq, sr, n));
        Assert.Equal(expectedBin, spectrum.PeakBin());
    }

    [Fact]
    public void Analyze_is_deterministic_bit_for_bit()
    {
        const int n = 1024;
        var frontEnd = new SpectralFrontEnd(n, TestFft.Create());
        Frame frame = SineFrame(440.0, 44100, n);

        MagnitudeSpectrum a = frontEnd.Analyze(frame);
        MagnitudeSpectrum b = frontEnd.Analyze(frame);
        Assert.Equal(a.Magnitudes, b.Magnitudes); // exact, element-wise
    }

    [Fact]
    public void Analyze_maps_a_stream_of_frames_to_a_stream_of_spectra()
    {
        const int n = 256, sr = 8000;
        var frontEnd = new SpectralFrontEnd(n, TestFft.Create());
        var frames = new List<Frame> { SineFrame(500.0, sr, n), SineFrame(1000.0, sr, n) };

        List<MagnitudeSpectrum> spectra = frontEnd.Analyze(frames).ToList();

        Assert.Equal(2, spectra.Count);
        Assert.Equal(16, spectra[0].PeakBin());  // 500 Hz → bin 16 (16·8000/256)
        Assert.Equal(32, spectra[1].PeakBin());  // 1000 Hz → bin 32
    }

    [Fact]
    public void Constructor_rejects_non_power_of_two_frame_size()
    {
        Assert.Throws<ArgumentException>(() => new SpectralFrontEnd(1000, TestFft.Create()));
    }

    [Fact]
    public void Analyze_rejects_a_frame_whose_length_differs_from_the_configured_size()
    {
        var frontEnd = new SpectralFrontEnd(1024, TestFft.Create());
        var rate = new SampleRate(8000);
        var shortFrame = new Frame(new float[512], new SamplePosition(0, rate));
        Assert.Throws<ArgumentException>(() => frontEnd.Analyze(shortFrame));
    }
}
