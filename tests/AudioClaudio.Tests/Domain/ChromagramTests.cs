using System.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Tests.Signals;
using Xunit;

namespace AudioClaudio.Tests.Domain;

/// <summary>
/// The chromagram: per-frame energy folded into the 12 pitch classes — a <b>timbre-robust</b> "which
/// notes are sounding" feature (it discards octave and instrument colour, keeping pitch class). This is
/// what lets us compare a real-piano recording against a SoundFont re-synthesis of the transcription:
/// two different pianos playing the same notes share a chromagram even though their waveforms don't.
/// </summary>
public class ChromagramTests
{
    private static readonly SampleRate Rate = new(44100);

    private static int Argmax(double[] v)
    {
        int best = 0;
        for (int i = 1; i < v.Length; i++)
        {
            if (v[i] > v[best])
            {
                best = i;
            }
        }

        return best;
    }

    private static double[] Summed(System.Collections.Generic.IReadOnlyList<double[]> chroma)
    {
        var s = new double[12];
        foreach (double[] c in chroma)
        {
            for (int i = 0; i < 12; i++)
            {
                s[i] += c[i];
            }
        }

        return s;
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Chroma_of_a_pure_A4_peaks_at_pitch_class_A()
    {
        float[] sine = SignalGenerator.Sine(440.0, 20000, Rate);
        Assert.Equal(9, Argmax(Summed(Chromagram.FromSamples(sine, Rate)))); // A = pitch class 9
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Chroma_of_a_C_major_triad_peaks_at_C_E_and_G()
    {
        float[] c = SignalGenerator.Sine(261.63, 20000, Rate, 0.5);
        float[] e = SignalGenerator.Sine(329.63, 20000, Rate, 0.5);
        float[] g = SignalGenerator.Sine(392.00, 20000, Rate, 0.5);
        var mix = new float[20000];
        for (int i = 0; i < mix.Length; i++)
        {
            mix[i] = c[i] + e[i] + g[i];
        }

        double[] summed = Summed(Chromagram.FromSamples(mix, Rate));
        int[] top3 = Enumerable.Range(0, 12).OrderByDescending(i => summed[i]).Take(3).OrderBy(i => i).ToArray();
        Assert.Equal(new[] { 0, 4, 7 }, top3); // C, E, G
    }
}
