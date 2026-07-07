using System;
using System.IO;
using AudioClaudio.Cli.Commands;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Infrastructure.Midi;
using AudioClaudio.Infrastructure.Synthesis;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Cli;

public class RenderCommandTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Render_command_writes_a_wav_matching_the_golden_within_tolerance()
    {
        var rate = TwoBarMelody.Rate;
        var synth = new MeltySynthSynthesizer(RepoPaths.SoundFontPath);
        string outPath = Path.Combine(Path.GetTempPath(), $"claudio-render-{Guid.NewGuid():N}.wav");

        try
        {
            // Load via the Step 7 reader, exactly as the CLI's render command does — this
            // exercises the committed fixtures/golden/two-bar.mid, not the in-code melody
            // directly, proving the CLI wiring (R8.3) end to end.
            var notes = MidiFileReader.ReadFile(RepoPaths.Fixture("golden", "two-bar.mid"), rate).Events;

            RenderCommand.RenderToWav(notes, synth, rate, outPath);

            Assert.True(File.Exists(outPath));
            byte[] actualWav = File.ReadAllBytes(outPath);
            byte[] expectedWav = File.ReadAllBytes(RepoPaths.Fixture("golden", "two-bar.wav"));
            WavGoldenComparer.AssertWithinTolerance(expectedWav, actualWav);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void RenderToWav_writes_a_file_that_round_trips_through_WavFileWriter()
    {
        var rate = new SampleRate(44100);
        var synth = new MeltySynthSynthesizer(RepoPaths.SoundFontPath);
        var notes = TwoBarMelody.Notes(rate);
        string outPath = Path.Combine(Path.GetTempPath(), $"claudio-render-{Guid.NewGuid():N}.wav");

        try
        {
            RenderCommand.RenderToWav(notes, synth, rate, outPath);

            byte[] written = File.ReadAllBytes(outPath);
            byte[] expectedFromDirectRender = WavFileWriter.ToBytes(synth.Render(notes, rate), rate);
            Assert.Equal(expectedFromDirectRender, written);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }
}
