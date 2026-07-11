using System;
using System.Collections.Generic;

namespace AudioClaudio.Domain;

/// <summary>
/// Infers a key signature (in fifths: sharps +, flats −) from pitch content by the classic
/// <b>Krumhansl-Schmuckler</b> key-finding algorithm. A 12-bin pitch-class profile (weighted by how
/// often each class sounds) is correlated against the Krumhansl-Kessler major and minor tonal
/// hierarchies rotated to all 12 tonics; the best-correlating of the 24 keys wins, and its key
/// signature is returned. Because relative major/minor share a signature, mode confusion (C major vs
/// A minor — identical pitch content) does not change the answer; only a fifth-related confusion
/// (picking the dominant/subdominant) would. Pure and deterministic (non-negotiable 3): the 24-key
/// scan is in a fixed order and ties break toward the fewest accidentals.
///
/// The result is a <b>declared-vs-detected</b> default: the CLI uses it when <c>--key</c> is omitted
/// (like auto-tempo), and <c>--key</c> overrides it. Feeds <see cref="PitchSpeller"/> and the emitted
/// <c>&lt;fifths&gt;</c>.
/// </summary>
public static class KeyDetector
{
    // Krumhansl-Kessler tonal-hierarchy profiles, index 0 = tonic. (Krumhansl 1990.)
    private static readonly double[] MajorProfile =
        { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };

    private static readonly double[] MinorProfile =
        { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };

    // Major key signature (fifths) for each tonic pitch class, choosing the conventional enharmonic
    // spelling (D♭ major = −5 not C♯ = +7; F♯ major = +6). A minor key takes its relative major's.
    private static readonly int[] MajorFifthsByTonic =
        { 0, -5, 2, -3, 4, -1, 6, 1, -4, 3, -2, 5 };

    /// <summary>Detect the key signature (fifths) of a set of sounding pitches. An empty set → 0 (C major
    /// / no accidentals). Weighting is by occurrence count.</summary>
    public static int Detect(IReadOnlyList<Pitch> pitches)
    {
        ArgumentNullException.ThrowIfNull(pitches);

        var profile = new double[12];
        foreach (Pitch p in pitches)
        {
            profile[(((p.MidiNumber % 12) + 12) % 12)] += 1.0;
        }

        return DetectFromProfile(profile);
    }

    /// <summary>Detect from a pre-built 12-bin pitch-class weight profile (index 0 = C). Public so a
    /// caller can weight by duration or velocity instead of raw count.</summary>
    public static int DetectFromProfile(IReadOnlyList<double> pitchClassWeights)
    {
        ArgumentNullException.ThrowIfNull(pitchClassWeights);
        if (pitchClassWeights.Count != 12)
        {
            throw new ArgumentException("A pitch-class profile must have exactly 12 bins.", nameof(pitchClassWeights));
        }

        double total = 0.0;
        for (int i = 0; i < 12; i++)
        {
            total += pitchClassWeights[i];
        }

        if (total <= 0.0)
        {
            return 0; // no pitch content → no accidentals
        }

        int bestFifths = 0;
        double bestCorrelation = double.NegativeInfinity;
        int bestAbsFifths = int.MaxValue;

        // Fixed scan order (tonic 0..11, major before minor) with a fewest-accidentals tie-break →
        // deterministic.
        for (int mode = 0; mode < 2; mode++)
        {
            double[] baseProfile = mode == 0 ? MajorProfile : MinorProfile;
            for (int tonic = 0; tonic < 12; tonic++)
            {
                double correlation = Correlation(pitchClassWeights, baseProfile, tonic);
                int fifths = mode == 0
                    ? MajorFifthsByTonic[tonic]
                    : MajorFifthsByTonic[(tonic + 3) % 12]; // minor → relative major's signature
                int absFifths = Math.Abs(fifths);

                if (correlation > bestCorrelation
                    || (correlation == bestCorrelation && absFifths < bestAbsFifths))
                {
                    bestCorrelation = correlation;
                    bestFifths = fifths;
                    bestAbsFifths = absFifths;
                }
            }
        }

        return bestFifths;
    }

    // Pearson correlation between the input profile and a base profile rotated so its tonic sits at
    // pitch class <paramref name="tonic"/>. Returns 0 when either series is flat (no discrimination).
    private static double Correlation(IReadOnlyList<double> input, double[] baseProfile, int tonic)
    {
        double inputMean = 0.0, profileMean = 0.0;
        for (int i = 0; i < 12; i++)
        {
            inputMean += input[i];
            profileMean += baseProfile[i];
        }

        inputMean /= 12.0;
        profileMean /= 12.0;

        double covariance = 0.0, inputVar = 0.0, profileVar = 0.0;
        for (int i = 0; i < 12; i++)
        {
            double di = input[i] - inputMean;
            double dp = baseProfile[(((i - tonic) % 12) + 12) % 12] - profileMean;
            covariance += di * dp;
            inputVar += di * di;
            profileVar += dp * dp;
        }

        double denominator = Math.Sqrt(inputVar * profileVar);
        return denominator > 0.0 ? covariance / denominator : 0.0;
    }
}
