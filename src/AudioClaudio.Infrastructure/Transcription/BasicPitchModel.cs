using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AudioClaudio.Infrastructure.Transcription;

/// <summary>
/// Runs Spotify's Basic Pitch neural pitch-detection model (ICASSP 2022, Apache-2.0) via
/// Microsoft.ML.OnnxRuntime. The model maps one ~2 s window of 22 050 Hz mono audio
/// (<see cref="WindowSamples"/> = 43 844 samples) to three posteriorgrams — note-frames,
/// onsets, and pitch contour (<see cref="BasicPitchWindowOutput"/>). This is the polyphonic
/// front end: unlike YIN it estimates many simultaneous pitches per frame.
///
/// The ONNX input/output tensor names and the note-vs-onset output identity were established
/// empirically (a sustained 440 Hz tone lights up bin 48 = A4 across the whole window in
/// <c>StatefulPartitionedCall:1</c>, and only at the attack in <c>:2</c>). CPU inference is
/// deterministic for a given input on a given build; like the MeltySynth mixdown it is not
/// guaranteed bit-identical across CPU architectures (SIMD), which the project already accepts.
/// </summary>
public sealed class BasicPitchModel : IDisposable
{
    /// <summary>The model's fixed input sample rate.</summary>
    public const int SampleRateHz = 22050;

    /// <summary>Samples per model window: 22050·2 − 256 (FFT hop), i.e. ~1.99 s.</summary>
    public const int WindowSamples = 43844;

    /// <summary>Time frames produced per window (86 fps · 2 s).</summary>
    public const int FramesPerWindow = 172;

    /// <summary>Pitch bins per note-frame/onset output: the 88 piano keys.</summary>
    public const int PitchBins = 88;

    /// <summary>Contour bins: 88 keys × 3 bins per semitone.</summary>
    public const int ContourBins = 264;

    // Established from the exported SavedModel signature (see class remarks).
    private const string InputName = "serving_default_input_2:0";
    private const string NoteFramesOutputName = "StatefulPartitionedCall:1";
    private const string OnsetsOutputName = "StatefulPartitionedCall:2";
    private const string ContoursOutputName = "StatefulPartitionedCall:0";

    private readonly InferenceSession _session;

    public BasicPitchModel(string modelPath)
    {
        ArgumentNullException.ThrowIfNull(modelPath);
        _session = new InferenceSession(modelPath);
    }

    /// <summary>Runs one window of exactly <see cref="WindowSamples"/> mono samples at 22 050 Hz.</summary>
    public BasicPitchWindowOutput Run(ReadOnlySpan<float> window)
    {
        if (window.Length != WindowSamples)
        {
            throw new ArgumentException(
                $"Window must be exactly {WindowSamples} samples; got {window.Length}.", nameof(window));
        }

        var input = new DenseTensor<float>(new[] { 1, WindowSamples, 1 });
        for (int i = 0; i < WindowSamples; i++)
        {
            input[0, i, 0] = window[i];
        }

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
            _session.Run(new[] { NamedOnnxValue.CreateFromTensor(InputName, input) });

        float[,] notes = ExtractGrid(results, NoteFramesOutputName, FramesPerWindow, PitchBins);
        float[,] onsets = ExtractGrid(results, OnsetsOutputName, FramesPerWindow, PitchBins);
        float[,] contours = ExtractGrid(results, ContoursOutputName, FramesPerWindow, ContourBins);
        return new BasicPitchWindowOutput(notes, onsets, contours);
    }

    // Copies a [1, rows, cols] ONNX output tensor into a [rows, cols] grid (batch dim dropped).
    private static float[,] ExtractGrid(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, string name, int rows, int cols)
    {
        Tensor<float> tensor = results.First(r => r.Name == name).AsTensor<float>();
        var grid = new float[rows, cols];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                grid[r, c] = tensor[0, r, c];
            }
        }

        return grid;
    }

    public void Dispose() => _session.Dispose();
}
