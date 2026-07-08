using AudioClaudio.Domain;
using AudioClaudio.Infrastructure.MusicXml;
using Xunit;

namespace AudioClaudio.Tests.MusicXml;

public class MusicXmlWriterTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void EmitsMusicXml40PartwiseDocumentHeader()
    {
        var score = MusicXmlFixtures.OneNote(new Pitch(60));

        var xml = new MusicXmlScoreWriter().WriteToString(score);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", xml);
        Assert.Contains(
            "<!DOCTYPE score-partwise PUBLIC \"-//Recordare//DTD MusicXML 4.0 Partwise//EN\" " +
            "\"http://www.musicxml.org/dtds/partwise.dtd\">",
            xml);
        Assert.Contains("<score-partwise version=\"4.0\">", xml);

        var doc = Xml.Parse(xml);
        Assert.Equal("P1", (string)doc.Descendants("score-part").Single().Attribute("id")!);
        Assert.Equal("Music", (string)doc.Descendants("score-part").Single().Element("part-name")!);
        Assert.Equal("P1", (string)doc.Descendants("part").Single().Attribute("id")!);
        Assert.Single(doc.Descendants("measure")); // one <measure> per bar
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void EmitsAttributesOnlyInTheFirstMeasure()
    {
        var score = new Score(new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth, new[]
        {
            new Measure(new[] { ScoreElement.Note(new Pitch(60), 64, 16) }),
            new Measure(new[] { ScoreElement.Note(new Pitch(62), 64, 16) }),
        });

        var doc = Xml.Parse(new MusicXmlScoreWriter().WriteToString(score));
        var measures = doc.Descendants("measure").ToArray();

        Assert.Equal(2, measures.Length);
        Assert.NotNull(measures[0].Element("attributes"));
        Assert.Null(measures[1].Element("attributes"));

        var attr = measures[0].Element("attributes")!;
        Assert.Equal("4", (string)attr.Element("divisions")!);
        Assert.Equal("0", (string)attr.Element("key")!.Element("fifths")!);
        Assert.Equal("4", (string)attr.Element("time")!.Element("beats")!);
        Assert.Equal("4", (string)attr.Element("time")!.Element("beat-type")!);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void ChoosesBassClefWhenMeanPitchBelowMiddleC()
    {
        // C2 (MIDI 36) and C3 (MIDI 48): mean 42 < 60 -> bass clef.
        var score = new Score(new Tempo(100), TimeSignature.FourFour, Subdivision.Sixteenth, new[]
        {
            new Measure(new[]
            {
                ScoreElement.Note(new Pitch(36), 64, 8),
                ScoreElement.Note(new Pitch(48), 64, 8),
            }),
        });

        var clef = Xml.Parse(new MusicXmlScoreWriter().WriteToString(score)).Descendants("clef").Single();

        Assert.Equal("F", (string)clef.Element("sign")!);
        Assert.Equal("4", (string)clef.Element("line")!);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void ChoosesTrebleClefWhenMeanPitchAtOrAboveMiddleC()
    {
        // C5 (MIDI 72) and E5 (MIDI 76): mean 74 >= 60 -> treble clef.
        var score = new Score(new Tempo(100), TimeSignature.FourFour, Subdivision.Sixteenth, new[]
        {
            new Measure(new[]
            {
                ScoreElement.Note(new Pitch(72), 64, 8),
                ScoreElement.Note(new Pitch(76), 64, 8),
            }),
        });

        var clef = Xml.Parse(new MusicXmlScoreWriter().WriteToString(score)).Descendants("clef").Single();

        Assert.Equal("G", (string)clef.Element("sign")!);
        Assert.Equal("2", (string)clef.Element("line")!);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(1, "16th", false)]
    [InlineData(2, "eighth", false)]
    [InlineData(3, "eighth", true)]
    [InlineData(4, "quarter", false)]
    [InlineData(6, "quarter", true)]
    [InlineData(8, "half", false)]
    [InlineData(12, "half", true)]
    [InlineData(16, "whole", false)]
    public void MapsDurationTicksToTypeDurationAndDot(int lengthTicks, string type, bool dotted)
    {
        // A serializer, not a validator: a single element need not fill the bar.
        // Grid is Subdivision.Sixteenth -> divisions = 4, so <duration> == LengthTicks.
        var score = new Score(new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth, new[]
        {
            new Measure(new[] { ScoreElement.Note(new Pitch(60), 64, lengthTicks) }),
        });

        var note = Xml.Parse(new MusicXmlScoreWriter().WriteToString(score)).Descendants("note").Single();

        Assert.Equal(type, (string)note.Element("type")!);
        Assert.Equal(lengthTicks.ToString(), (string)note.Element("duration")!);
        Assert.Equal(dotted, note.Element("dot") is not null);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void EmitsRestElementForRests()
    {
        var score = new Score(new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth, new[]
        {
            new Measure(new[] { ScoreElement.Rest(16) }),
        });

        var note = Xml.Parse(new MusicXmlScoreWriter().WriteToString(score)).Descendants("note").Single();

        Assert.NotNull(note.Element("rest"));
        Assert.Null(note.Element("pitch"));
        Assert.Equal("16", (string)note.Element("duration")!);
        Assert.Equal("whole", (string)note.Element("type")!);
    }

    [Theory]
    [Trait("Category", "Fast")]
    [InlineData(60, "C", null, 4)] // middle C
    [InlineData(61, "C", 1, 4)] // C#4
    [InlineData(70, "A", 1, 4)] // A#4 / Bb4
    [InlineData(21, "A", null, 0)] // A0, lowest key
    [InlineData(108, "C", null, 8)] // C8, highest key
    public void MapsMidiNumbersToStepAlterOctave(int midi, string step, int? alter, int octave)
    {
        var score = MusicXmlFixtures.OneNote(new Pitch(midi));

        var pitch = Xml.Parse(new MusicXmlScoreWriter().WriteToString(score)).Descendants("pitch").Single();

        Assert.Equal(step, (string)pitch.Element("step")!);
        Assert.Equal(octave.ToString(), (string)pitch.Element("octave")!);
        if (alter is null)
        {
            Assert.Null(pitch.Element("alter"));
        }
        else
        {
            Assert.Equal(alter.Value.ToString(), (string)pitch.Element("alter")!);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void OutputIsDeterministicAndUsesLfNewlines()
    {
        var writer = new MusicXmlScoreWriter();
        var score = MusicXmlFixtures.Twinkle();

        var first = writer.WriteToString(score);
        var second = writer.WriteToString(score);

        Assert.Equal(first, second); // same input -> identical output
        Assert.DoesNotContain("\r\n", first); // never CRLF (would break the golden in CI)
        Assert.DoesNotContain("\r", first); // no stray carriage returns at all
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void IncludesNoteNameLyricWhenOptedIn()
    {
        var score = MusicXmlFixtures.OneNote(new Pitch(60)); // middle C

        var xml = new MusicXmlScoreWriter(includeNoteNames: true).WriteToString(score);

        Assert.Contains("<lyric", xml);
        Assert.Contains("<text>C4</text>", xml);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void OmitsNoteNameLyricByDefault()
    {
        var score = MusicXmlFixtures.OneNote(new Pitch(60)); // middle C

        var xml = new MusicXmlScoreWriter().WriteToString(score);

        Assert.DoesNotContain("<lyric", xml);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void IncludesSharpInNoteNameLyricForSharpNote()
    {
        var score = MusicXmlFixtures.OneNote(new Pitch(61)); // C#4

        var xml = new MusicXmlScoreWriter(includeNoteNames: true).WriteToString(score);

        Assert.Contains("<text>C#4</text>", xml);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void EmitsTieStartAndStopForBarSplitNotes()
    {
        // A note tied across the barline: measure 1 ends with a tied-to-next C4,
        // measure 2 continues with the tied-from-previous segment.
        var score = new Score(new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth, new[]
        {
            new Measure(new[]
            {
                ScoreElement.Note(new Pitch(60), 64, 12),
                ScoreElement.Note(new Pitch(60), 64, 4, tiedToNext: true),
            }),
            new Measure(new[]
            {
                ScoreElement.Note(new Pitch(60), 64, 12),
                ScoreElement.Rest(4),
            }),
        });

        var notes = Xml.Parse(new MusicXmlScoreWriter().WriteToString(score)).Descendants("note").ToArray();

        // Second note (index 1): starts a tie.
        var startNote = notes[1];
        Assert.Equal("start", (string)startNote.Element("tie")!.Attribute("type")!);
        Assert.Equal("start", (string)startNote.Element("notations")!.Element("tied")!.Attribute("type")!);

        // Third note (index 2, first of measure 2): stops the tie.
        var stopNote = notes[2];
        Assert.Equal("stop", (string)stopNote.Element("tie")!.Attribute("type")!);
        Assert.Equal("stop", (string)stopNote.Element("notations")!.Element("tied")!.Attribute("type")!);
    }
}
