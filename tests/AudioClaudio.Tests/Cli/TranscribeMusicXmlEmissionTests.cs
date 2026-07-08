using AudioClaudio.Cli.Commands; // TranscribeCommand (Step 9)
using AudioClaudio.Domain;
using AudioClaudio.Tests.MusicXml; // Xml.Parse
using AudioClaudio.Tests.Signals; // SignalGenerator, WavWriter
using Xunit;

namespace AudioClaudio.Tests.Cli;

public class TranscribeMusicXmlEmissionTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void TranscribeEmitsScoreMusicXmlAlongsideMidiTrio()
    {
        var rate = new SampleRate(44100);
        // One second of A4 through the signal generator, written as a mono WAV input.
        var pcm = SignalGenerator.Sine(new Pitch(69).Frequency(), rate.Hz, rate);
        var outDir = Directory.CreateTempSubdirectory().FullName;
        var wav = Path.Combine(outDir, "in.wav");
        WavWriter.WriteMonoFile(wav, pcm, rate);

        // Drive Step 9's factored command directly (top-level Program has no callable Main).
        TranscribeCommand.Run(wav, tempoBpm: 120, outDir: outDir);

        Assert.True(File.Exists(Path.Combine(outDir, "raw.mid")), "raw.mid should still be written");
        Assert.True(File.Exists(Path.Combine(outDir, "score.mid")), "score.mid should still be written");

        var musicXmlPath = Path.Combine(outDir, "score.musicxml");
        Assert.True(File.Exists(musicXmlPath), "score.musicxml completes the §7 trio");
        var doc = Xml.Parse(File.ReadAllText(musicXmlPath));
        Assert.Equal("4.0", (string)doc.Root!.Attribute("version")!);
    }
}
