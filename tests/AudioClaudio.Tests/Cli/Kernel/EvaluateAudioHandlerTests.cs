using System.IO;
using AudioClaudio.Cli;
using AudioClaudio.Domain;
using AudioClaudio.Tests.Signals;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class EvaluateAudioHandlerTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void EvaluateAudio_reports_perfect_similarity_for_a_wav_against_itself()
    {
        var app = AppBuilder.Build(new System.Text.StringBuilder(), noColor: true);
        var rate = new SampleRate(44100);
        float[] pcm = SignalGenerator.HarmonicStack(new Pitch(69).Frequency(), (int)(1.0 * rate.Hz), rate, partials: 6, decay: 1.0);
        string wav = Path.Combine(Path.GetTempPath(), $"claudio-eval-audio-{Guid.NewGuid():N}.wav");
        WavWriter.WriteMonoFile(wav, pcm, rate);

        var stdout = new StringWriter();
        int code;
        try
        {
            code = app.Run(new[] { "evaluate-audio", wav, wav }, stdout, new StringWriter());
        }
        finally
        {
            if (File.Exists(wav)) File.Delete(wav);
        }

        Assert.Equal(0, code);
        Assert.Contains("100.0%", stdout.ToString());
    }
}
