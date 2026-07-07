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
/// </summary>
public sealed class ListenCommand
{
    private readonly LiveTranscriptionSession _session;
    private readonly INoteEventWriter _rawWriter;   // DryWetMidiWriter (raw performance)
    private readonly IScoreWriter _scoreWriter;     // DryWetMidiWriter (quantized MIDI)
    private readonly IScoreWriter? _musicXmlWriter; // MusicXmlScoreWriter; null until Step 11 registers it
    private readonly Action<string> _print;

    public ListenCommand(LiveTranscriptionSession session,
                         INoteEventWriter rawWriter, IScoreWriter scoreWriter,
                         Action<string> print, IScoreWriter? musicXmlWriter = null)
    {
        _session = session;
        _rawWriter = rawWriter;
        _scoreWriter = scoreWriter;
        _print = print;
        _musicXmlWriter = musicXmlWriter;
    }

    public LiveSessionResult Run(IAudioSource source, int tempoBpm, string outDir,
                                 CancellationToken ct = default)
    {
        Directory.CreateDirectory(outDir);
        _print($"Listening at {tempoBpm} BPM. Press Ctrl+C to stop.");

        // The live print streams notes incrementally; the returned result is the ACCURATE
        // batch transcription of the session's audio (R10.3) — that is what the files below use.
        // tempoBpm still denominates the raw-performance MIDI's tempo map (INoteEventWriter).
        var result = _session.Run(source, n => _print(FormatNote(n)), ct);
        var tempo = new Tempo(tempoBpm);

        string rawPath = Path.Combine(outDir, "raw.mid");
        string scorePath = Path.Combine(outDir, "score.mid");
        using (var raw = File.Create(rawPath))
            _rawWriter.Write(result.Events, tempo, raw);
        using (var score = File.Create(scorePath))
            _scoreWriter.Write(result.Score, score);
        _print($"Wrote {rawPath} and {scorePath}.");

        if (_musicXmlWriter is not null)
        {
            string xmlPath = Path.Combine(outDir, "score.musicxml");
            using (var xml = File.Create(xmlPath))
                _musicXmlWriter.Write(result.Score, xml);
            _print($"Wrote {xmlPath}.");
        }
        return result;
    }

    private static string FormatNote(NoteEvent n) =>
        $"note {n.Pitch.MidiNumber,3}  onset {n.Onset.Samples,10}  dur {n.Duration.Samples,8}";
}
