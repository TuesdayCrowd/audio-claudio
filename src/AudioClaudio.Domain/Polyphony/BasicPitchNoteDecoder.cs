using System.Collections.Generic;

namespace AudioClaudio.Domain.Polyphony;

/// <summary>
/// Decodes Basic Pitch's frame/onset posteriorgrams into discrete note events — a faithful port
/// of the model's <c>output_to_notes_polyphonic</c> (Apache-2.0). The shape of the algorithm:
/// <list type="number">
/// <item>optionally augment predicted onsets with onsets <i>inferred</i> from sharp frame-energy
///   rises (<see cref="InferOnsets"/>);</item>
/// <item>keep only time-local peaks of the onset signal that clear the onset threshold;</item>
/// <item>walking onsets latest-first, start a note at each peak's pitch and run it forward until
///   that pitch band's frame energy stays below the frame threshold for <see cref="NoteDecoderOptions.EnergyTolerance"/>
///   frames, consuming ("zeroing") that band and its two neighbours so overtone smear does not
///   spawn duplicates;</item>
/// <item>the "melodia trick": repeatedly take the largest leftover frame energy and grow a note
///   both directions, catching sustained notes that never produced a clear onset.</item>
/// </list>
/// Pure and deterministic: same grids in, same notes out. Pitch bin <c>i</c> is MIDI <c>21 + i</c>.
/// </summary>
public static class BasicPitchNoteDecoder
{
    private const int MidiOffset = 21; // pitch bin 0 = A0 = MIDI 21

    public static IReadOnlyList<BasicPitchNote> Decode(
        float[,] frames, float[,] onsets, NoteDecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(onsets);
        ArgumentNullException.ThrowIfNull(options);

        int nFrames = frames.GetLength(0);
        int nBins = frames.GetLength(1);
        double onsetThresh = options.OnsetThreshold;
        double frameThresh = options.FrameThreshold;
        int minLen = options.MinNoteLenFrames;
        int energyTol = options.EnergyTolerance;
        int maxBin = nBins - 1;

        float[,] onsetSignal = options.InferOnsets ? InferredOnsets(onsets, frames) : onsets;

        // A copy of the frame energies we carve notes out of, so overtone bands and already-claimed
        // spans are not re-detected.
        var remaining = (float[,])frames.Clone();
        var notes = new List<BasicPitchNote>();

        // Peaks of the onset signal along time (strictly greater than both time-neighbours) that
        // clear the threshold — iterated LATEST-first (Basic Pitch's reverse ordering), so later
        // notes claim their energy before earlier ones.
        List<(int Frame, int Bin)> onsetPeaks = OnsetPeaks(onsetSignal, nFrames, nBins, onsetThresh);
        onsetPeaks.Sort((a, b) => a.Frame != b.Frame ? b.Frame - a.Frame : b.Bin - a.Bin);

        foreach ((int noteStart, int freq) in onsetPeaks)
        {
            if (noteStart >= nFrames - 1)
            {
                continue;
            }

            int end = WalkForwardToEnergyDrop(remaining, noteStart + 1, freq, nFrames, frameThresh, energyTol);
            if (end - noteStart <= minLen)
            {
                continue;
            }

            ClearBandAndNeighbours(remaining, noteStart, end, freq, maxBin);
            notes.Add(new BasicPitchNote(noteStart, end, freq + MidiOffset, Mean(frames, noteStart, end, freq)));
        }

        if (options.MelodiaTrick)
        {
            MelodiaSweep(frames, remaining, notes, nFrames, nBins, frameThresh, energyTol, minLen, maxBin);
        }

        return notes;
    }

    /// <summary>
    /// Onsets augmented with rises inferred from frame energy: for lags 1..2, the frame-to-frame
    /// increase, min-combined, negatives clamped to zero, rescaled to the onset signal's peak, then
    /// taken element-wise-max with the predicted onsets.
    /// </summary>
    private static float[,] InferredOnsets(float[,] onsets, float[,] frames, int nDiff = 2)
    {
        int t = frames.GetLength(0);
        int w = frames.GetLength(1);
        var frameDiff = new float[t, w];
        for (int i = 0; i < t; i++)
        {
            for (int j = 0; j < w; j++)
            {
                frameDiff[i, j] = float.MaxValue;
            }
        }

        for (int n = 1; n <= nDiff; n++)
        {
            for (int i = 0; i < t; i++)
            {
                for (int j = 0; j < w; j++)
                {
                    float prev = i - n >= 0 ? frames[i - n, j] : 0f;
                    float d = frames[i, j] - prev;
                    if (d < frameDiff[i, j])
                    {
                        frameDiff[i, j] = d;
                    }
                }
            }
        }

        float maxOnset = Max(onsets);
        float maxDiff = 0f;
        for (int i = 0; i < t; i++)
        {
            for (int j = 0; j < w; j++)
            {
                if (frameDiff[i, j] < 0f || i < nDiff)
                {
                    frameDiff[i, j] = 0f; // clamp negatives; zero the first nDiff rows
                }

                if (frameDiff[i, j] > maxDiff)
                {
                    maxDiff = frameDiff[i, j];
                }
            }
        }

        var result = new float[t, w];
        float scale = maxDiff > 0f ? maxOnset / maxDiff : 0f;
        for (int i = 0; i < t; i++)
        {
            for (int j = 0; j < w; j++)
            {
                float rescaled = frameDiff[i, j] * scale;
                result[i, j] = System.Math.Max(onsets[i, j], rescaled);
            }
        }

        return result;
    }

    private static List<(int Frame, int Bin)> OnsetPeaks(
        float[,] onsetSignal, int nFrames, int nBins, double onsetThresh)
    {
        var peaks = new List<(int, int)>();
        for (int t = 1; t < nFrames - 1; t++)
        {
            for (int b = 0; b < nBins; b++)
            {
                float v = onsetSignal[t, b];
                if (v >= onsetThresh && v > onsetSignal[t - 1, b] && v > onsetSignal[t + 1, b])
                {
                    peaks.Add((t, b));
                }
            }
        }

        return peaks;
    }

    // Walk forward from `from` until energy at `freq` stays below threshold for energyTol frames;
    // return the frame just past the note's real end (exclusive).
    private static int WalkForwardToEnergyDrop(
        float[,] remaining, int from, int freq, int nFrames, double frameThresh, int energyTol)
    {
        int i = from;
        int k = 0;
        while (i < nFrames - 1 && k < energyTol)
        {
            k = remaining[i, freq] < frameThresh ? k + 1 : 0;
            i++;
        }

        return i - k;
    }

    private static void ClearBandAndNeighbours(float[,] remaining, int start, int end, int freq, int maxBin)
    {
        for (int i = start; i < end; i++)
        {
            remaining[i, freq] = 0f;
            if (freq < maxBin)
            {
                remaining[i, freq + 1] = 0f;
            }

            if (freq > 0)
            {
                remaining[i, freq - 1] = 0f;
            }
        }
    }

    private static void MelodiaSweep(
        float[,] frames, float[,] remaining, List<BasicPitchNote> notes,
        int nFrames, int nBins, double frameThresh, int energyTol, int minLen, int maxBin)
    {
        while (true)
        {
            (int iMid, int freq, float peak) = ArgMax(remaining, nFrames, nBins);
            if (peak <= frameThresh)
            {
                break;
            }

            remaining[iMid, freq] = 0f;

            // forward pass
            int i = iMid + 1;
            int k = 0;
            while (i < nFrames - 1 && k < energyTol)
            {
                k = remaining[i, freq] < frameThresh ? k + 1 : 0;
                ClearCellAndNeighbours(remaining, i, freq, maxBin);
                i++;
            }

            int iEnd = i - 1 - k;

            // backward pass
            i = iMid - 1;
            k = 0;
            while (i > 0 && k < energyTol)
            {
                k = remaining[i, freq] < frameThresh ? k + 1 : 0;
                ClearCellAndNeighbours(remaining, i, freq, maxBin);
                i--;
            }

            int iStart = i + 1 + k;
            if (iEnd - iStart <= minLen)
            {
                continue;
            }

            notes.Add(new BasicPitchNote(iStart, iEnd, freq + MidiOffset, Mean(frames, iStart, iEnd, freq)));
        }
    }

    private static void ClearCellAndNeighbours(float[,] remaining, int i, int freq, int maxBin)
    {
        remaining[i, freq] = 0f;
        if (freq < maxBin)
        {
            remaining[i, freq + 1] = 0f;
        }

        if (freq > 0)
        {
            remaining[i, freq - 1] = 0f;
        }
    }

    // First (row-major: lowest frame, then lowest bin) location of the maximum value.
    private static (int Frame, int Bin, float Value) ArgMax(float[,] grid, int nFrames, int nBins)
    {
        int bestT = 0;
        int bestB = 0;
        float best = float.NegativeInfinity;
        for (int t = 0; t < nFrames; t++)
        {
            for (int b = 0; b < nBins; b++)
            {
                if (grid[t, b] > best)
                {
                    best = grid[t, b];
                    bestT = t;
                    bestB = b;
                }
            }
        }

        return (bestT, bestB, best);
    }

    // Mean of frames[start..end) at one bin.
    private static double Mean(float[,] frames, int start, int end, int bin)
    {
        double sum = 0.0;
        for (int i = start; i < end; i++)
        {
            sum += frames[i, bin];
        }

        return sum / (end - start);
    }

    private static float Max(float[,] grid)
    {
        float max = float.NegativeInfinity;
        foreach (float v in grid)
        {
            if (v > max)
            {
                max = v;
            }
        }

        return max;
    }
}
