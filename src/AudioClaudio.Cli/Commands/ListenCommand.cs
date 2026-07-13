using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AudioClaudio.Application.Ports;
using AudioClaudio.Application.UseCases;
using AudioClaudio.Domain;

namespace AudioClaudio.Cli.Commands;

/// <summary>
/// The `listen` command: run a live transcription, print each detected note as
/// it occurs, and on stop write the session's raw MIDI, quantized MIDI, and
/// (when a writer is supplied — Step 11) MusicXML. Writers are Stream-based
/// (CONTRACTS §7/§11); this composition-layer command opens the FileStreams and
/// calls them. Capture and detection code stay untouched (R10.3/R10.4).
///
/// <paramref name="onLiveNote"/>/<paramref name="onFinalScore"/> (live-notation view, Phase-2
/// §8 item 3) are OPTIONAL hooks, both null by default, so every existing caller/test is
/// unaffected. They exist because the live device's note stream is enumerated EXACTLY ONCE, by
/// this class's own call to <see cref="LiveTranscriptionSession.Run"/> -- a second, independent
/// consumer of the same live source is not possible (see the live-notation design doc).
/// <c>onLiveNote</c> fires once per note, at the same moment as the console print;
/// <c>onFinalScore</c> fires once, after the accurate batch <see cref="Score"/> is computed,
/// with THAT (not a live approximation) score.
/// </summary>
public sealed class ListenCommand
{
    private readonly LiveTranscriptionSession _session;
    private readonly INoteEventWriter _rawWriter;   // DryWetMidiWriter (raw performance)
    private readonly IScoreWriter _scoreWriter;     // DryWetMidiWriter (quantized MIDI)
    private readonly IScoreWriter? _musicXmlWriter; // MusicXmlScoreWriter; null until Step 11 registers it
    private readonly Action<string> _print;
    private readonly Action<NoteEvent>? _onLiveNote;
    private readonly Action<Score>? _onFinalScore;

    public ListenCommand(LiveTranscriptionSession session,
                         INoteEventWriter rawWriter, IScoreWriter scoreWriter,
                         Action<string> print, IScoreWriter? musicXmlWriter = null,
                         Action<NoteEvent>? onLiveNote = null, Action<Score>? onFinalScore = null)
    {
        _session = session;
        _rawWriter = rawWriter;
        _scoreWriter = scoreWriter;
        _print = print;
        _musicXmlWriter = musicXmlWriter;
        _onLiveNote = onLiveNote;
        _onFinalScore = onFinalScore;
    }

    /// <remarks>
    /// <paramref name="overrideSampleRate"/>/<paramref name="overrideTimeSignature"/> together form
    /// a per-take time-signature override (the live-view browser's Time selector): when BOTH are
    /// supplied, the batch pass's raw <see cref="LiveSessionResult.Events"/> are re-quantized onto a
    /// grid identical to the pipeline's own EXCEPT for the time signature -- preserving whatever
    /// tempo the pipeline settled on (declared or estimated; the same tempo-estimation asymmetry
    /// already accepted for the live-preview grid, see <c>ListenAppCommand</c>) while making the
    /// SAVED score.mid/score.musicxml (and the final live-view publish) follow the take's chosen
    /// signature. <paramref name="overrideSampleRate"/> must equal the rate <see
    /// cref="LiveSessionResult.Events"/>' onsets are declared at (the caller already knows this --
    /// it is the same rate the mic/source was opened at). Null (the default) keeps the pipeline's
    /// own <see cref="Score"/> untouched, exactly as before this parameter existed.
    /// </remarks>
    public LiveSessionResult Run(IAudioSource source, int tempoBpm, string outDir,
                                 CancellationToken ct = default,
                                 SampleRate? overrideSampleRate = null,
                                 TimeSignature? overrideTimeSignature = null)
    {
        Directory.CreateDirectory(outDir);
        _print($"Listening at {tempoBpm} BPM. Press Ctrl+C to stop.");

        // The live print streams notes incrementally; the returned result is the ACCURATE
        // batch transcription of the session's audio (R10.3) — that is what the files below use.
        // tempoBpm still denominates the raw-performance MIDI's tempo map (INoteEventWriter).
        var result = _session.Run(source, n =>
        {
            _print(FormatNote(n));
            SafeInvokeHook(_onLiveNote, n, "onLiveNote");
        }, ct);

        Score scoreToWrite = result.Score;
        if (overrideSampleRate is { } rate && overrideTimeSignature is { } timeSignature)
        {
            var overrideGrid = new QuantizationGrid(rate, result.Score.Tempo, timeSignature, result.Score.Subdivision);
            scoreToWrite = Quantizer.Quantize(result.Events, overrideGrid);
        }

        SafeInvokeHook(_onFinalScore, scoreToWrite, "onFinalScore");

        var tempo = new Tempo(tempoBpm);

        string rawPath = Path.Combine(outDir, "raw.mid");
        string scorePath = Path.Combine(outDir, "score.mid");
        using (var raw = File.Create(rawPath))
            _rawWriter.Write(result.Events, tempo, raw);
        using (var score = File.Create(scorePath))
            _scoreWriter.Write(scoreToWrite, score);
        _print($"Wrote {rawPath} and {scorePath}.");

        if (_musicXmlWriter is not null)
        {
            string xmlPath = Path.Combine(outDir, "score.musicxml");
            using (var xml = File.Create(xmlPath))
                _musicXmlWriter.Write(scoreToWrite, xml);
            _print($"Wrote {xmlPath}.");
        }
        return result;
    }

    // The live-view hooks are OPTIONAL and drive a best-effort side channel (the browser sheet).
    // A misbehaving hook -- e.g. the live server died mid-session -- must NEVER propagate out and
    // abort the session before the raw.mid/score.mid/score.musicxml trio is written, which is the
    // CORE job of `listen` (R10.3). So each hook is invoked defensively: on failure, report via the
    // existing print delegate and carry on.
    private void SafeInvokeHook<T>(Action<T>? hook, T argument, string hookName)
    {
        if (hook is null)
        {
            return;
        }

        try
        {
            hook(argument);
        }
        catch (Exception ex)
        {
            _print($"live view: {hookName} hook failed ({ex.Message}); continuing.");
        }
    }

    private static string FormatNote(NoteEvent n) =>
        $"note {n.Pitch.MidiNumber,3}  onset {n.Onset.Samples,10}  dur {n.Duration.Samples,8}";
}
