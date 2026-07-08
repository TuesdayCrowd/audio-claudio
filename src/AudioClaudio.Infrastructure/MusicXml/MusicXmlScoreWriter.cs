using System.Text;
using AudioClaudio.Application.Ports;
using AudioClaudio.Domain;

namespace AudioClaudio.Infrastructure.MusicXml;

/// <summary>
/// Hand-rolled MusicXML 4.0 serializer for a monophonic <see cref="Score"/> (R11.3).
/// Single staff, clef chosen by pitch range, 4/4, deterministic byte-for-byte output:
/// LF newlines only and UTF-8 without a BOM (non-negotiable 3 — see <see cref="Nl"/>).
/// </summary>
public sealed class MusicXmlScoreWriter : IScoreWriter
{
    // Mean-pitch threshold for clef choice: strictly below middle C -> bass, otherwise treble
    // (so the tie at exactly MIDI 60 and an all-rests score both default to treble).
    private const int MiddleC = 60;

    // Explicit LF only. Environment.NewLine is CRLF on Windows and would break the
    // bit-for-bit determinism non-negotiable (CLAUDE.md §4, non-negotiable 3).
    private const string Nl = "\n";

    private readonly bool _includeNoteNames;
    private readonly string? _workTitle;

    /// <summary>
    /// <paramref name="includeNoteNames"/>: when true, every note carries its scientific-pitch name
    /// (e.g. C4, F#5) as a &lt;lyric&gt;, so a renderer such as OSMD prints it beneath the note — a
    /// learning/verification aid (`listen --note-names`). Default false keeps the plain golden output.
    /// <paramref name="workTitle"/>: when non-blank, emitted as the score's &lt;work-title&gt; (e.g. so
    /// a renderer such as OSMD shows it instead of "Untitled Score"). Default null/blank keeps the
    /// plain golden output.
    /// </summary>
    public MusicXmlScoreWriter(bool includeNoteNames = false, string? workTitle = null)
    {
        _includeNoteNames = includeNoteNames;
        _workTitle = string.IsNullOrWhiteSpace(workTitle) ? null : workTitle;
    }

    /// <summary>Serialize a score to UTF-8 (no BOM) MusicXML on the stream.</summary>
    public void Write(Score score, Stream destination)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(WriteToString(score));
        destination.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Serialize a score to a MusicXML 4.0 string (LF newlines).</summary>
    public string WriteToString(Score score)
    {
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
        sb.Append("      <part-name>Music</part-name>").Append(Nl);
        sb.Append("    </score-part>").Append(Nl);
        sb.Append("  </part-list>").Append(Nl);
        sb.Append("  <part id=\"P1\">").Append(Nl);

        var clef = ChooseClef(score);
        int divisions = score.Subdivision.TicksPerQuarter(); // MusicXML divisions per quarter note
        bool tiedFromPrevious = false;
        for (int i = 0; i < score.Measures.Count; i++)
        {
            AppendMeasure(sb, score, score.Measures[i], measureNumber: i + 1, isFirst: i == 0,
                          clef, divisions, ref tiedFromPrevious, _includeNoteNames);
        }

        sb.Append("  </part>").Append(Nl);
        sb.Append("</score-partwise>").Append(Nl);
        return sb.ToString();
    }

    // XML-escapes text content for the <work-title> element. Order matters: '&' must be escaped
    // first, or the '&' introduced by the later replacements would itself be re-escaped.
    private static string XmlEscape(string s) =>
        s.Replace("&", "&amp;")
         .Replace("<", "&lt;")
         .Replace(">", "&gt;")
         .Replace("\"", "&quot;")
         .Replace("'", "&apos;");

    private static void AppendMeasure(StringBuilder sb, Score score, Measure measure,
                                      int measureNumber, bool isFirst, Clef clef, int divisions,
                                      ref bool tiedFromPrevious, bool includeNoteNames)
    {
        sb.Append($"    <measure number=\"{measureNumber}\">").Append(Nl);
        if (isFirst)
        {
            sb.Append("      <attributes>").Append(Nl);
            sb.Append($"        <divisions>{divisions}</divisions>").Append(Nl);
            sb.Append("        <key>").Append(Nl);
            sb.Append("          <fifths>0</fifths>").Append(Nl);
            sb.Append("        </key>").Append(Nl);
            sb.Append("        <time>").Append(Nl);
            sb.Append($"          <beats>{score.TimeSignature.BeatsPerMeasure}</beats>").Append(Nl);
            sb.Append($"          <beat-type>{score.TimeSignature.BeatUnit}</beat-type>").Append(Nl);
            sb.Append("        </time>").Append(Nl);
            sb.Append("        <clef>").Append(Nl);
            sb.Append($"          <sign>{clef.Sign}</sign>").Append(Nl);
            sb.Append($"          <line>{clef.Line}</line>").Append(Nl);
            sb.Append("        </clef>").Append(Nl);
            sb.Append("      </attributes>").Append(Nl);
        }

        foreach (var element in measure.Elements)
        {
            AppendElement(sb, element, divisions, tiedFromPrevious, includeNoteNames);
            tiedFromPrevious = element.TiedToNext;
        }

        sb.Append("    </measure>").Append(Nl);
    }

    // Emits one ScoreElement. Its LengthTicks need NOT be a single standard note value: a note cut
    // at a barline (Step 6 TiedToNext) or a rest filling an odd gap can be e.g. 15 sixteenths. Such a
    // run is spelled as a sequence of standard values — tied notes for a note, consecutive rests for a
    // rest (Step 6 conserves the ticks; R11.1 leaves this notation-spelling to Step 11).
    // MusicXML <note> child order (partwise content model): (pitch|rest), duration, tie*, type, dot*, notations.
    private static void AppendElement(StringBuilder sb, ScoreElement element, int divisions, bool tiedFromPrevious,
                                      bool includeNoteNames)
    {
        var parts = Decompose(element.LengthTicks, divisions);
        bool isNote = element.Kind == ElementKind.Note && element.Pitch is Pitch;

        for (int p = 0; p < parts.Count; p++)
        {
            var (type, dotted, partTicks) = parts[p];
            // Notes tie in when the element is held from a previous measure (first part) or this part
            // continues the decomposed run (p > 0); tie out when held into the next measure (last part)
            // or more parts follow. Rests are never tied (MusicXML has no rest tie).
            bool tieIn = isNote && (p > 0 || tiedFromPrevious);
            bool tieOut = isNote && (p < parts.Count - 1 || element.TiedToNext);

            sb.Append("      <note>").Append(Nl);
            if (isNote)
            {
                var (step, alter, octave) = PitchToXml((Pitch)element.Pitch!);
                sb.Append("        <pitch>").Append(Nl);
                sb.Append($"          <step>{step}</step>").Append(Nl);
                if (alter != 0)
                {
                    sb.Append($"          <alter>{alter}</alter>").Append(Nl);
                }

                sb.Append($"          <octave>{octave}</octave>").Append(Nl);
                sb.Append("        </pitch>").Append(Nl);
            }
            else
            {
                sb.Append("        <rest/>").Append(Nl);
            }

            sb.Append($"        <duration>{partTicks}</duration>").Append(Nl);

            if (tieIn)
            {
                sb.Append("        <tie type=\"stop\"/>").Append(Nl);
            }

            if (tieOut)
            {
                sb.Append("        <tie type=\"start\"/>").Append(Nl);
            }

            sb.Append($"        <type>{type}</type>").Append(Nl);
            if (dotted)
            {
                sb.Append("        <dot/>").Append(Nl);
            }

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

            // Note-name lyric (opt-in): show the played note's scientific-pitch name once, at its onset —
            // the first decomposed part (p == 0) of an element that is NOT a tie continuation from a prior
            // measure. MusicXML note content model puts <lyric> after <notations>.
            if (includeNoteNames && isNote && p == 0 && !tiedFromPrevious)
            {
                var (step, alter, octave) = PitchToXml((Pitch)element.Pitch!);
                string name = $"{step}{(alter == 1 ? "#" : string.Empty)}{octave}";
                sb.Append("        <lyric number=\"1\">").Append(Nl);
                sb.Append("          <syllabic>single</syllabic>").Append(Nl);
                sb.Append($"          <text>{name}</text>").Append(Nl);
                sb.Append("        </lyric>").Append(Nl);
            }

            sb.Append("      </note>").Append(Nl);
        }
    }

    // MIDI number -> (step, chromatic alter, octave). Sharps only; octave n/12 - 1 => MIDI 60 is C4.
    private static (string Step, int Alter, int Octave) PitchToXml(Pitch pitch)
    {
        int n = pitch.MidiNumber;
        int pitchClass = ((n % 12) + 12) % 12;
        int octave = (n / 12) - 1;
        return pitchClass switch
        {
            0 => ("C", 0, octave),
            1 => ("C", 1, octave),
            2 => ("D", 0, octave),
            3 => ("D", 1, octave),
            4 => ("E", 0, octave),
            5 => ("F", 0, octave),
            6 => ("F", 1, octave),
            7 => ("G", 0, octave),
            8 => ("G", 1, octave),
            9 => ("A", 0, octave),
            10 => ("A", 1, octave),
            _ => ("B", 0, octave),
        };
    }

    // Standard note values in sixteenth-equivalents, largest first, each with its MusicXML type/dot.
    // divisions = Subdivision.TicksPerQuarter(); a sixteenth is divisions/4 ticks, so this table is
    // grid-independent. No run exceeds a whole note (16 sixteenths): a 4/4 barline splits every run
    // to at most one bar.
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

    // Decompose a length into standard note values, greedily largest-first, returning
    // (type, dotted, ticks) per part. A single standard value yields one part (so clean, barline-
    // aligned scores serialize exactly as before); a non-standard length (e.g. a 15/16 barline
    // segment) yields a tied-note / consecutive-rest run. Since the smallest value is a sixteenth,
    // every positive whole-sixteenth length is fully consumed.
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
            foreach (var (sixteenths, type, dotted) in StandardValues)
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

    // Range-based clef with fixed tie-breaks: mean == MiddleC -> treble; an all-rests score -> treble.
    private static Clef ChooseClef(Score score)
    {
        long sum = 0;
        int count = 0;
        foreach (var measure in score.Measures)
        {
            foreach (var element in measure.Elements)
            {
                if (element.Pitch is Pitch pitch)
                {
                    sum += pitch.MidiNumber;
                    count++;
                }
            }
        }

        if (count == 0)
        {
            return Clef.Treble;
        }

        double mean = (double)sum / count;
        return mean < MiddleC ? Clef.Bass : Clef.Treble;
    }

    private readonly record struct Clef(string Sign, int Line)
    {
        public static readonly Clef Treble = new("G", 2);
        public static readonly Clef Bass = new("F", 4);
    }
}
