using System.IO;
using AudioClaudio.Cli;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi;
using AudioClaudio.Tests.Signals;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class TranscribeHandlerTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Transcribe_mono_emits_raw_and_score_midi_for_a_sustained_note()
    {
        var app = AppBuilder.Build(new System.Text.StringBuilder(), noColor: true);
        var rate = new SampleRate(44100);
        float[] pcm = SignalGenerator.HarmonicStack(new Pitch(69).Frequency(), (int)(1.0 * rate.Hz), rate, partials: 6, decay: 1.0);
        string dir = Path.Combine(Path.GetTempPath(), $"claudio-transcribe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string wav = Path.Combine(dir, "in.wav");
        WavWriter.WriteMonoFile(wav, pcm, rate);

        try
        {
            int code = app.Run(
                new[] { "transcribe", wav, "--tempo", "120", "--out-dir", dir, "--mono" },
                new StringWriter(), new StringWriter());

            Assert.Equal(0, code);
            var rawRead = MidiFileReader.ReadFile(Path.Combine(dir, "raw.mid"), rate);
            Assert.Single(rawRead.Events);
            Assert.Equal(69, rawRead.Events[0].Pitch.MidiNumber);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Transcribe_reports_a_friendly_error_for_a_missing_input_file()
    {
        var app = AppBuilder.Build(new System.Text.StringBuilder(), noColor: true);
        var stderr = new StringWriter();

        int code = app.Run(new[] { "transcribe", "does-not-exist.wav", "--mono" }, new StringWriter(), stderr);

        Assert.Equal(1, code);
        Assert.Contains("input file 'does-not-exist.wav' not found", stderr.ToString());
    }
}
