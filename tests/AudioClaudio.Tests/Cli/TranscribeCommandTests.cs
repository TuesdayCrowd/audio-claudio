using System.IO;
using AudioClaudio.Cli.Commands;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi; // MidiFileReader (Step 7 reader)
using AudioClaudio.Tests.Signals; // SignalGenerator, WavWriter
using Xunit;

namespace AudioClaudio.Tests.Cli;

public sealed class TranscribeCommandTests
{
    [Trait("Category", "Fast")]
    [Fact]
    public void Transcribe_emits_raw_and_score_midi_for_a_sustained_note()
    {
        var rate = new SampleRate(44100);
        float[] pcm = SignalGenerator.HarmonicStack(
            new Pitch(69).Frequency(), (int)(1.0 * rate.Hz), rate, partials: 6, decay: 1.0);

        string dir = Path.Combine(Path.GetTempPath(), $"acl_transcribe_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string wav = Path.Combine(dir, "in.wav");
        try
        {
            WavWriter.WriteMonoFile(wav, pcm, rate);

            TranscribeCommand.Run(wav, tempoBpm: 120, outDir: dir);

            string raw = Path.Combine(dir, "raw.mid");
            string score = Path.Combine(dir, "score.mid");
            Assert.True(File.Exists(raw), "raw.mid missing");
            Assert.True(File.Exists(score), "score.mid missing");

            var rawRead = MidiFileReader.ReadFile(raw, rate);
            Assert.Single(rawRead.Events);
            Assert.Equal(69, rawRead.Events[0].Pitch.MidiNumber);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // Note on "runs without a SoundFont" (Task 9's lazy-synth requirement): `TranscribeCommand.Run`
    // above takes no synthesizer/SoundFont path at all — its signature is `(string inputWav, double
    // tempoBpm, string outDir)` — so it is structurally incapable of constructing an `ISynthesizer`.
    // A runtime test that hides `fixtures/soundfont/` cannot actually prove this in this repo layout:
    // `SoundFontLocator.Resolve` walks up from `AppContext.BaseDirectory` (the test binary's own
    // location under the repo), not `Environment.CurrentDirectory`, so it always finds the real
    // committed fixture regardless of the test's working directory — a false pass either way. The
    // proof here is structural (verified by inspection of `TranscribeCommand.cs` and `Program.cs`'s
    // `Lazy<MeltySynthSynthesizer>`), not a fragile runtime check.
}
