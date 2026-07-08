using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.MusicXml;
using CsCheck;
using Xunit;

namespace AudioClaudio.Tests.MusicXml;

/// <summary>
/// The bar-level trial balance for the MusicXML writer (R11.1): every emitted
/// measure's note+rest &lt;duration&gt; values sum exactly to the time signature,
/// for randomly generated valid scores.
/// </summary>
public class MusicXmlBarConservationTests
{
    private const int Velocity = 64;
    private const int BarTicks = 16; // 4 beats * 4 sixteenth-ticks per quarter, a full 4/4 bar at Subdivision.Sixteenth

    // Standard note-value lengths in ticks (divisions = 4): 16th..whole incl. dotted.
    private static readonly int[] StandardTicks = { 1, 2, 3, 4, 6, 8, 12, 16 };

    // A pitch in the MVP detection range (MIDI 33..96), or null for a rest.
    private static readonly Gen<Pitch?> GenPitchOrRest =
        Gen.Frequency(
            (1, Gen.Const((Pitch?)null)),
            (4, Gen.Int[33, 96].Select(n => (Pitch?)new Pitch(n))));

    // Elements whose tick-durations sum to exactly `remaining`. Base list is never
    // mutated (Enumerable.Prepend + ToList allocate fresh), so sharing Gen.Const is safe.
    private static Gen<List<ScoreElement>> GenElements(int remaining)
    {
        if (remaining == 0)
        {
            return Gen.Const(new List<ScoreElement>());
        }

        var fitting = StandardTicks.Where(t => t <= remaining).ToArray();
        return
            from t in Gen.OneOfConst(fitting)
            from p in GenPitchOrRest
            from rest in GenElements(remaining - t)
            select rest.Prepend(
                p is Pitch pitch ? ScoreElement.Note(pitch, Velocity, t) : ScoreElement.Rest(t)).ToList();
    }

    private static readonly Gen<Measure> GenMeasure =
        GenElements(BarTicks).Select(els => new Measure(els));

    private static readonly Gen<Score> GenScore =
        from measureCount in Gen.Int[1, 4]
        from tempo in Gen.Int[60, 140]
        from measures in GenMeasure.List[measureCount]
        select new Score(new Tempo(tempo), TimeSignature.FourFour, Subdivision.Sixteenth, measures);

    [Fact]
    [Trait("Category", "Slow")]
    public void EveryMeasureSumsToTheTimeSignature()
    {
        GenScore.Sample(score =>
        {
            var xml = new MusicXmlScoreWriter().WriteToString(score);
            var doc = Xml.Parse(xml);

            foreach (var measure in doc.Descendants("measure"))
            {
                var sum = measure.Elements("note").Sum(note => (int)note.Element("duration")!);
                Assert.Equal(BarTicks, sum);
            }
        }, iter: 1000);
    }
}
