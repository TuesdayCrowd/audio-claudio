using System;
using System.IO;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi; // MidiFileReader, MidiReadResult
using Xunit;

namespace AudioClaudio.Tests.ClosedLoop;

public sealed class RegressionCorpusTests
{
    [Trait("Category", "Fast")]
    [Fact]
    public void All_regression_fixtures_transcribe_within_tolerance()
    {
        string dir = Fixtures.RegressionsDir;
        if (!Directory.Exists(dir))
        {
            return;
        }

        // MIDI stores no sample rate; the corpus was rendered at the generator's fixed rate (denominate here).
        var rate = new SampleRate(ClosedLoopGen.SampleRateHz);

        foreach (string mid in Directory.GetFiles(dir, "*.mid"))
        {
            MidiReadResult read = MidiFileReader.ReadFile(mid, rate); // { Events, Tempo }
            var c = new ClosedLoopCase(
                rate,
                (int)Math.Round(read.Tempo.BeatsPerMinute),
                TimeSignature.FourFour,
                Subdivision.Sixteenth,
                read.Events);

            ClosedLoop.RunCase(c); // throws (and re-quarantines) on any regression
        }
    }
}
