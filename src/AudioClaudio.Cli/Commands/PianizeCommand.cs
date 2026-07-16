using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AudioClaudio.Application.Ports;
using AudioClaudio.Application.UseCases;
using AudioClaudio.Cli.Composition;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Polyphony;
using AudioClaudio.Domain.Spectral; // Radix2Fft
using AudioClaudio.Infrastructure.Audio; // WavAudioSource, WavFileWriter
using AudioClaudio.Infrastructure.Midi; // MultiTrackMidiWriter, DryWetMidiWriter
using AudioClaudio.Infrastructure.MusicXml; // GrandStaffMusicXmlWriter
using AudioClaudio.Infrastructure.Separation; // SpleeterSourceSeparator
using AudioClaudio.Infrastructure.Synthesis; // MeltySynthSynthesizer

namespace AudioClaudio.Cli.Commands;

/// <summary>
/// Wires the batch "all notes on piano" pipeline for
/// <c>claudio pianize &lt;mix.wav&gt;</c> (DECISIONS.md "Multi-instrument -&gt; piano"): separates the
/// mix into stems (Stage 1), transcribes every pitched stem through its routed engine (Stage 2 --
/// Transkun on <c>piano</c>, Basic Pitch on <c>bass</c>/<c>other</c>/<c>vocals</c>; <c>drums</c> has
/// no routing entry and is silently skipped), writes a faithful <c>multitrack.mid</c> (Stage 3, one
/// track per instrument INCLUDING vocals, always), then merges the *included* stems' notes (vocals
/// excluded unless <c>--include-vocals</c>) into one grand-staff piano arrangement --
/// <c>score.mid</c> + <c>score.musicxml</c> -- and renders those same notes on piano as
/// <c>recreation.wav</c>. Root-only output, mirroring <see cref="SeparateCommand"/>/
/// <see cref="TranscribeCommand"/>. This is a dense, faithful "all notes on piano" rendering, NOT a
/// playable reduction -- a combo's combined stems routinely exceed two hands.
/// </summary>
public static class PianizeCommand
{
    // Framing used only to READ the input mix; SpleeterSourceSeparator reconstructs the whole
    // buffer internally and resamples as needed, so any size/hop works here (mirrors SeparateCommand).
    private static readonly FrameParameters InputFrameParameters = new(4096, 4096);

    // The common rate every stem's transcription is reconciled to (MultiStemTranscriber's target
    // rate) before merge/quantize/render -- also Spleeter's own fixed working rate.
    private static readonly SampleRate OutputRate = new(44100);

    private const string VocalsStemName = "vocals";

    /// <summary>Everything a caller needs to print a one-line-per-artifact summary.</summary>
    public sealed record Result(
        IReadOnlyList<SeparatedStem> Stems,
        IReadOnlyList<StemTranscription> Transcriptions,
        IReadOnlyList<NoteEvent> MergedNotes,
        GrandStaffScore GrandStaff,
        Tempo Tempo,
        bool TempoEstimated,
        int Key,
        bool KeyDetected);

    public static Result Run(
        string mixWav,
        string outDir,
        string? separatorModelDir,
        double? tempoBpm,
        int? keyFifths,
        bool includeVocals,
        bool includeNoteNames,
        bool triplets,
        string? soundfontPath)
    {
        Directory.CreateDirectory(outDir);

        // 1. Separate the mix (Stage 1) and write the 5 stem WAVs so the intermediates are
        // inspectable, exactly as `separate` does on its own.
        IReadOnlyList<SeparatedStem> stems;
        using (var source = WavAudioSource.FromFile(mixWav, InputFrameParameters))
        using (var separator = new SpleeterSourceSeparator(SeparatorModelLocator.Resolve(separatorModelDir), new Radix2Fft()))
        {
            stems = separator.Separate(source);
        }

        foreach (SeparatedStem stem in stems)
        {
            float[] pcm = Framing.ReconstructMono(stem.Audio.Frames.ToList());
            WavFileWriter.Write(Path.Combine(outDir, $"{stem.Name}.wav"), pcm, OutputRate);
        }

        // 2. Transcribe every routed stem (Stage 2): Transkun on piano, Basic Pitch on the rest;
        // drums has no routing entry and is silently skipped. Notes come back rescaled to 44100 Hz.
        IReadOnlyList<StemTranscription> transcriptions;
        (IReadOnlyList<StemRoute> routing, IDisposable transcribers) = MultiStemRouting.Build();
        try
        {
            transcriptions = new MultiStemTranscriber(routing, OutputRate).Transcribe(stems);
        }
        finally
        {
            transcribers.Dispose();
        }

        // Merge the notes going into the PIANO arrangement: every included stem (vocals excluded
        // unless --include-vocals; drums already absent). Tempo/key are declared or derived from
        // THIS merged set -- the one number multitrack.mid, score.mid/musicxml, and recreation.wav
        // all share.
        List<NoteEvent> mergedNotes = transcriptions
            .Where(t => includeVocals || t.StemName != VocalsStemName)
            .SelectMany(t => t.Notes)
            .ToList();

        bool tempoEstimated = tempoBpm is null;
        Tempo tempo = tempoBpm is { } bpm ? new Tempo(bpm) : TempoEstimator.Estimate(mergedNotes, new Tempo(120));

        bool keyDetected = keyFifths is null;
        int key = keyFifths ?? KeyDetector.Detect(mergedNotes.Select(n => n.Pitch).ToList());

        // 3. multitrack.mid (Stage 3): EVERY routed stem, vocals always included regardless of
        // --include-vocals -- the notes are never lost even when excluded from the piano arrangement.
        using (var multitrack = File.Create(Path.Combine(outDir, "multitrack.mid")))
        {
            var tracks = transcriptions.Select(t => (t.StemName, t.GmProgram, t.Notes)).ToList();
            new MultiTrackMidiWriter().Write(tracks, tempo, multitrack);
        }

        // 4/5. Merge -> grand-staff piano score: score.mid + score.musicxml (every included note,
        // treble/bass split -- the existing polyphonic grand-staff pipeline, not a reduction).
        var subdivision = triplets ? Subdivision.Twelfth : Subdivision.Sixteenth;
        var grid = new QuantizationGrid(OutputRate, tempo, TimeSignature.FourFour, subdivision);
        var chordWindow = new SampleDuration(OutputRate.Hz / 20, OutputRate); // ~50 ms
        GrandStaffScore grandStaff = PolyphonicQuantizer.Quantize(mergedNotes, grid, chordWindow);

        using (var mx = File.Create(Path.Combine(outDir, "score.musicxml")))
        {
            new GrandStaffMusicXmlWriter(includeNoteNames, fifths: key).Write(grandStaff, mx);
        }

        var flat = GrandStaffFlattener.ToNoteEvents(grandStaff, grid);
        using (var score = File.Create(Path.Combine(outDir, "score.mid")))
        {
            new DryWetMidiWriter().Write(flat, tempo, score);
        }

        // 6. Render the SAME merged notes on piano (program 0) -> recreation.wav.
        var synth = new MeltySynthSynthesizer(SoundFontLocator.Resolve(soundfontPath));
        RenderCommand.RenderToWav(mergedNotes, synth, OutputRate, Path.Combine(outDir, "recreation.wav"));

        return new Result(stems, transcriptions, mergedNotes, grandStaff, tempo, tempoEstimated, key, keyDetected);
    }
}
