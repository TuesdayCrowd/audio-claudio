namespace AudioClaudio.Domain;

/// <summary>
/// Key-signature-aware enharmonic spelling: a MIDI number → a notated (step, alter, octave). A MIDI
/// number alone is 12-fold ambiguous (MIDI 61 is C♯ in D major but D♭ in A♭ major), so correct
/// engraving needs the key. The rule is the classic <b>line of fifths, nearest to the key centre</b>:
/// every pitch has a position on the chain of fifths (…F♭ C♭ G♭ D♭ A♭ E♭ B♭ F C G D A E B F♯ C♯…),
/// and a key with <paramref name="fifths"/> sharps(+)/flats(−) centres its diatonic band there. Each
/// pitch class is spelled by the representative nearest that centre — so diatonic notes come out
/// natural, and chromatics come out in the key's own accidental direction (A♭ major → D♭/E♭/G♭/A♭/B♭,
/// D major → C♯/F♯/G♯). Ties (a pitch equidistant on both sides) prefer the spelling with fewer
/// accidentals, keeping naturals natural. Pure and deterministic.
/// </summary>
public static class PitchSpeller
{
    // Step letters in fifths order, indexed by ((p+1) mod 7): p=−1→F, 0→C, 1→G, 2→D, 3→A, 4→E, 5→B.
    private static readonly string[] FifthsLetters = { "F", "C", "G", "D", "A", "E", "B" };

    /// <param name="fifths">Key signature: sharps positive, flats negative (A♭ major = −4, D major = +2).</param>
    public static (string Step, int Alter, int Octave) Spell(int midiNumber, int fifths)
    {
        int pitchClass = (((midiNumber % 12) + 12) % 12);
        int baseIndex = (7 * pitchClass) % 12;   // a line-of-fifths index in [0,11] with this pitch class
        int centre = fifths + 2;                 // centre of the diatonic band [fifths−1, fifths+5]

        // Among the representatives of this pitch class (12 apart on the line of fifths), take the one
        // nearest the key centre; on a tie, the one with fewer accidentals.
        int bestIndex = baseIndex;
        int bestDistance = int.MaxValue;
        int bestAbsAlter = int.MaxValue;
        for (int m = -1; m <= 1; m++)
        {
            int index = baseIndex + (12 * m);
            int distance = System.Math.Abs(index - centre);
            int absAlter = System.Math.Abs(AlterOf(index));
            if (distance < bestDistance || (distance == bestDistance && absAlter < bestAbsAlter))
            {
                bestIndex = index;
                bestDistance = distance;
                bestAbsAlter = absAlter;
            }
        }

        string step = FifthsLetters[(((bestIndex + 1) % 7) + 7) % 7];
        int alter = AlterOf(bestIndex);
        // The octave follows the *letter*, not the raw pitch class, so C♭4 (MIDI 59) sits in octave 4.
        int octave = ((midiNumber - alter - NaturalSemitone(step)) / 12) - 1;
        return (step, alter, octave);
    }

    // Accidental of a line-of-fifths index: 0 for the seven naturals (F..B), +1 per further sharp, −1 per flat.
    private static int AlterOf(int index) => (int)System.Math.Floor((index + 1) / 7.0);

    private static int NaturalSemitone(string step) => step switch
    {
        "C" => 0,
        "D" => 2,
        "E" => 4,
        "F" => 5,
        "G" => 7,
        "A" => 9,
        _ => 11, // B
    };
}
