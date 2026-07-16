using System.Collections.Generic;
using System.IO;
using AudioClaudio.Domain;
using AudioClaudio.Tests.Signals; // WavWriter (Step 2 test utility)

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>
/// Persists a separation closed-loop case that dragged the corpus median SI-SDR below the committed
/// gate, so a regression is reproducible: the mix (what the separator actually saw) plus every
/// instrument's ground-truth stem (what it should have recovered). The separator's own recovered stems
/// are not persisted -- they regenerate deterministically (same-machine) by re-running
/// <c>SpleeterSourceSeparator</c> on the quarantined mix. The separation analogue of
/// <see cref="PolyphonicQuarantine"/> (R9.3 discipline).
/// </summary>
public static class SeparationQuarantine
{
    /// <summary>Writes &lt;id&gt;-mix.wav and &lt;id&gt;-&lt;stem&gt;-truth.wav for every instrument;
    /// returns the directory.</summary>
    public static string Persist(string id, SeparationClosedLoop.CaseResult result, SampleRate rate, string? directory = null)
    {
        string dir = directory ?? Path.Combine(Fixtures.QuarantineDir, "separation");
        Directory.CreateDirectory(dir);

        WavWriter.WriteMonoFile(Path.Combine(dir, $"{id}-mix.wav"), result.Mix, rate);
        foreach (KeyValuePair<string, float[]> stem in result.GroundTruthStems)
        {
            WavWriter.WriteMonoFile(Path.Combine(dir, $"{id}-{stem.Key}-truth.wav"), stem.Value, rate);
        }

        return dir;
    }
}
