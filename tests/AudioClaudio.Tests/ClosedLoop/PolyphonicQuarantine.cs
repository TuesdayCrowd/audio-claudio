using System.Collections.Generic;
using System.IO;
using System.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.Midi; // DryWetMidiWriter
using AudioClaudio.Tests.Signals; // WavWriter (Step 2 test utility)

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>
/// Persists a polyphonic closed-loop case that dragged the corpus F1 below the committed gate, so a
/// regression is reproducible and can be promoted to <c>fixtures/regressions/polyphonic/</c> — the
/// subdirectory <see cref="PolyphonicRegressionCorpusTests"/> replays (never the top-level
/// <c>fixtures/regressions/</c>, which the monophonic <see cref="RegressionCorpusTests"/> scans). The
/// <b>WAV is the faithful artifact</b> for offline inspection; the committed fixture is the MIDI (the
/// score), since the audio regenerates deterministically. The reference tempo is nominal (the note onsets
/// already carry their true sample positions), so it is written at a fixed 120 BPM purely for a readable
/// tick map. The polyphonic analogue of <see cref="Quarantine"/> (which takes a monophonic <see cref="ClosedLoopCase"/>).
/// </summary>
public static class PolyphonicQuarantine
{
    private const int NominalTempoBpm = 120;

    /// <summary>Writes &lt;id&gt;.mid (the reference score) and &lt;id&gt;.wav (the rendered audio);
    /// returns the directory.</summary>
    public static string Persist(string id, IReadOnlyList<NoteEvent> score, IReadOnlyList<float> pcm, SampleRate rate, string? directory = null)
    {
        string dir = directory ?? Path.Combine(Fixtures.QuarantineDir, "polyphonic");
        Directory.CreateDirectory(dir);

        using (var midi = File.Create(Path.Combine(dir, $"{id}.mid")))
        {
            new DryWetMidiWriter().Write(score, new Tempo(NominalTempoBpm), midi);
        }

        float[] samples = pcm as float[] ?? pcm.ToArray();
        WavWriter.WriteMonoFile(Path.Combine(dir, $"{id}.wav"), samples, rate);
        return dir;
    }
}
