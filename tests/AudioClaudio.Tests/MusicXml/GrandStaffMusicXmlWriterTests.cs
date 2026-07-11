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
    public void Emits_the_declared_key_and_spells_pitches_for_it()
    {
        // A♭ major (4 flats): MIDI 56/68 are the tonic A♭ — they must engrave as A♭ (step A, alter −1),
        // never G♯, and the key signature must read −4. The old writer hard-coded fifths 0 and sharps,
        // so this asserts behaviour the pre-4c writer could not produce.
        var events = new List<NoteEvent> { Note(56, 0, 44100), Note(68, 0, 44100) }; // bass A♭2 + treble A♭4
        GrandStaffScore score = PolyphonicQuantizer.Quantize(events, Grid, new SampleDuration(1000, R));

        string xml = new GrandStaffMusicXmlWriter(fifths: -4).WriteToString(score);
        XDocument doc = XDocument.Parse(xml);

        Assert.Contains("<fifths>-4</fifths>", xml);
        Assert.Contains(doc.Descendants("note"), n =>
            n.Element("pitch")?.Element("step")?.Value == "A" &&
            n.Element("pitch")?.Element("alter")?.Value == "-1"); // A♭, a flat
        Assert.DoesNotContain(doc.Descendants("note"), n =>
            n.Element("pitch")?.Element("step")?.Value == "G" &&
            n.Element("pitch")?.Element("alter")?.Value == "1");  // never G♯

        // The scientific-pitch lyric renders the flat too (as "b").
        string named = new GrandStaffMusicXmlWriter(includeNoteNames: true, fifths: -4).WriteToString(score);
        Assert.Contains("<text>Ab4</text>", named);
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

    [Fact]
    [Trait("Category", "Fast")]
    public void Emits_dynamic_marks_when_the_velocity_level_changes()
    {
        var events = new List<NoteEvent>
        {
            new(new Pitch(60), new SamplePosition(0, R), new SampleDuration(44100, R), 100),     // bar 1: loud → ff
            new(new Pitch(62), new SamplePosition(44100, R), new SampleDuration(44100, R), 40),  // bar 2: soft → p
        };
        GrandStaffScore score = PolyphonicQuantizer.Quantize(events, Grid, new SampleDuration(1000, R));

        XDocument doc = XDocument.Parse(new GrandStaffMusicXmlWriter().WriteToString(score));
        var marks = doc.Descendants("dynamics").SelectMany(d => d.Elements().Select(e => e.Name.LocalName)).ToList();

        Assert.Contains("ff", marks); // velocity 100
        Assert.Contains("p", marks);  // velocity 40
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Emits_pedal_marks_from_pedal_changes()
    {
        // SampleScore is two 4/4 bars (16 ticks each at divisions=4). Pedal down at tick 0 (bar 1),
        // up at tick 20 (bar 2, offset 4).
        var pedal = new (int Tick, bool Down)[] { (0, true), (20, false) };

        string xml = new GrandStaffMusicXmlWriter().WriteToString(SampleScore(), pedal);
        XDocument doc = XDocument.Parse(xml);

        var types = doc.Descendants("pedal").Select(p => p.Attribute("type")?.Value).ToList();
        Assert.Contains("start", types); // pedal down
        Assert.Contains("stop", types);  // pedal up
    }
}
