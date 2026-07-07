using System.Collections.Generic;
using System.IO;
using System.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi; // DryWetMidiWriter
using AudioClaudio.Tests.Signals; // WavWriter (Step 2 test utility)

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>Persists a failing closed-loop case so it can be reproduced and promoted to fixtures/regressions/.</summary>
public static class Quarantine
{
    /// <summary>Writes &lt;id&gt;.mid (the generated performance) and &lt;id&gt;.wav (the rendered audio); returns the directory.</summary>
    public static string Persist(ClosedLoopCase c, IReadOnlyList<float> pcm, string? directory = null)
    {
        string dir = directory ?? Fixtures.QuarantineDir;
        Directory.CreateDirectory(dir);

        string id = c.Id();

        // Step 7's writer is Stream-based and needs the tempo for the sample->tick map; open a FileStream.
        using (var midi = File.Create(Path.Combine(dir, $"{id}.mid")))
        {
            new DryWetMidiWriter().Write(c.Events, new Tempo(c.TempoBpm), midi);
        }

        float[] samples = pcm as float[] ?? pcm.ToArray();
        WavWriter.WriteMonoFile(Path.Combine(dir, $"{id}.wav"), samples, c.Rate);
        return dir;
    }
}
