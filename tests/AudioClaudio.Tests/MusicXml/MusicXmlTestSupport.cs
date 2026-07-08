using System.Xml;
using System.Xml.Linq;
using AudioClaudio.Domain;

namespace AudioClaudio.Tests.MusicXml;

/// <summary>Parse MusicXML that carries a DOCTYPE without fetching the external DTD.
/// XDocument.Parse defaults to DtdProcessing.Prohibit and would THROW on our DOCTYPE.</summary>
internal static class Xml
{
    internal static XDocument Parse(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader);
    }
}

/// <summary>Deterministic Score fixtures for the MusicXML writer tests.
/// Grid is Subdivision.Sixteenth, so divisions = 4: a quarter is 4 ticks, a whole 16.</summary>
internal static class MusicXmlFixtures
{
    private const int Velocity = 64;

    // One bar, one whole note (16 ticks) of the given pitch — fills a 4/4 bar exactly.
    internal static Score OneNote(Pitch pitch) =>
        new(new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth, new[]
        {
            new Measure(new[] { ScoreElement.Note(pitch, Velocity, 16) }),
        });

    // "Twinkle, twinkle" opening: two full 4/4 bars, treble range, ending on a rest.
    // C4 C4 G4 G4 | A4 A4 G4 (rest); every element a quarter = 4 ticks.
    internal static Score Twinkle() =>
        new(new Tempo(120), TimeSignature.FourFour, Subdivision.Sixteenth, new[]
        {
            new Measure(new[]
            {
                ScoreElement.Note(new Pitch(60), Velocity, 4),
                ScoreElement.Note(new Pitch(60), Velocity, 4),
                ScoreElement.Note(new Pitch(67), Velocity, 4),
                ScoreElement.Note(new Pitch(67), Velocity, 4),
            }),
            new Measure(new[]
            {
                ScoreElement.Note(new Pitch(69), Velocity, 4),
                ScoreElement.Note(new Pitch(69), Velocity, 4),
                ScoreElement.Note(new Pitch(67), Velocity, 4),
                ScoreElement.Rest(4),
            }),
        });
}
