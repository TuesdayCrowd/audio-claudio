using System.Collections.Generic;
using System.Text;
using AudioClaudio.Domain;
using AudioClaudio.Domain.Polyphony;

namespace AudioClaudio.Infrastructure.MusicXml;

/// <summary>
/// Hand-rolled MusicXML 4.0 serializer for a polyphonic <see cref="GrandStaffScore"/>: one piano
/// part with two staves (treble = voice 1 / staff 1, bass = voice 2 / staff 2), chords emitted via
/// <c>&lt;chord/&gt;</c>, and a <c>&lt;backup&gt;</c> rewinding the cursor between the staves.
/// Deterministic byte-for-byte output (LF newlines, UTF-8 without BOM). The monophonic
/// <see cref="MusicXmlScoreWriter"/> is untouched; the shared note-value/pitch spelling is small and
/// duplicated here on purpose so that writer's golden cannot be disturbed.
/// </summary>
public sealed class GrandStaffMusicXmlWriter
{
    private const string Nl = "\n";

    private readonly bool _includeNoteNames;
    private readonly string? _workTitle;
    private readonly int _fifths;

    /// <param name="fifths">Key signature: sharps positive, flats negative (A♭ major = −4). Drives both
    /// the emitted <c>&lt;fifths&gt;</c> and the enharmonic spelling of every pitch (<see cref="PitchSpeller"/>).</param>
    public GrandStaffMusicXmlWriter(bool includeNoteNames = false, string? workTitle = null, int fifths = 0)
    {
        _includeNoteNames = includeNoteNames;
        _workTitle = string.IsNullOrWhiteSpace(workTitle) ? null : workTitle;
        _fifths = fifths;
    }

    /// <summary>Serialize to UTF-8 (no BOM) MusicXML on the stream.</summary>
    public void Write(GrandStaffScore score, Stream destination)
    {
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(WriteToString(score));
        destination.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Serialize to a MusicXML 4.0 string (LF newlines).</summary>
    public string WriteToString(GrandStaffScore score)
    {
        ArgumentNullException.ThrowIfNull(score);

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>").Append(Nl);
        sb.Append("<!DOCTYPE score-partwise PUBLIC \"-//Recordare//DTD MusicXML 4.0 Partwise//EN\" " +
                  "\"http://www.musicxml.org/dtds/partwise.dtd\">").Append(Nl);
        sb.Append("<score-partwise version=\"4.0\">").Append(Nl);
        if (_workTitle is not null)
        {
            sb.Append("  <work>").Append(Nl);
            sb.Append($"    <work-title>{XmlEscape(_workTitle)}</work-title>").Append(Nl);
            sb.Append("  </work>").Append(Nl);
        }

        sb.Append("  <part-list>").Append(Nl);
        sb.Append("    <score-part id=\"P1\">").Append(Nl);
        sb.Append("      <part-name>Piano</part-name>").Append(Nl);
        sb.Append("    </score-part>").Append(Nl);
        sb.Append("  </part-list>").Append(Nl);
        sb.Append("  <part id=\"P1\">").Append(Nl);

        int divisions = score.Subdivision.TicksPerQuarter();
        bool tiedTreble = false;
        bool tiedBass = false;
        for (int i = 0; i < score.Measures.Count; i++)
        {
            AppendMeasure(sb, score, score.Measures[i], measureNumber: i + 1, isFirst: i == 0,
                          divisions, ref tiedTreble, ref tiedBass);
        }

        sb.Append("  </part>").Append(Nl);
        sb.Append("</score-partwise>").Append(Nl);
        return sb.ToString();
    }

    private void AppendMeasure(StringBuilder sb, GrandStaffScore score, GrandStaffMeasure measure,
                               int measureNumber, bool isFirst, int divisions,
                               ref bool tiedTreble, ref bool tiedBass)
    {
        sb.Append($"    <measure number=\"{measureNumber}\">").Append(Nl);
        if (isFirst)
        {
            sb.Append("      <attributes>").Append(Nl);
            sb.Append($"        <divisions>{divisions}</divisions>").Append(Nl);
            sb.Append("        <key>").Append(Nl);
            sb.Append($"          <fifths>{_fifths}</fifths>").Append(Nl);
            sb.Append("        </key>").Append(Nl);
            sb.Append("        <time>").Append(Nl);
            sb.Append($"          <beats>{score.TimeSignature.BeatsPerMeasure}</beats>").Append(Nl);
            sb.Append($"          <beat-type>{score.TimeSignature.BeatUnit}</beat-type>").Append(Nl);
            sb.Append("        </time>").Append(Nl);
            sb.Append("        <staves>2</staves>").Append(Nl);
            sb.Append("        <clef number=\"1\">").Append(Nl);
            sb.Append("          <sign>G</sign>").Append(Nl);
            sb.Append("          <line>2</line>").Append(Nl);
            sb.Append("        </clef>").Append(Nl);
            sb.Append("        <clef number=\"2\">").Append(Nl);
            sb.Append("          <sign>F</sign>").Append(Nl);
            sb.Append("          <line>4</line>").Append(Nl);
            sb.Append("        </clef>").Append(Nl);
            sb.Append("      </attributes>").Append(Nl);
        }

        int trebleDuration = 0;
        foreach (ChordElement element in measure.Treble)
        {
            trebleDuration += AppendChordElement(sb, element, voice: 1, staff: 1, divisions, tiedTreble);
            tiedTreble = element.TiedToNext;
        }

        // Rewind to the start of the measure, then lay the bass staff (bar conservation makes the
        // treble's total the measure length).
        sb.Append("      <backup>").Append(Nl);
        sb.Append($"        <duration>{trebleDuration}</duration>").Append(Nl);
        sb.Append("      </backup>").Append(Nl);

        foreach (ChordElement element in measure.Bass)
        {
            AppendChordElement(sb, element, voice: 2, staff: 2, divisions, tiedBass);
            tiedBass = element.TiedToNext;
        }

        sb.Append("    </measure>").Append(Nl);
    }

    // Emits one chord/rest element on one staff, returning the duration units it advanced. A chord's
    // pitches are emitted as sibling <note>s, the 2nd onward carrying <chord/>; a non-standard length
    // is spelled as a tied run of standard values (chords tie note-for-note).
    private int AppendChordElement(StringBuilder sb, ChordElement element, int voice, int staff,
                                   int divisions, bool tiedFromPrevious)
    {
        List<(string Type, bool Dotted, int Ticks)> parts = Decompose(element.LengthTicks, divisions);
        bool isNote = element.Kind == ElementKind.Note;
        int duration = 0;

        for (int p = 0; p < parts.Count; p++)
        {
            (string type, bool dotted, int partTicks) = parts[p];
            bool tieIn = isNote && (p > 0 || tiedFromPrevious);
            bool tieOut = isNote && (p < parts.Count - 1 || element.TiedToNext);

            if (isNote)
            {
                for (int j = 0; j < element.Pitches.Count; j++)
                {
                    AppendNote(sb, element.Pitches[j], isChordMember: j > 0, partTicks, tieIn, tieOut,
                               voice, type, dotted, staff,
                               includeName: _includeNoteNames && j == 0 && p == 0 && !tiedFromPrevious);
                }
            }
            else
            {
                AppendRest(sb, partTicks, voice, type, dotted, staff);
            }

            duration += partTicks;
        }

        return duration;
    }

    // <note> content order (MusicXML DTD): chord?, (pitch|rest), duration, tie*, voice, type, dot*, staff, notations*, lyric*.
    private void AppendNote(StringBuilder sb, Pitch pitch, bool isChordMember, int durationTicks,
                            bool tieIn, bool tieOut, int voice, string type, bool dotted, int staff, bool includeName)
    {
        sb.Append("      <note>").Append(Nl);
        if (isChordMember)
        {
            sb.Append("        <chord/>").Append(Nl);
        }

        (string step, int alter, int octave) = PitchSpeller.Spell(pitch.MidiNumber, _fifths);
        sb.Append("        <pitch>").Append(Nl);
        sb.Append($"          <step>{step}</step>").Append(Nl);
        if (alter != 0)
        {
            sb.Append($"          <alter>{alter}</alter>").Append(Nl);
        }

        sb.Append($"          <octave>{octave}</octave>").Append(Nl);
        sb.Append("        </pitch>").Append(Nl);
        sb.Append($"        <duration>{durationTicks}</duration>").Append(Nl);
        if (tieIn)
        {
            sb.Append("        <tie type=\"stop\"/>").Append(Nl);
        }

        if (tieOut)
        {
            sb.Append("        <tie type=\"start\"/>").Append(Nl);
        }

        sb.Append($"        <voice>{voice}</voice>").Append(Nl);
        sb.Append($"        <type>{type}</type>").Append(Nl);
        if (dotted)
        {
            sb.Append("        <dot/>").Append(Nl);
        }

        sb.Append($"        <staff>{staff}</staff>").Append(Nl);
        if (tieIn || tieOut)
        {
            sb.Append("        <notations>").Append(Nl);
            if (tieIn)
            {
                sb.Append("          <tied type=\"stop\"/>").Append(Nl);
            }

            if (tieOut)
            {
                sb.Append("          <tied type=\"start\"/>").Append(Nl);
            }

            sb.Append("        </notations>").Append(Nl);
        }

        if (includeName)
        {
            string name = $"{step}{Accidental(alter)}{octave}";
            sb.Append("        <lyric number=\"1\">").Append(Nl);
            sb.Append("          <syllabic>single</syllabic>").Append(Nl);
            sb.Append($"          <text>{name}</text>").Append(Nl);
            sb.Append("        </lyric>").Append(Nl);
        }

        sb.Append("      </note>").Append(Nl);
    }

    private void AppendRest(StringBuilder sb, int durationTicks, int voice, string type, bool dotted, int staff)
    {
        sb.Append("      <note>").Append(Nl);
        sb.Append("        <rest/>").Append(Nl);
        sb.Append($"        <duration>{durationTicks}</duration>").Append(Nl);
        sb.Append($"        <voice>{voice}</voice>").Append(Nl);
        sb.Append($"        <type>{type}</type>").Append(Nl);
        if (dotted)
        {
            sb.Append("        <dot/>").Append(Nl);
        }

        sb.Append($"        <staff>{staff}</staff>").Append(Nl);
        sb.Append("      </note>").Append(Nl);
    }

    private static string XmlEscape(string s) =>
        s.Replace("&", "&amp;")
         .Replace("<", "&lt;")
         .Replace(">", "&gt;")
         .Replace("\"", "&quot;")
         .Replace("'", "&apos;");

    // Accidental suffix for a scientific-pitch name lyric (ASCII, matching the "F#5" convention): a
    // double-flat "bb" through double-sharp "##".
    private static string Accidental(int alter) => alter switch
    {
        <= -2 => "bb",
        -1 => "b",
        0 => "",
        1 => "#",
        _ => "##",
    };

    private static readonly (int Sixteenths, string Type, bool Dotted)[] StandardValues =
    {
        (16, "whole", false),
        (12, "half", true),
        (8, "half", false),
        (6, "quarter", true),
        (4, "quarter", false),
        (3, "eighth", true),
        (2, "eighth", false),
        (1, "16th", false),
    };

    private static List<(string Type, bool Dotted, int Ticks)> Decompose(int lengthTicks, int divisions)
    {
        if (lengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthTicks), lengthTicks, "Element length must be positive.");
        }

        int remainingSixteenths = lengthTicks * 4 / divisions;
        if (remainingSixteenths * divisions / 4 != lengthTicks)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthTicks), lengthTicks,
                $"Length {lengthTicks} at {divisions} divisions/quarter is not a whole number of sixteenths.");
        }

        var parts = new List<(string Type, bool Dotted, int Ticks)>();
        while (remainingSixteenths > 0)
        {
            foreach ((int sixteenths, string type, bool dotted) in StandardValues)
            {
                if (sixteenths <= remainingSixteenths)
                {
                    parts.Add((type, dotted, sixteenths * divisions / 4));
                    remainingSixteenths -= sixteenths;
                    break;
                }
            }
        }

        return parts;
    }
}
