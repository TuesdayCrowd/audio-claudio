using AudioClaudio.Domain;
using Xunit;

namespace AudioClaudio.Tests.Signals;

public class SignalGeneratorTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Sine_is_deterministic_bounded_and_correctly_shaped()
    {
        var rate = new SampleRate(48000);

        var a = SignalGenerator.Sine(440.0, 48000, rate, amplitude: 0.8);
        var b = SignalGenerator.Sine(440.0, 48000, rate, amplitude: 0.8);

        Assert.Equal(a, b);          // deterministic: identical output for identical input
        Assert.Equal(48000, a.Length);
        Assert.Equal(0f, a[0]);      // sin(0) == 0
        foreach (var s in a) Assert.InRange(s, -0.8f, 0.8f);

        int quarterPeriod = (int)(rate.Hz / 440.0 / 4); // ~ first peak
        Assert.True(a[quarterPeriod] > 0.7f);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void HarmonicStack_is_deterministic_bounded_and_differs_from_a_pure_sine()
    {
        var rate = new SampleRate(48000);

        var stack1 = SignalGenerator.HarmonicStack(220.0, 4800, rate, partials: 6, decay: 1.0, amplitude: 0.8);
        var stack2 = SignalGenerator.HarmonicStack(220.0, 4800, rate, partials: 6, decay: 1.0, amplitude: 0.8);
        var sine = SignalGenerator.Sine(220.0, 4800, rate, amplitude: 0.8);

        Assert.Equal(stack1, stack2);   // deterministic
        Assert.Equal(4800, stack1.Length);
        foreach (var s in stack1) Assert.InRange(s, -1f, 1f); // normalised to stay in range

        bool differsFromFundamental = false;
        for (int i = 0; i < stack1.Length; i++)
            if (MathF.Abs(stack1[i] - sine[i]) > 1e-4f) { differsFromFundamental = true; break; }
        Assert.True(differsFromFundamental); // partials genuinely change the waveform
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void HarmonicStack_can_be_rendered_to_frames()
    {
        var rate = new SampleRate(44100);
        var buffer = SignalGenerator.HarmonicStack(261.63, 2048, rate); // "rendered to frames" (R2.3)
        var frames = Framing.Split(buffer, rate, new FrameParameters(1024, 512));
        Assert.NotEmpty(frames);
        Assert.Equal(0, frames[0].Start.Samples);
    }
}
