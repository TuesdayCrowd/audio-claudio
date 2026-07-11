using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Polyphony;
using AudioClaudio.Infrastructure.MusicXml;
using Xunit;

namespace AudioClaudio.Tests.MusicXml;

/// <summary>
/// v2 Stage 3d — eighth-note triplets in the grand-staff MusicXML. A beat of three eighth-triplets is
/// engraved with a 3:2 time-modification on each and a start/stop tuplet bracket over the group; the score
/// stays well-formed and bar-conserving. The straight-grid output is unchanged (covered by the existing
/// GrandStaff writer golden).
/// </summary>
public class TripletNotationTests
{
    private const int Bpm = 120;
    private static readonly SampleRate Rate = new(44100);
    private const long SamplesPerBeat = 22050;            // 60/120 * 44100
    private const long TripletDur = SamplesPerBeat / 3;   // an eighth-note triplet = beat/3

    // One 4/4 bar: three eighth-triplets in beat 1, then three quarter notes (beats 2–4), all treble.
    private static GrandStaffScore TripletBar()
    {
        var grid = new QuantizationGrid(Rate, new Tempo(Bpm), TimeSignature.FourFour, Subdivision.Twelfth);
        var events = new List<NoteEvent>();
        for (int k = 0; k < 3; k++)
        {
            events.Add(new NoteEvent(new Pitch(72), new SamplePosition(k * TripletDur, Rate),
                new SampleDuration(TripletDur, Rate), 96));
        }

        for (int beat = 1; beat < 4; beat++)
        {
            events.Add(new NoteEvent(new Pitch(72), new SamplePosition(beat * SamplesPerBeat, Rate),
                new SampleDuration(SamplesPerBeat, Rate), 96));
        }

        return PolyphonicQuantizer.Quantize(events, grid, new SampleDuration(500, Rate));
    }

    private static string Xml() => new GrandStaffMusicXmlWriter().WriteToString(TripletBar());

    [Fact]
    [Trait("Category", "Fast")]
    public void TripletBeat_IsEngravedAsAThreeTwoTuplet()
    {
        string xml = Xml();
        XDocument doc = XDocument.Parse(xml); // well-formed

        Assert.Equal("12", doc.Descendants("divisions").Single().Value);

        // The three triplet notes: duration 4 ticks, type eighth, a 3:2 time-modification each.
        var triplets = doc.Descendants("note")
            .Where(n => n.Element("time-modification") is not null)
            .ToList();
        Assert.Equal(3, triplets.Count);
        Assert.All(triplets, n =>
        {
            Assert.Equal("4", n.Element("duration")!.Value);
            Assert.Equal("eighth", n.Element("type")!.Value);
            Assert.Equal("3", n.Element("time-modification")!.Element("actual-notes")!.Value);
            Assert.Equal("2", n.Element("time-modification")!.Element("normal-notes")!.Value);
        });

        // Exactly one start and one stop bracket, over the group.
        var tuplets = doc.Descendants("tuplet").ToList();
        Assert.Equal("start", tuplets.First().Attribute("type")!.Value);
        Assert.Equal("stop", tuplets.Last().Attribute("type")!.Value);
        Assert.Equal(2, tuplets.Count);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(13)]
    public void OddLengths_FromMessyInput_DecomposeExactly_AndStayWellFormed(int oddLen)
    {
        // A length that is neither a clean straight nor a clean triplet value (the fallback path). It must
        // spell exactly (durations sum to the element length) and never throw. Pad to a 48-tick bar.
        int ticksPerBar = 4 * Subdivision.Twelfth.TicksPerQuarter();
        var treble = new List<ChordElement>
        {
            ChordElement.Note(new[] { new Pitch(72) }, 96, oddLen),
            ChordElement.Rest(ticksPerBar - oddLen),
        };
        var bass = new List<ChordElement> { ChordElement.Rest(ticksPerBar) };
        var score = new GrandStaffScore(new Tempo(Bpm), TimeSignature.FourFour, Subdivision.Twelfth,
            new[] { new GrandStaffMeasure(treble, bass) });

        string xml = new GrandStaffMusicXmlWriter().WriteToString(score);
        XDocument doc = XDocument.Parse(xml); // well-formed, no throw

        // Every <note>'s <duration> on staff 1 must sum to the full bar (the odd length spelled exactly).
        int trebleDuration = doc.Descendants("note")
            .Where(n => n.Element("staff")!.Value == "1")
            .Sum(n => int.Parse(n.Element("duration")!.Value));
        Assert.Equal(ticksPerBar, trebleDuration);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void TripletBar_ConservesTheBar_PerStaff()
    {
        GrandStaffScore score = TripletBar();
        GrandStaffMeasure m = Assert.Single(score.Measures);
        int ticksPerBar = 4 * Subdivision.Twelfth.TicksPerQuarter(); // 48

        // 3 triplet-eighths (4) + 3 quarters (12) = 12 + 36 = 48; bass all rests = 48.
        Assert.Equal(ticksPerBar, m.Treble.Sum(e => e.LengthTicks));
        Assert.Equal(ticksPerBar, m.Bass.Sum(e => e.LengthTicks));
        Assert.Equal(3, m.Treble.Count(e => e.Kind == ElementKind.Note && e.LengthTicks == 4)); // the triplets
    }
}
