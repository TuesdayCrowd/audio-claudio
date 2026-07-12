using System.Linq;
using System.Text;
using AudioClaudio.Application;
using AudioClaudio.Cli.Cli;
using AudioClaudio.Cli.Commands;
using AudioClaudio.Cli.Composition;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Evaluation;
using AudioClaudio.Domain.Polyphony;
using AudioClaudio.Domain.Spectral;
using AudioClaudio.Infrastructure.Audio;
using AudioClaudio.Infrastructure.Midi;
using AudioClaudio.Infrastructure.MusicXml;
using AudioClaudio.Infrastructure.Synthesis;
using AudioClaudio.Infrastructure.Transcription;

namespace AudioClaudio.Cli;

/// <summary>
/// The composition root for the CLI-kernel migration (v2 Stage 5): builds the
/// <see cref="CommandLineApp"/> and registers all seven commands with their full option
/// surface. Handlers are wired one command at a time by later tasks (14–21); <see
/// cref="Program"/>'s top-level statements do nothing but call <see cref="Build"/>, wrap
/// <c>Run</c> in the top-level try/catch (Task 14), and return its exit code — every other
/// line of app behavior lives here so it is unit-testable in-process.
/// </summary>
public static class AppBuilder
{
    public static string Version { get; } =
        typeof(AppBuilder).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Builds one <see cref="AnsiStyler"/> for handler-printed messages (e.g. a styled
    /// "error:" prefix on a missing-file sentence), computed the same way
    /// <see cref="AnsiStyler.FromEnvironment"/> computes it internally for the kernel's own
    /// help/error rendering — so a handler's own color decisions never disagree with the
    /// kernel's (S5.6). Kept internal so Tasks 15+ can reuse it without recomputing.
    /// </summary>
    internal static AnsiStyler ConsoleStyler(bool noColor) =>
        new(
            interactiveTerminal: !Console.IsOutputRedirected,
            noColorEnvSet: !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")),
            noColorFlag: noColor);

    private static readonly SampleRate Rate = new(44100);

    /// <summary>S5.5's handler-level counterpart: turn a missing input file into one clean
    /// sentence via the handler's OWN stderr writer, before any reader/adapter touches the path.</summary>
    private static bool TryRequireFile(string path, TextWriter stderr, AnsiStyler styler)
    {
        if (File.Exists(path)) return true;
        stderr.Write($"{styler.Error("error:")} input file '{path}' not found\n");
        return false;
    }

    public static CommandLineApp Build(StringBuilder logBuffer, bool noColor)
    {
        ArgumentNullException.ThrowIfNull(logBuffer);
        var styler = ConsoleStyler(noColor);
        var app = new CommandLineApp("claudio", "a real-time piano transcriber", Version);

        var transcribe = new CliCommand("transcribe", "Transcribe a WAV recording to MIDI + MusicXML.")
            .WithArgument(new CliArgument("input.wav", "the recording to transcribe"))
            .WithOption(new CliOption("--tempo", OptionKind.Double, "declared tempo in BPM (auto-estimated if omitted)"))
            .WithOption(new CliOption("--out-dir", OptionKind.Path, "directory to write raw.mid/score.mid/score.musicxml", defaultValue: "."))
            .WithOption(new CliOption("--note-names", OptionKind.Flag, "print a scientific-pitch-name lyric under each note"))
            .WithOption(new CliOption("--mono", OptionKind.Flag, "use the monophonic YIN pipeline instead of the polyphonic default"))
            .WithOption(new CliOption("--model", OptionKind.String, "explicit model path, or 'transkun' for the Transkun engine"))
            .WithOption(new CliOption("--key", OptionKind.Int, "override the auto-detected key signature (sharps +, flats -)"))
            .WithOption(new CliOption("--onset-threshold", OptionKind.Double, "polyphonic onset activation threshold"))
            .WithOption(new CliOption("--frame-threshold", OptionKind.Double, "polyphonic sustained-frame activation threshold"))
            .WithOption(new CliOption("--min-note-len", OptionKind.Int, "polyphonic flicker floor in frames"))
            .WithOption(new CliOption("--legato", OptionKind.Flag, "(--mono) opt into legato note recovery"))
            .WithOption(new CliOption("--coarse-rhythm", OptionKind.Flag, "(--mono) floor note values at an eighth"))
            .WithOption(new CliOption("--triplets", OptionKind.Flag, "engrave eighth-note triplets"))
            .WithExample("claudio transcribe song.wav --out-dir out");
        app.Register(transcribe, (p, stdout, stderr) =>
        {
            if (!TryRequireFile(p.Argument("input.wav"), stderr, styler)) return 1;

            double? tempo = p.Double("tempo");
            string outDir = p.Path("out-dir") ?? ".";
            bool noteNames = p.Flag("note-names");
            bool legato = p.Flag("legato");
            bool coarseRhythm = p.Flag("coarse-rhythm");
            bool poly = TranscribeModeResolver.Resolve(p) == TranscribeMode.Polyphonic;

            if (!KeyOption.Validate(p.Int("key"), out string? keyError))
            {
                stderr.Write($"{styler.Error("error:")} {keyError}\n");
                return 1;
            }

            if (poly && p.String("model") == "transkun")
            {
                // Transkun engine (v2 Stage 4): notation-fidelity piano transcription (real durations +
                // sustain pedal), self-contained via ONNX. Core-first — frame-resolution timing, no velocity.
                Directory.CreateDirectory(outDir);
                using var tkSource = WavAudioSource.FromFile(p.Argument("input.wav"), new FrameParameters(1024, 256));
                using var tk = new TranskunTranscriber(TranskunModelLocator.Resolve(), new Radix2Fft());
                (var tkNotes, var tkPedal) = tk.TranscribeDetailed(tkSource);
                Tempo tkTempo = tempo is { } tb ? new Tempo(tb) : TempoEstimator.Estimate(tkNotes, new Tempo(120));
                int tkKey = p.Int("key") ?? KeyDetector.Detect(tkNotes.Select(e => e.Pitch).ToList());

                var tkWriter = new DryWetMidiWriter();
                using (var raw = File.Create(Path.Combine(outDir, "raw.mid")))
                    tkWriter.Write(tkNotes, tkTempo, raw);

                var tkSubdivision = p.Flag("triplets") ? Subdivision.Twelfth : Subdivision.Sixteenth;
                var tkGrid = new QuantizationGrid(Rate, tkTempo, TimeSignature.FourFour, tkSubdivision);
                var tkChordWindow = new SampleDuration(Rate.Hz / 20, Rate);
                var tkGrandStaff = PolyphonicQuantizer.Quantize(tkNotes, tkGrid, tkChordWindow);
                var tkPedalMarks = tkPedal.Select(c => ((int)tkGrid.SamplesToTick(c.Sample), c.Down)).ToList();
                using (var mx = File.Create(Path.Combine(outDir, "score.musicxml")))
                    new GrandStaffMusicXmlWriter(noteNames, fifths: tkKey).Write(tkGrandStaff, mx, tkPedalMarks);
                var tkQuantized = GrandStaffFlattener.ToNoteEvents(tkGrandStaff, tkGrid);
                using (var score = File.Create(Path.Combine(outDir, "score.mid")))
                    tkWriter.Write(tkQuantized, tkTempo, score);
                stdout.WriteLine($"Transkun transcription: {tkNotes.Count} notes -> raw.mid; {tkQuantized.Count} quantized -> score.mid + score.musicxml (grand staff, {tkGrandStaff.Measures.Count} bars, key {tkKey:+#;-#;0}, {tkPedal.Count / 2} pedal spans)");
                File.WriteAllText(Path.Combine(outDir, "log.txt"), logBuffer.ToString());
                return 0;
            }

            if (poly)
            {
                // Polyphonic path (Basic Pitch). raw.mid is the honest many-note output; score.mid/
                // score.musicxml are quantized with the grand-staff quantizer.
                Directory.CreateDirectory(outDir);
                string modelPath = ModelLocator.Resolve(p.String("model"));
                using var polySource = WavAudioSource.FromFile(p.Argument("input.wav"), new FrameParameters(1024, 256));
                var decoderOptions = PolyDecoderOptions.FromArgs(p); // --onset-threshold/--frame-threshold/--min-note-len
                using var polyTx = new BasicPitchTranscriber(modelPath, decoderOptions, tempo: tempo is { } bpm ? new Tempo(bpm) : null);
                TranscriptionResult polyResult = polyTx.Transcribe(polySource);
                // Auto-detect the key signature from the notes (Krumhansl-Schmuckler) unless --key declares it.
                int key = p.Int("key") ?? KeyDetector.Detect(polyResult.RawEvents.Select(e => e.Pitch).ToList());
                var polyWriter = new DryWetMidiWriter();
                using (var raw = File.Create(Path.Combine(outDir, "raw.mid")))
                    polyWriter.Write(polyResult.RawEvents, polyResult.Score.Tempo, raw); // honest un-quantized polyphony

                // Stage 3: quantize into a polyphonic grand-staff score (chords + two staves), then
                // emit both score.musicxml (grand staff) and a flattened polyphonic score.mid.
                var polyRate = new SampleRate(BasicPitchModel.SampleRateHz);
                var polySubdivision = p.Flag("triplets") ? Subdivision.Twelfth : Subdivision.Sixteenth;
                var polyGrid = new QuantizationGrid(polyRate, polyResult.Score.Tempo, TimeSignature.FourFour, polySubdivision);
                var chordWindow = new SampleDuration(polyRate.Hz / 20, polyRate); // ~50 ms: notes this close are one chord
                var grandStaff = PolyphonicQuantizer.Quantize(polyResult.RawEvents, polyGrid, chordWindow);
                using (var mx = File.Create(Path.Combine(outDir, "score.musicxml")))
                    new GrandStaffMusicXmlWriter(noteNames, fifths: key).Write(grandStaff, mx);
                var quantized = GrandStaffFlattener.ToNoteEvents(grandStaff, polyGrid);
                using (var score = File.Create(Path.Combine(outDir, "score.mid")))
                    polyWriter.Write(quantized, polyResult.Score.Tempo, score);
                stdout.WriteLine($"Polyphonic transcription: {polyResult.RawEvents.Count} notes -> raw.mid; {quantized.Count} quantized -> score.mid + score.musicxml (grand staff, {grandStaff.Measures.Count} bars, key {key:+#;-#;0}{(p.Int("key") is null ? " detected" : " declared")})");
                File.WriteAllText(Path.Combine(outDir, "log.txt"), logBuffer.ToString());
                return 0;
            }

            TranscribeCommand.Run(p.Argument("input.wav"), tempo, outDir, noteNames, legato, coarseRhythm);
            File.WriteAllText(Path.Combine(outDir, "log.txt"), logBuffer.ToString());
            return 0;
        });

        var notate = new CliCommand("notate", "Engrave an existing MIDI file as a grand-staff score.")
            .WithArgument(new CliArgument("input.mid", "the MIDI file to notate"))
            .WithOption(new CliOption("--out-dir", OptionKind.Path, "directory to write score.mid/score.musicxml", defaultValue: "."))
            .WithOption(new CliOption("--tempo", OptionKind.Double, "declared tempo in BPM (auto-estimated if omitted)"))
            .WithOption(new CliOption("--key", OptionKind.Int, "override the auto-detected key signature"))
            .WithOption(new CliOption("--note-names", OptionKind.Flag, "print a scientific-pitch-name lyric under each note"))
            .WithOption(new CliOption("--triplets", OptionKind.Flag, "engrave eighth-note triplets"))
            .WithExample("claudio notate performance.mid --out-dir out");
        app.Register(notate, (p, stdout, stderr) =>
        {
            if (!TryRequireFile(p.Argument("input.mid"), stderr, styler)) return 1;

            string outDir = p.Path("out-dir") ?? ".";
            Directory.CreateDirectory(outDir);
            bool noteNames = p.Flag("note-names");

            if (!KeyOption.Validate(p.Int("key"), out string? keyError))
            {
                stderr.Write($"{styler.Error("error:")} {keyError}\n");
                return 1;
            }

            var read = MidiFileReader.ReadFile(p.Argument("input.mid"), Rate, flattenPedal: false);
            int key = p.Int("key") ?? KeyDetector.Detect(read.Events.Select(e => e.Pitch).ToList());
            double? tempoArg = p.Double("tempo");
            Tempo scoreTempo = tempoArg is null
                ? TempoEstimator.Estimate(read.Events, read.Tempo)
                : new Tempo(tempoArg.Value);
            var notateSubdivision = p.Flag("triplets") ? Subdivision.Twelfth : Subdivision.Sixteenth;
            var grid = new QuantizationGrid(Rate, scoreTempo, TimeSignature.FourFour, notateSubdivision);
            var chordWindow = new SampleDuration(Rate.Hz / 20, Rate);
            var grandStaff = PolyphonicQuantizer.Quantize(read.Events, grid, chordWindow);
            var writer = new DryWetMidiWriter();
            var pedalMarks = read.PedalChanges.Select(c => ((int)grid.SamplesToTick(c.Sample), c.Down)).ToList();
            using (var mx = File.Create(Path.Combine(outDir, "score.musicxml")))
                new GrandStaffMusicXmlWriter(noteNames, fifths: key).Write(grandStaff, mx, pedalMarks);
            var quantized = GrandStaffFlattener.ToNoteEvents(grandStaff, grid);
            using (var score = File.Create(Path.Combine(outDir, "score.mid")))
                writer.Write(quantized, scoreTempo, score);
            stdout.WriteLine($"Notated {read.Events.Count} notes ({scoreTempo.BeatsPerMinute:F0} BPM{(tempoArg is null ? " estimated" : "")}) -> score.musicxml + score.mid (grand staff, {grandStaff.Measures.Count} bars, key {key:+#;-#;0}{(p.Int("key") is null ? " detected" : " declared")})");
            return 0;
        });

        var render = new CliCommand("render", "Render a MIDI file to a deterministic WAV.")
            .WithArgument(new CliArgument("input.mid", "the MIDI file to render"))
            .WithArgument(new CliArgument("output.wav", "the WAV file to write"))
            .WithOption(new CliOption("--soundfont", OptionKind.Path, "explicit SoundFont path (auto-discovered otherwise)"))
            .WithExample("claudio render song.mid song.wav");
        app.Register(render, (p, stdout, stderr) =>
        {
            if (!TryRequireFile(p.Argument("input.mid"), stderr, styler)) return 1;
            var notes = MidiFileReader.ReadFile(p.Argument("input.mid"), Rate).Events;
            var synth = new MeltySynthSynthesizer(
                AudioClaudio.Cli.Composition.SoundFontLocator.Resolve(p.Path("soundfont")));
            RenderCommand.RenderToWav(notes, synth, Rate, p.Argument("output.wav"));
            return 0;
        });

        var play = new CliCommand("play", "Play a MIDI file through the default audio device.")
            .WithArgument(new CliArgument("input.mid", "the MIDI file to play"))
            .WithOption(new CliOption("--soundfont", OptionKind.Path, "explicit SoundFont path (auto-discovered otherwise)"))
            .WithExample("claudio play song.mid");
        app.Register(play, (p, stdout, stderr) =>
        {
            if (!TryRequireFile(p.Argument("input.mid"), stderr, styler)) return 1;
            var notes = MidiFileReader.ReadFile(p.Argument("input.mid"), Rate).Events;
            var synth = new MeltySynthSynthesizer(
                AudioClaudio.Cli.Composition.SoundFontLocator.Resolve(p.Path("soundfont")));
            PlayCommand.Play(notes, synth, Rate);
            return 0;
        });

        var evaluate = new CliCommand("evaluate", "Score a candidate transcription against a reference note-set.")
            .WithArgument(new CliArgument("candidate.mid", "the transcription to evaluate"))
            .WithArgument(new CliArgument("reference.mid", "the ground-truth reference"))
            .WithOption(new CliOption("--onset-tolerance-ms", OptionKind.Double, "onset matching tolerance in ms (default 50)"))
            .WithOption(new CliOption("--align", OptionKind.Flag, "cancel a global tempo ratio before scoring"))
            .WithOption(new CliOption("--warp", OptionKind.Flag, "DTW-warp to also remove local rubato (wins over --align)"))
            .WithExample("claudio evaluate out/score.mid reference.mid --align");
        app.Register(evaluate, (p, stdout, stderr) =>
        {
            if (!TryRequireFile(p.Argument("candidate.mid"), stderr, styler)) return 1;
            if (!TryRequireFile(p.Argument("reference.mid"), stderr, styler)) return 1;

            var candidate = MidiFileReader.ReadFile(p.Argument("candidate.mid"), Rate).Events;
            var reference = MidiFileReader.ReadFile(p.Argument("reference.mid"), Rate).Events;
            double tolMs = p.Double("onset-tolerance-ms") ?? 50.0;

            bool warp = p.Flag("warp");
            bool align = p.Flag("align");
            IReadOnlyList<NoteEvent> evalCandidate = candidate;
            if (warp)
            {
                evalCandidate = OnsetAlignment.DtwWarp(candidate, reference);
                stdout.WriteLine("(candidate DTW-warped to the reference timeline — local rubato removed)");
            }
            else if (align)
            {
                evalCandidate = OnsetAlignment.GlobalScale(candidate, reference);
                stdout.WriteLine("(candidate globally time-aligned to the reference span)");
            }

            EvaluateCommand.Run(evalCandidate, reference, new NoteMatchOptions(tolMs / 1000.0), stdout.WriteLine);
            return 0;
        });

        var evaluateAudio = new CliCommand("evaluate-audio", "Compare two WAVs by pitch-content (chroma) similarity.")
            .WithArgument(new CliArgument("original.wav", "the original recording"))
            .WithArgument(new CliArgument("reproduction.wav", "the re-synthesized recording"))
            .WithExample("claudio evaluate-audio input.wav recreation.wav");
        app.Register(evaluateAudio, (p, stdout, stderr) =>
        {
            if (!TryRequireFile(p.Argument("original.wav"), stderr, styler)) return 1;
            if (!TryRequireFile(p.Argument("reproduction.wav"), stderr, styler)) return 1;

            const int FrameSize = 4096, Hop = 2048;
            using var audioA = WavAudioSource.FromFile(p.Argument("original.wav"), new FrameParameters(FrameSize, Hop));
            using var audioB = WavAudioSource.FromFile(p.Argument("reproduction.wav"), new FrameParameters(FrameSize, Hop));
            var chromaA = Chromagram.FromFrames(audioA.Frames, FrameSize);
            var chromaB = Chromagram.FromFrames(audioB.Frames, FrameSize);
            double similarity = ChromaSimilarity.Compare(chromaA, chromaB);
            stdout.WriteLine($"Chroma (pitch-content) similarity: {CliFormat.Percent(similarity)}  ({chromaA.Count} vs {chromaB.Count} frames)");
            return 0;
        });

        var listen = new CliCommand("listen", "Transcribe live from the microphone.")
            .WithOption(new CliOption("--tempo", OptionKind.Double, "declared tempo in BPM (auto-estimated if omitted)"))
            .WithOption(new CliOption("--out-dir", OptionKind.Path, "directory to write raw.mid/score.mid/score.musicxml", defaultValue: "."))
            .WithOption(new CliOption("--view", OptionKind.Flag, "open a live sheet-music browser view"))
            .WithOption(new CliOption("--record", OptionKind.Flag, "also write input.wav + recreation.wav"))
            .WithOption(new CliOption("--skip-silence", OptionKind.Flag, "collapse pauses > 500ms (implies --record)"))
            .WithOption(new CliOption("--note-names", OptionKind.Flag, "print a scientific-pitch-name lyric under each note"))
            .WithOption(new CliOption("--soundfont", OptionKind.Path, "explicit SoundFont for the --record recreation (auto-discovered otherwise)"))
            .WithOption(new CliOption("--poly", OptionKind.Flag, "(prototype) near-real-time POLYPHONIC live view (requires --view)"))
            .WithExample("claudio listen --view --record");
        app.Register(listen, (p, stdout, stderr) => ListenAppCommand.Run(p, stdout, stderr, logBuffer));

        return app;
    }
}
