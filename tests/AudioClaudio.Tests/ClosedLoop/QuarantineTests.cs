using System.IO;
using AudioClaudio.Infrastructure.Midi; // MidiFileReader (test = composition root)
using Xunit;

namespace AudioClaudio.Tests.ClosedLoop;

public sealed class QuarantineTests
{
    [Trait("Category", "Fast")]
    [Fact]
    public void Quarantine_persists_midi_and_wav_for_a_failing_case()
    {
        var c = ClosedLoopGen.Fixed();
        float[] pcm = new float[c.Rate.Hz / 2]; // half a second of silence stands in for a render
        var dir = Path.Combine(Path.GetTempPath(), $"acl_quarantine_test_{c.Id()}");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        try
        {
            string written = Quarantine.Persist(c, pcm, dir);

            var mid = Path.Combine(written, $"{c.Id()}.mid");
            var wav = Path.Combine(written, $"{c.Id()}.wav");
            Assert.True(File.Exists(mid), "quarantined MIDI missing");
            Assert.True(File.Exists(wav), "quarantined WAV missing");

            // MIDI carries no sample rate, so the reader is told which rate to denominate positions in.
            var read = MidiFileReader.ReadFile(mid, c.Rate);
            Assert.Equal(c.Events.Count, read.Events.Count);
            Assert.Equal(c.Events[0].Pitch.MidiNumber, read.Events[0].Pitch.MidiNumber);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
