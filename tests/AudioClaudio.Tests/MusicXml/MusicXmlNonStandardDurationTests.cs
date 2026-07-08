using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.MusicXml;
using Xunit;

namespace AudioClaudio.Tests.MusicXml;

/// <summary>
/// Regression for the live-`listen` crash: a note cut at a barline (Step 6 structural tie) can leave a
/// NON-standard segment — e.g. a note starting one sixteenth into a 4/4 bar runs 15/16 to the barline —
/// which is not a single note value. The writer must spell such a run as a chain of standard values
/// (tied notes, or consecutive rests), not throw "No standard note type for 15 ticks".
/// </summary>
public class MusicXmlNonStandardDurationTests
{
    private const int Velocity = 64;

    // Bar 1: [16th rest, C4 held 15/16 and tied across the barline].
    // Bar 2: [C4 1/16 continuation, 15/16 rest]. Each bar conserves 16 ticks.
    private static Score BarlineCrossingScore() =>
        new(new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth, new[]
        {
            new Measure(new[]
            {
                ScoreElement.Rest(1),
                ScoreElement.Note(new Pitch(60), Velocity, 15, tiedToNext: true),
            }),
            new Measure(new[]
            {
                ScoreElement.Note(new Pitch(60), Velocity, 1),
                ScoreElement.Rest(15),
            }),
        });

    [Fact]
    [Trait("Category", "Fast")]
    public void NonStandardBarlineSegmentIsSpelledAsTiedStandardValues_WithoutThrowing()
    {
        // Before the fix this threw ArgumentOutOfRangeException on the 15-tick note.
        string xml = new MusicXmlScoreWriter().WriteToString(BarlineCrossingScore());

        XDocument doc = Xml.Parse(xml); // well-formed MusicXML

        // The 15/16 note decomposes to dotted-half (12) + dotted-eighth (3); with the 16th rest, the
        // 1/16 continuation, and the 15/16 rest (2 parts), that is six <note>s in all.
        var notes = doc.Descendants("note").ToList();
        Assert.Equal(6, notes.Count);

        // Bar-conservation at the XML level: every measure's durations sum to one 4/4 bar (16 ticks).
        foreach (var measure in doc.Descendants("measure"))
        {
            int sum = measure.Descendants("duration").Sum(d => int.Parse(d.Value, CultureInfo.InvariantCulture));
            Assert.Equal(16, sum);
        }

        // The 15/16 spelling is present: a dotted half tied to a dotted eighth.
        Assert.Contains(notes, n => (string?)n.Element("type") == "half" && n.Element("dot") is not null);
        Assert.Contains(notes, n => (string?)n.Element("type") == "eighth" && n.Element("dot") is not null);

        // The note is genuinely held across the barline: a tie starts and a tie stops.
        Assert.Contains(doc.Descendants("tie"), t => (string?)t.Attribute("type") == "start");
        Assert.Contains(doc.Descendants("tie"), t => (string?)t.Attribute("type") == "stop");

        // Every pitched note is the C4 that was played.
        foreach (var pitch in doc.Descendants("pitch"))
        {
            Assert.Equal("C", (string?)pitch.Element("step"));
            Assert.Equal("4", (string?)pitch.Element("octave"));
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void NonStandardRestIsSpelledAsConsecutiveRests_NeverTied()
    {
        var score = new Score(new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth, new[]
        {
            new Measure(new[]
            {
                ScoreElement.Note(new Pitch(60), Velocity, 1),
                ScoreElement.Rest(15),
            }),
        });

        XDocument doc = Xml.Parse(new MusicXmlScoreWriter().WriteToString(score));

        // 15/16 rest -> dotted-half rest + dotted-eighth rest (two rests), and rests are never tied.
        var rests = doc.Descendants("note").Where(n => n.Element("rest") is not null).ToList();
        Assert.Equal(2, rests.Count);
        Assert.All(rests, r => Assert.Null(r.Element("tie")));
        Assert.All(rests, r => Assert.Empty(r.Descendants("tied")));
        Assert.Equal(16, doc.Descendants("duration").Sum(d => int.Parse(d.Value, CultureInfo.InvariantCulture)));
    }
}
