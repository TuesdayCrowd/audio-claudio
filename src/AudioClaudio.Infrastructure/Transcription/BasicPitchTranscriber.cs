using System.Collections.Generic;
using System.Linq;
using AudioClaudio.Application;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Polyphony;

namespace AudioClaudio.Infrastructure.Transcription;

/// <summary>
/// A polyphonic <see cref="ITranscriber"/> built on Spotify's Basic Pitch (the second transcriber
/// behind the same port — §8 item 1). It reconstructs the source's mono signal, resamples it to the
/// model's 22 050 Hz, runs the model over overlapping ~2 s windows (front-padded, then the 30-frame
/// overlap trimmed away so windows stitch seamlessly), decodes the note/onset posteriorgrams into
/// discrete notes (<see cref="BasicPitchNoteDecoder"/>), and maps the model's frame indices back to
/// sample time. Unlike <see cref="TranscriptionPipeline"/> it recovers many simultaneous notes.
///
/// The produced <see cref="TranscriptionResult.RawEvents"/> is the honest polyphonic output; the
/// <see cref="TranscriptionResult.Score"/> is quantized with the existing (monophonic) quantizer for
/// now — polyphonic score-building (chords, two staves, voices) is a later stage.
/// </summary>
public sealed class BasicPitchTranscriber : ITranscriber, IDisposable
{
    // Basic Pitch windowing constants (from its constants.py / inference.py).
    private const int OverlapLength = 30 * 256;                       // 7680 samples overlapped between windows
    private const int FrontPad = OverlapLength / 2;                   // 3840 samples of leading zeros
    private const int HopSamples = BasicPitchModel.WindowSamples - OverlapLength; // 36164
    private const int TrimFrames = 15;                               // half the 30-frame overlap, trimmed each side
    private const int KeptFramesPerWindow = BasicPitchModel.FramesPerWindow - (2 * TrimFrames); // 142
    private const int FftHop = 256;

    private readonly BasicPitchModel _model;
    private readonly NoteDecoderOptions _decoderOptions;
    private readonly Tempo _tempo;
    private readonly TimeSignature _timeSignature;
    private readonly Subdivision _subdivision;

    public BasicPitchTranscriber(
        string modelPath,
        NoteDecoderOptions? decoderOptions = null,
        Tempo? tempo = null,
        TimeSignature? timeSignature = null,
        Subdivision subdivision = Subdivision.Sixteenth)
    {
        _model = new BasicPitchModel(modelPath);
        _decoderOptions = decoderOptions ?? NoteDecoderOptions.Default;
        _tempo = tempo ?? new Tempo(120);
        _timeSignature = timeSignature ?? TimeSignature.FourFour;
        _subdivision = subdivision;
    }

    public TranscriptionResult Transcribe(IAudioSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var frames = source.Frames.ToList();
        var rate22 = new SampleRate(BasicPitchModel.SampleRateHz);
        if (frames.Count == 0)
        {
            var emptyGrid = new QuantizationGrid(rate22, _tempo, _timeSignature, _subdivision);
            return new TranscriptionResult(Quantizer.Quantize(Array.Empty<NoteEvent>(), emptyGrid), Array.Empty<NoteEvent>());
        }

        int sourceRate = frames[0].Rate.Hz;
        float[] mono = Framing.ReconstructMono(frames);
        float[] audio = AudioResampler.Resample(mono, sourceRate, BasicPitchModel.SampleRateHz);

        (float[,] noteGrid, float[,] onsetGrid) = RunWindows(audio);
        IReadOnlyList<BasicPitchNote> decoded = BasicPitchNoteDecoder.Decode(noteGrid, onsetGrid, _decoderOptions);

        var events = new List<NoteEvent>(decoded.Count);
        foreach (BasicPitchNote note in decoded)
        {
            if (note.MidiPitch < Pitch.MinMidi || note.MidiPitch > Pitch.MaxMidi)
            {
                continue; // outside the 88-key range (shouldn't happen; guard for safety)
            }

            long onset = FrameToSample(note.StartFrame);
            long end = FrameToSample(note.EndFrame);
            long duration = System.Math.Max(1, end - onset);
            int velocity = System.Math.Clamp((int)System.Math.Round(note.Amplitude * 127.0), 1, 127);

            events.Add(new NoteEvent(
                new Pitch(note.MidiPitch),
                new SamplePosition(onset, rate22),
                new SampleDuration(duration, rate22),
                velocity));
        }

        events.Sort((a, b) => a.Onset.Samples != b.Onset.Samples
            ? a.Onset.Samples.CompareTo(b.Onset.Samples)
            : a.Pitch.MidiNumber.CompareTo(b.Pitch.MidiNumber));

        var grid = new QuantizationGrid(rate22, _tempo, _timeSignature, _subdivision);
        Score score = Quantizer.Quantize(events, grid);
        return new TranscriptionResult(score, events);
    }

    // Runs the model over the (front-padded) signal in overlapping windows and stitches the
    // per-window outputs into full note-frame and onset grids, trimming the overlap so frames
    // are contiguous.
    private (float[,] Notes, float[,] Onsets) RunWindows(float[] audio)
    {
        var padded = new float[FrontPad + audio.Length];
        Array.Copy(audio, 0, padded, FrontPad, audio.Length);

        var noteRows = new List<float[]>();
        var onsetRows = new List<float[]>();
        var window = new float[BasicPitchModel.WindowSamples];

        for (int start = 0; start < padded.Length; start += HopSamples)
        {
            Array.Clear(window);
            int count = System.Math.Min(BasicPitchModel.WindowSamples, padded.Length - start);
            Array.Copy(padded, start, window, 0, count);

            BasicPitchWindowOutput output = _model.Run(window);
            for (int f = TrimFrames; f < BasicPitchModel.FramesPerWindow - TrimFrames; f++)
            {
                noteRows.Add(RowOf(output.NoteFrames, f, BasicPitchModel.PitchBins));
                onsetRows.Add(RowOf(output.Onsets, f, BasicPitchModel.PitchBins));
            }
        }

        // Drop the frames past the real (pre-pad) signal length — the last window was zero-padded.
        int keep = System.Math.Min(noteRows.Count, (int)((double)audio.Length / HopSamples * KeptFramesPerWindow));
        keep = System.Math.Max(keep, 0);
        return (ToGrid(noteRows, keep, BasicPitchModel.PitchBins), ToGrid(onsetRows, keep, BasicPitchModel.PitchBins));
    }

    private static float[] RowOf(float[,] grid, int row, int cols)
    {
        var r = new float[cols];
        for (int c = 0; c < cols; c++)
        {
            r[c] = grid[row, c];
        }

        return r;
    }

    private static float[,] ToGrid(List<float[]> rows, int rowCount, int cols)
    {
        var grid = new float[rowCount, cols];
        for (int i = 0; i < rowCount; i++)
        {
            for (int c = 0; c < cols; c++)
            {
                grid[i, c] = rows[i][c];
            }
        }

        return grid;
    }

    // Global (stitched) frame index -> sample position at 22 050 Hz. Each kept frame f (0..141) of
    // window w maps to padded sample w·HopSamples + (TrimFrames+f)·FftHop; subtract the front pad to
    // get the real position. Exact for the overlap-trim stitching this class performs.
    private static long FrameToSample(int globalFrame)
    {
        long w = globalFrame / KeptFramesPerWindow;
        long f = globalFrame % KeptFramesPerWindow;
        long sample = (w * HopSamples) + ((TrimFrames + f) * FftHop) - FrontPad;
        return System.Math.Max(0, sample);
    }

    public void Dispose() => _model.Dispose();
}
