using System.IO;
using AudioClaudio.Cli;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Cli.Kernel;

public class EvaluateHandlerTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Evaluate_reports_perfect_F1_for_an_identical_candidate()
    {
        var app = AppBuilder.Build(new System.Text.StringBuilder(), noColor: true);
        var rate = TwoBarMelody.Rate;
        var writer = new DryWetMidiWriter();
        string midiPath = Path.Combine(Path.GetTempPath(), $"claudio-eval-{Guid.NewGuid():N}.mid");
        using (var f = File.Create(midiPath))
            writer.Write(TwoBarMelody.Notes(rate), new Tempo(120), f);

        try
        {
            var stdout = new StringWriter();
            int code = app.Run(new[] { "evaluate", midiPath, midiPath }, stdout, new StringWriter());

            Assert.Equal(0, code);
            Assert.Contains("F1:              100.0%", stdout.ToString());
        }
        finally
        {
            if (File.Exists(midiPath)) File.Delete(midiPath);
        }
    }
}
