using System;
using AudioClaudio.Infrastructure.Transcription;
using AudioClaudio.Tests.TestSupport;
using Xunit;

namespace AudioClaudio.Tests.Infrastructure;

/// <summary>
/// Proves the Basic Pitch ONNX model runs under Microsoft.ML.OnnxRuntime in this codebase and
/// that our reading of its contract is right: a pure 440 Hz tone (A4 = MIDI 69 = pitch bin 48)
/// must light up bin 48 across the note-frames posteriorgram and fire an onset near the attack.
/// This is the polyphony front end's foundation — if it fails, nothing downstream can work.
/// </summary>
public class BasicPitchModelTests
{
    private const int A4Bin = 48; // MIDI 69 - MIDI_OFFSET 21

    [Fact]
    [Trait("Category", "Slow")] // loads the ONNX model + runs inference
    public void Recognizes_a_440Hz_sine_as_A4_across_the_window()
    {
        // A 2 s A4 tone at the model's own 22 050 Hz rate, exactly one model window (43 844 samples).
        var window = new float[BasicPitchModel.WindowSamples];
        for (int i = 0; i < window.Length; i++)
        {
            window[i] = 0.5f * MathF.Sin(2f * MathF.PI * 440f * i / BasicPitchModel.SampleRateHz);
        }

        using var model = new BasicPitchModel(RepoPaths.Fixture("models", "basic-pitch-nmp.onnx"));
        BasicPitchWindowOutput o = model.Run(window);

        // Shapes match the documented contract.
        Assert.Equal(BasicPitchModel.FramesPerWindow, o.NoteFrames.GetLength(0)); // 172 frames
        Assert.Equal(BasicPitchModel.PitchBins, o.NoteFrames.GetLength(1));       // 88 pitches
        Assert.Equal(BasicPitchModel.FramesPerWindow, o.Onsets.GetLength(0));
        Assert.Equal(BasicPitchModel.ContourBins, o.Contours.GetLength(1));       // 264 contour bins

        // Note-frames: A4 is THE pitch, sustained across most of the window.
        Assert.Equal(A4Bin, DominantBin(o.NoteFrames));
        Assert.True(FramesActive(o.NoteFrames, A4Bin, 0.3f) > 100,
            "A sustained A4 should activate bin 48 across most of the 172 frames.");

        // Onsets: bin 48 fires, but only near the attack (a handful of frames, not the whole tone).
        int onsetFrames = FramesActive(o.Onsets, A4Bin, 0.3f);
        Assert.True(onsetFrames is > 0 and < 30,
            $"An onset should be a brief attack, not a sustain; got {onsetFrames} active frames.");
    }

    // Index of the pitch bin with the greatest mean activation over time.
    private static int DominantBin(float[,] grid)
    {
        int frames = grid.GetLength(0);
        int bins = grid.GetLength(1);
        int best = 0;
        double bestMean = double.NegativeInfinity;
        for (int b = 0; b < bins; b++)
        {
            double sum = 0.0;
            for (int f = 0; f < frames; f++)
            {
                sum += grid[f, b];
            }

            double mean = sum / frames;
            if (mean > bestMean)
            {
                bestMean = mean;
                best = b;
            }
        }

        return best;
    }

    // Number of frames where the given bin's activation exceeds the threshold.
    private static int FramesActive(float[,] grid, int bin, float threshold)
    {
        int frames = grid.GetLength(0);
        int count = 0;
        for (int f = 0; f < frames; f++)
        {
            if (grid[f, bin] > threshold)
            {
                count++;
            }
        }

        return count;
    }
}
