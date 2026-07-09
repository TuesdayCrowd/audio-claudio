using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Polyphony;
using AudioClaudio.Infrastructure.MusicXml;
using Xunit;

namespace AudioClaudio.Tests.MusicXml;

/// <summary>
/// The polyphonic MusicXML writer: a two-staff piano part with chords. Structural checks (it is
/// well-formed, declares two staves + clefs, carries a chord, backs up between staves) plus the
/// load-bearing invariant — each staff advances exactly one bar per measure (sum of non-chord
/// note durations), which is bar conservation surfacing at the XML layer.
/// </summary>
public class GrandStaffMusicXmlWriterTests
{
    private static readonly SampleRate R = new(22050);
    private static readonly QuantizationGrid Grid =
        new(R, new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth);

    private static NoteEvent Note(int midi, long onset, long duration) =>
        new(new Pitch(midi), new SamplePosition(onset, R), new SampleDuration(duration, R));

    private static GrandStaffScore SampleScore()
    {
        var events = new List<NoteEvent>
        {
            Note(48, 0, 44100), Note(64, 0, 44100), Note(67, 0, 44100), // bar 1: bass C3 + treble chord E4/G4
            Note(72, 44100, 44100), Note(40, 44100, 44100),             // bar 2: treble C5, bass E2
        };
        return PolyphonicQuantizer.Quantize(events, Grid, new SampleDuration(1000, R));
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Emits_wellformed_grand_staff_with_two_staves_a_chord_and_a_backup()
    {
        string xml = new GrandStaffMusicXmlWriter().WriteToString(SampleScore());
        XDocument doc = XDocument.Parse(xml); // throws if not well-formed

        Assert.Contains("<staves>2</staves>", xml);
        Assert.Equal(2, doc.Descendants("clef").Count());                                  // treble + bass clef
        Assert.Contains(doc.Descendants("note"), n => n.Element("chord") is not null);     // a real chord
        Assert.NotEmpty(doc.Descendants("backup"));                                        // staff separation
        Assert.Contains(doc.Descendants("staff"), s => s.Value == "1");
        Assert.Contains(doc.Descendants("staff"), s => s.Value == "2");
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Each_staff_advances_exactly_one_bar_per_measure()
    {
        string xml = new GrandStaffMusicXmlWriter().WriteToString(SampleScore());
        XDocument doc = XDocument.Parse(xml);

        foreach (XElement measure in doc.Descendants("measure"))
        {
            // A voice's time advance = durations of its non-<chord> notes (chord notes don't advance time).
            int Advance(string staff) => measure.Elements("note")
                .Where(n => n.Element("chord") is null && n.Element("staff")?.Value == staff)
                .Sum(n => (int)n.Element("duration")!);

            int treble = Advance("1");
            int bass = Advance("2");
            Assert.Equal(treble, bass); // both staves span the same bar
            Assert.Equal(16, treble);   // one 4/4 bar = 16 duration units at divisions=4
        }
    }
}
