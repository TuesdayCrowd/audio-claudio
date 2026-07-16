using System;
using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Application.Ports; // SeparatedStem
using AudioClaudio.Cli.Composition; // SeparatorModelLocator
using AudioClaudio.Domain;
using AudioClaudio.Domain.Separation; // SiSdr
using AudioClaudio.Domain.Spectral; // Radix2Fft
using AudioClaudio.Infrastructure.Audio; // PcmAudioSource
using AudioClaudio.Infrastructure.Separation; // SpleeterSourceSeparator
using AudioClaudio.Infrastructure.Synthesis; // MeltySynthSynthesizer

namespace AudioClaudio.Tests.ClosedLoop;

/// <summary>
/// Shared oracle + engine for the <b>separation closed-loop gate</b> (<see cref="SeparationClosedLoopTests"/>):
/// renders each <see cref="SeparationClosedLoopGen.InstrumentPart"/> on its own
/// <see cref="MeltySynthSynthesizer"/> (one GM program per instance -- the whole trick), sums the
/// per-instrument renders into a mix, runs the (expensive, shared) <see cref="SpleeterSourceSeparator"/>
/// once per case, and scores each instrument's recovered stem against its own ground-truth render with
/// <see cref="SiSdr"/>. Unlike the polyphonic F1 gate this is a <b>regression guard</b>, not a release
/// claim: Spleeter is trained on real recordings, not synthesized GM renders, so the absolute numbers are
/// expected to be modest -- see <c>docs/CORPUS.md</c> "Corpus 3".
/// </summary>
public static class SeparationClosedLoop
{
    public const int SampleRateHz = SeparationClosedLoopGen.SampleRateHz;

    /// <summary>
    /// The committed regression-guard gate (median SI-SDR, dB, pooled across all instruments and cases
    /// in the default seed-4242, 6-case corpus). This is NOT a release-quality claim -- it exists only
    /// to catch a regression in the separation pipeline (STFT, masking, reconstruction, or the
    /// committed ONNX weights). First measured run (this machine): bass 17.01 dB / other (tenor sax)
    /// 11.70 dB / piano 6.44 dB median per-stem, <b>12.14 dB overall median</b>. Set here to 6.0 dB --
    /// about 6 dB (roughly a 4x power ratio) of headroom below the measured overall median, and at or
    /// below every individual stem's own median, so a same-machine rerun or the documented ONNX
    /// same-model/cross-architecture SIMD drift (see PolyphonicClosedLoopTests) has ample room without
    /// masking a real regression.
    /// TODO: freeze after first measured run -- revisit once this has run on CI hardware a few times.
    /// </summary>
    public const double GateThresholdDb = 6.0;

    public static SpleeterSourceSeparator CreateSeparator() =>
        new(SeparatorModelLocator.Resolve(null), new Radix2Fft());

    /// <summary>One <see cref="MeltySynthSynthesizer"/> per instrument, keyed by its target Spleeter
    /// stem name -- constructed once (each instance loads the committed SoundFont) and reused across
    /// every case, since the GM program (and thus the instrument) never changes case to case.</summary>
    public static IReadOnlyDictionary<string, MeltySynthSynthesizer> CreateSynthesizers(string soundFontPath) =>
        new Dictionary<string, MeltySynthSynthesizer>
        {
            [SeparationClosedLoopGen.BassTargetStem] = new MeltySynthSynthesizer(soundFontPath, SeparationClosedLoopGen.BassGmProgram),
            [SeparationClosedLoopGen.PianoTargetStem] = new MeltySynthSynthesizer(soundFontPath, SeparationClosedLoopGen.PianoGmProgram),
            [SeparationClosedLoopGen.SaxTargetStem] = new MeltySynthSynthesizer(soundFontPath, SeparationClosedLoopGen.SaxGmProgram),
        };

    /// <summary>One instrument's SI-SDR score against its own ground truth.</summary>
    public readonly record struct InstrumentScore(string TargetStem, double SiSdrDb);

    /// <summary>The full result of one case: per-instrument scores, plus the mix and ground-truth stems
    /// (kept so a failing case can be quarantined for reproduction).</summary>
    public sealed record CaseResult(
        IReadOnlyList<InstrumentScore> Scores,
        float[] Mix,
        IReadOnlyDictionary<string, float[]> GroundTruthStems);

    /// <summary>
    /// Renders every instrument part on its own synth, sums them into a mix, separates once, and scores
    /// each instrument's recovered stem against its own ground-truth render. The mix and every
    /// ground-truth stem are padded to the longest instrument's length before summing/scoring (an
    /// instrument's own release tail can run past another's).
    /// </summary>
    public static CaseResult RenderAndSeparate(
        SeparationClosedLoopGen.SeparationCase testCase,
        IReadOnlyDictionary<string, MeltySynthSynthesizer> synths,
        SpleeterSourceSeparator separator,
        SampleRate rate)
    {
        var groundTruth = new Dictionary<string, float[]>();
        int length = 0;
        foreach (SeparationClosedLoopGen.InstrumentPart part in testCase.Parts)
        {
            MeltySynthSynthesizer synth = synths[part.TargetStem];
            float[] pcm = synth.Render(part.Notes, rate);
            groundTruth[part.TargetStem] = pcm;
            length = Math.Max(length, pcm.Length);
        }

        var mix = new float[length];
        foreach (float[] pcm in groundTruth.Values)
        {
            for (int i = 0; i < pcm.Length; i++)
            {
                mix[i] += pcm[i];
            }
        }

        // Pad every ground-truth stem to the mix's length so SiSdr.Compute sees equal-length spans.
        foreach (string stem in groundTruth.Keys.ToList())
        {
            float[] pcm = groundTruth[stem];
            if (pcm.Length < length)
            {
                var padded = new float[length];
                Array.Copy(pcm, padded, pcm.Length);
                groundTruth[stem] = padded;
            }
        }

        var mixSource = new PcmAudioSource(mix, rate, new FrameParameters(4096, 1024));
        IReadOnlyList<SeparatedStem> recovered = separator.Separate(mixSource);

        var scores = new List<InstrumentScore>(testCase.Parts.Count);
        foreach (SeparationClosedLoopGen.InstrumentPart part in testCase.Parts)
        {
            SeparatedStem stem = recovered.First(s => s.Name == part.TargetStem);
            float[] recoveredPcm = Framing.ReconstructMono(stem.Audio.Frames.ToList());

            int n = Math.Min(recoveredPcm.Length, groundTruth[part.TargetStem].Length);
            double siSdr = SiSdr.Compute(
                recoveredPcm.AsSpan(0, n), groundTruth[part.TargetStem].AsSpan(0, n));
            scores.Add(new InstrumentScore(part.TargetStem, siSdr));
        }

        return new CaseResult(scores, mix, groundTruth);
    }
}
